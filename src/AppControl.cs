using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace OneCWhatchdog
{
    sealed class AppControlSettings
    {
        // Разрешённые программы: всё, чего нет в списке, — нарушение.
        public bool Enabled;
        /// <summary>true — завершать программы не из списка, false — только записывать в журнал.</summary>
        public bool Block;
        public List<string> Allowed = new List<string>();

        // Запрещённые программы: нарушение — то, что есть в списке.
        public bool DenyEnabled;
        public bool DenyBlock;
        public List<string> Denied = new List<string>();
    }

    sealed class AppControlEventArgs : EventArgs
    {
        public AppControlEventArgs(string message, bool alert)
        {
            Message = message;
            Alert = alert;
        }

        public string Message { get; private set; }
        public bool Alert { get; private set; }
    }

    /// <summary>
    /// Контроль запуска программ. Раз в секунду просматривает процессы в сеансах пользователей
    /// (все, кроме сеанса служб 0) и сверяет имя exe со списками.
    ///   Разрешённые: программы из папок Windows, Защитника Windows и WebView2 (их использует
    ///   сама Windows — поиск в «Пуске», виджеты) разрешены всегда — туда обычный пользователь
    ///   ничего положить не может.
    ///   Запрещённые: действуют и на программы из папки Windows (cmd.exe, regedit.exe…)
    ///   и важнее списка разрешённых.
    /// Никогда не трогаем процессы служебных учётных записей и саму программу.
    /// </summary>
    sealed class AppControl : IDisposable
    {
        const int PollIntervalMs = 1000;
        static readonly TimeSpan BlockReportInterval = TimeSpan.FromMinutes(1);

        static readonly string[] TrustedDirs =
        {
            WithSlash(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            WithSlash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows Defender")),
            WithSlash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeWebView")),
        };

        readonly object sync = new object();
        // pid → время создания процесса: каждый процесс проверяется один раз, повторно — если pid переиспользован.
        readonly Dictionary<int, long> checkedProcesses = new Dictionary<int, long>();
        // Когда о программе последний раз писали в журнал: у браузера десятки процессов,
        // в журнал о нём — одна строка (в режиме журнала — один раз, при завершении — раз в минуту).
        readonly Dictionary<string, DateTime> reported = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly string ownName;

        Rules rules = new Rules();
        Timer timer;
        int busy;
        volatile bool disposed;

        public event EventHandler<AppControlEventArgs> Event;

        public AppControl(AppControlSettings settings)
        {
            using (var self = Process.GetCurrentProcess())
                ownName = Path.GetFileName(self.MainModule.FileName);
            Apply(settings);
        }

        public void Apply(AppControlSettings settings)
        {
            var next = new Rules
            {
                AllowEnabled = settings.Enabled,
                AllowBlock = settings.Block,
                Allowed = ToNameSet(settings.Allowed),
                DenyEnabled = settings.DenyEnabled,
                DenyBlock = settings.DenyBlock,
                Denied = ToNameSet(settings.Denied)
            };
            lock (sync)
            {
                rules = next;
                // Перепроверяем всё запущенное: могли включить завершение или изменить списки.
                checkedProcesses.Clear();
                reported.Clear();
            }
        }

        public void Start()
        {
            timer = new Timer(Tick, null, 0, PollIntervalMs);
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null)
                timer.Dispose();
        }

        /// <summary>Приводит запись списка к имени exe: «C:\path\App» → «App.exe».</summary>
        public static string NormalizeName(string entry)
        {
            string name = (entry ?? "").Trim().Trim('"').Trim();
            if (name.Length == 0)
                return "";
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0)
                name = name.Substring(slash + 1);
            if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            return name;
        }

        static HashSet<string> ToNameSet(IEnumerable<string> entries)
        {
            return new HashSet<string>(entries.Select(NormalizeName).Where(n => n.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Программы, запущенные сейчас в сеансах пользователей, — для подсказки в окне.
        /// includeTrusted=false — без программ из папок Windows, Защитника и WebView2.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetRunningPrograms(bool includeTrusted)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId == 0)
                            continue;
                    }
                    catch (InvalidOperationException) { continue; }

                    IntPtr handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, p.Id);
                    if (handle == IntPtr.Zero)
                        continue;
                    try
                    {
                        string path = GetImagePath(handle);
                        if (path != null && (includeTrusted || !IsTrustedPath(path)))
                            result[Path.GetFileName(path)] = path;
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(handle);
                    }
                }
            }
            return result.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        void Tick(object state)
        {
            if (disposed || Interlocked.Exchange(ref busy, 1) == 1)
                return;
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                Raise("Ошибка контроля программ: " + ex.Message, false);
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        void Scan()
        {
            Rules current;
            lock (sync)
            {
                current = rules;
                if (!current.AllowEnabled && !current.DenyEnabled)
                {
                    checkedProcesses.Clear();
                    return;
                }
            }

            var alive = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int session;
                    try { session = p.SessionId; }
                    catch (InvalidOperationException) { continue; } // процесс уже завершился
                    if (session == 0)
                        continue;
                    alive.Add(p.Id);
                    Check(p.Id, current);
                }
            }

            lock (sync)
            {
                foreach (var pid in checkedProcesses.Keys.Where(k => !alive.Contains(k)).ToList())
                    checkedProcesses.Remove(pid);
            }
        }

        void Check(int pid, Rules current)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessTerminate, false, pid);
            if (handle == IntPtr.Zero)
                handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return; // защищённый системный процесс

            try
            {
                long created = GetCreationTime(handle);
                lock (sync)
                {
                    long known;
                    if (checkedProcesses.TryGetValue(pid, out known) && known == created)
                        return;
                    checkedProcesses[pid] = created;
                }

                string path = GetImagePath(handle);
                if (path == null)
                    return;
                string name = Path.GetFileName(path);
                if (string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase))
                    return;

                bool denied = current.DenyEnabled && current.Denied.Contains(name);
                bool notAllowed = !denied && current.AllowEnabled
                    && !IsTrustedPath(path) && !current.Allowed.Contains(name);
                if (!denied && !notAllowed)
                    return;

                SecurityIdentifier sid;
                string user;
                if (!TryGetOwner(handle, out sid, out user) || IsServiceAccount(sid))
                    return; // системные процессы и агенты служб, работающие от имени SYSTEM в сеансе пользователя

                string details = name + " (" + path + "), пользователь " + user;
                bool block = denied ? current.DenyBlock : current.AllowBlock;
                if (!block)
                {
                    if (ShouldReport(name, TimeSpan.MaxValue))
                        Raise((denied ? "Запущена запрещённая программа: " : "Запущена программа не из списка разрешённых: ")
                            + details, false);
                }
                else if (NativeMethods.TerminateProcess(handle, 1))
                {
                    if (ShouldReport(name, BlockReportInterval))
                        Raise((denied ? "Запрещённая программа завершена: " : "Запуск запрещён, программа завершена: ")
                            + details, true);
                }
                else
                {
                    Raise("Не удалось завершить запрещённую программу " + details + ": "
                        + new Win32Exception(Marshal.GetLastWin32Error()).Message, true);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        void Raise(string message, bool alert)
        {
            if (disposed)
                return;
            var handler = Event;
            if (handler != null)
                handler(this, new AppControlEventArgs(message, alert));
        }

        bool ShouldReport(string name, TimeSpan interval)
        {
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                DateTime last;
                if (reported.TryGetValue(name, out last) && (interval == TimeSpan.MaxValue || now - last < interval))
                    return false;
                reported[name] = now;
                return true;
            }
        }

        static bool IsTrustedPath(string path)
        {
            return TrustedDirs.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsServiceAccount(SecurityIdentifier sid)
        {
            return sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
                || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);
        }

        static string WithSlash(string dir)
        {
            return dir.EndsWith("\\") ? dir : dir + "\\";
        }

        static string GetImagePath(IntPtr process)
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }

        static long GetCreationTime(IntPtr process)
        {
            long creation, exit, kernel, user;
            return NativeMethods.GetProcessTimes(process, out creation, out exit, out kernel, out user) ? creation : 0;
        }

        static bool TryGetOwner(IntPtr process, out SecurityIdentifier sid, out string name)
        {
            sid = null;
            name = null;
            IntPtr token;
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out token))
                return false;
            try
            {
                using (var identity = new WindowsIdentity(token))
                {
                    sid = identity.User;
                    if (sid == null)
                        return false;
                    try { name = identity.Name; }
                    catch (SystemException) { name = sid.Value; }
                    return true;
                }
            }
            catch (SystemException)
            {
                return false;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }

        /// <summary>Неизменяемый снимок правил — проверка читает его без блокировки.</summary>
        sealed class Rules
        {
            public bool AllowEnabled;
            public bool AllowBlock;
            public HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool DenyEnabled;
            public bool DenyBlock;
            public HashSet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        static class NativeMethods
        {
            public const int ProcessTerminate = 0x0001;
            public const int ProcessQueryLimitedInformation = 0x1000;
            public const int TokenQuery = 0x0008;

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool TerminateProcess(IntPtr process, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        }
    }
}

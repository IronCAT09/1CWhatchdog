using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace OneCWhatchdog
{
    sealed class MonitorEventArgs : EventArgs
    {
        public MonitorEventArgs(string service, string message, bool alert)
        {
            Service = service;
            Message = message;
            Alert = alert;
        }

        public string Service { get; private set; }
        public string Message { get; private set; }
        public bool Alert { get; private set; }
    }

    /// <summary>
    /// Периодически опрашивает отмеченные службы. Если служба не в состоянии
    /// «Выполняется» дольше TimeoutSeconds — пытается её поднять.
    /// </summary>
    sealed class ServiceMonitor : IDisposable
    {
        public const int DefaultTimeoutSeconds = 30;
        public const int MinTimeoutSeconds = 10;
        public const int MaxTimeoutSeconds = 3600;
        const int CheckIntervalMs = 5000;
        static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(15);

        readonly object sync = new object();
        readonly HashSet<string> watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> notRunningSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> recovering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> reportedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> lastAlert = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Timer timer;
        int busy;
        volatile bool disposed;
        volatile int timeoutSeconds = DefaultTimeoutSeconds;

        public event EventHandler<MonitorEventArgs> Event;

        public ServiceMonitor(IEnumerable<string> services, int timeoutSeconds)
        {
            foreach (var name in services)
                watched.Add(name);
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>Сколько секунд служба может не работать, прежде чем её поднимут.</summary>
        public int TimeoutSeconds
        {
            get { return timeoutSeconds; }
            set { timeoutSeconds = Math.Max(MinTimeoutSeconds, Math.Min(MaxTimeoutSeconds, value)); }
        }

        public int Count
        {
            get { lock (sync) return watched.Count; }
        }

        public string[] GetWatched()
        {
            lock (sync) return watched.ToArray();
        }

        public void SetWatched(string name, bool on)
        {
            lock (sync)
            {
                if (on)
                {
                    watched.Add(name);
                    return;
                }
                watched.Remove(name);
                notRunningSince.Remove(name);
                recovering.Remove(name);
                reportedMissing.Remove(name);
                lastAlert.Remove(name);
            }
        }

        public void ReplaceWatched(IEnumerable<string> services)
        {
            var next = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);
            foreach (var name in GetWatched())
                if (!next.Contains(name))
                    SetWatched(name, false);
            foreach (var name in next)
                SetWatched(name, true);
        }

        public void Start()
        {
            timer = new Timer(Tick, null, 0, CheckIntervalMs);
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null)
                timer.Dispose();
        }

        void Tick(object state)
        {
            if (disposed || Interlocked.Exchange(ref busy, 1) == 1)
                return;
            try
            {
                foreach (var name in GetWatched())
                {
                    if (disposed)
                        break;
                    try { Check(name); }
                    catch (Exception ex) { Raise(name, "ошибка проверки: " + Describe(ex), false); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        void Check(string name)
        {
            using (var sc = new ServiceController(name))
            {
                ServiceControllerStatus status;
                try
                {
                    status = sc.Status;
                }
                catch (InvalidOperationException)
                {
                    bool first;
                    lock (sync) first = watched.Contains(name) && reportedMissing.Add(name);
                    if (first)
                        Raise(name, "служба не найдена", true);
                    return;
                }

                DateTime now = DateTime.UtcNow;
                DateTime since;
                bool recovered = false, wentDown = false;
                lock (sync)
                {
                    if (!watched.Contains(name))
                        return;
                    reportedMissing.Remove(name);

                    if (status == ServiceControllerStatus.Running)
                    {
                        notRunningSince.Remove(name);
                        lastAlert.Remove(name);
                        recovered = recovering.Remove(name);
                        since = now;
                    }
                    else if (!notRunningSince.TryGetValue(name, out since))
                    {
                        notRunningSince[name] = now;
                        since = now;
                        wentDown = true;
                    }
                }

                if (recovered)
                {
                    Raise(name, "служба снова работает", false);
                    return;
                }
                if (status == ServiceControllerStatus.Running)
                    return;
                if (wentDown)
                {
                    Raise(name, "состояние «" + Texts.Status(status) + "», ожидание "
                        + TimeoutSeconds + " с", false);
                    return;
                }
                if (now - since < TimeSpan.FromSeconds(TimeoutSeconds))
                    return;

                Recover(sc, status);

                lock (sync)
                {
                    if (watched.Contains(name))
                        notRunningSince[name] = DateTime.UtcNow;
                }
            }
        }

        void Recover(ServiceController sc, ServiceControllerStatus status)
        {
            string name = sc.ServiceName;
            lock (sync) recovering.Add(name);

            string prefix = "не отвечает " + TimeoutSeconds + " с («" + Texts.Status(status) + "»)";
            try
            {
                switch (status)
                {
                    case ServiceControllerStatus.Stopped:
                        Raise(name, prefix + " — запуск", true);
                        sc.Start();
                        break;

                    case ServiceControllerStatus.Paused:
                        Raise(name, prefix + " — возобновление", true);
                        sc.Continue();
                        break;

                    default:
                        // Зависла в промежуточном состоянии: завершаем процесс и запускаем заново.
                        Raise(name, prefix + " — перезапуск", true);
                        if (!KillServiceProcess(name))
                            return;
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, StopWaitTimeout);
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            Raise(name, "не остановилась после завершения процесса", true);
                            return;
                        }
                        sc.Start();
                        break;
                }
            }
            catch (Exception ex)
            {
                Raise(name, "не удалось запустить: " + Describe(ex), true);
            }
        }

        bool KillServiceProcess(string name)
        {
            string wqlName = name.Replace("\\", "\\\\").Replace("'", "\\'");
            uint pid = 0;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Service WHERE Name='" + wqlName + "'"))
            {
                foreach (ManagementObject o in searcher.Get())
                    using (o) pid = (uint)o["ProcessId"];
            }
            if (pid == 0)
                return true; // процесса уже нет — сразу пробуем запустить

            int sharing;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Service WHERE ProcessId=" + pid))
            using (var result = searcher.Get())
                sharing = result.Count;

            if (sharing > 1)
            {
                Raise(name, "процесс " + pid + " общий для " + sharing
                    + " служб — принудительное завершение пропущено", true);
                return false;
            }

            using (var p = Process.GetProcessById((int)pid))
            {
                p.Kill();
                p.WaitForExit((int)StopWaitTimeout.TotalMilliseconds);
            }
            Raise(name, "процесс " + pid + " завершён принудительно", false);
            return true;
        }

        void Raise(string service, string message, bool alert)
        {
            if (disposed)
                return;
            if (alert)
            {
                // Не повторяем одно и то же всплывающее уведомление каждые 30 секунд.
                lock (sync)
                {
                    string last;
                    alert = !(lastAlert.TryGetValue(service, out last) && last == message);
                    lastAlert[service] = message;
                }
            }
            var handler = Event;
            if (handler != null)
                handler(this, new MonitorEventArgs(service, message, alert));
        }

        static string Describe(Exception ex)
        {
            var inner = ex.InnerException as Win32Exception;
            return inner != null ? inner.Message : ex.Message;
        }
    }
}

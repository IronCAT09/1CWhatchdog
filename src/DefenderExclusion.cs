using System;
using System.IO;
using System.Linq;
using System.Management;

namespace OneCWhatchdog
{
    /// <summary>
    /// Исключение папки установки из проверки Защитника Windows (то же, что Add-MpPreference
    /// -ExclusionPath). Нужны права администратора. В исключение добавляется только папка
    /// в Program Files — писать в неё могут только администраторы, так что подложить туда
    /// что-то в обход антивируса нельзя.
    /// </summary>
    static class DefenderExclusion
    {
        const string Namespace = @"root\Microsoft\Windows\Defender";
        const string PreferenceClass = "MSFT_MpPreference";

        public static string ExcludedPath
        {
            get { return Autostart.InstallDir; }
        }

        public static bool IsExcluded()
        {
            return Run(delegate
            {
                using (var searcher = new ManagementObjectSearcher(Namespace, "SELECT ExclusionPath FROM " + PreferenceClass))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject preference in results)
                    {
                        using (preference)
                        {
                            var paths = preference["ExclusionPath"] as string[];
                            if (paths != null && paths.Any(p => SamePath(p, ExcludedPath)))
                                return true;
                        }
                    }
                }
                return false;
            });
        }

        public static void Add()
        {
            Invoke("Add");
        }

        public static void Remove()
        {
            Invoke("Remove");
        }

        static void Invoke(string method)
        {
            Run(delegate
            {
                using (var preferences = new ManagementClass(Namespace, PreferenceClass, null))
                using (var parameters = preferences.GetMethodParameters(method))
                {
                    parameters["ExclusionPath"] = new[] { ExcludedPath };
                    using (var result = preferences.InvokeMethod(method, parameters, null))
                    {
                        object code = result == null ? null : result["ReturnValue"];
                        if (code != null && Convert.ToInt64(code) != 0)
                            throw new InvalidOperationException("Защитник Windows вернул код ошибки " + code + ".");
                    }
                }
                return true;
            });
        }

        static T Run<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
                    throw new InvalidOperationException(
                        "Защитник Windows недоступен — возможно, установлен другой антивирус. "
                        + "Добавьте папку " + ExcludedPath + " в исключения в его настройках.", ex);
                if (ex.ErrorCode == ManagementStatus.AccessDenied)
                    throw new InvalidOperationException("Нет прав на изменение настроек Защитника Windows.", ex);
                throw new InvalidOperationException(ex.Message.Trim(), ex);
            }
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(
                (a ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar),
                (b ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ServiceWatchdog
{
    static class Settings
    {
        const long MaxLogSize = 1024 * 1024;

        static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Program.AppName);
        static readonly string ServicesPath = Path.Combine(Dir, "services.txt");
        static readonly string TimeoutPath = Path.Combine(Dir, "timeout.txt");
        static readonly string LogPath = Path.Combine(Dir, "watchdog.log");
        static readonly object logSync = new object();

        public static int LoadTimeout()
        {
            int seconds;
            if (File.Exists(TimeoutPath) && int.TryParse(File.ReadAllText(TimeoutPath).Trim(), out seconds))
                return seconds;
            return ServiceMonitor.DefaultTimeoutSeconds;
        }

        public static void SaveTimeout(int seconds)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(TimeoutPath, seconds.ToString());
        }

        public static List<string> LoadServices()
        {
            if (!File.Exists(ServicesPath))
                return new List<string>();
            return File.ReadAllLines(ServicesPath, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void SaveServices(IEnumerable<string> services)
        {
            Directory.CreateDirectory(Dir);
            string tmp = ServicesPath + ".tmp";
            File.WriteAllLines(tmp, services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), Encoding.UTF8);
            if (File.Exists(ServicesPath))
                File.Replace(tmp, ServicesPath, null);
            else
                File.Move(tmp, ServicesPath);
        }

        public static void AppendLog(string line)
        {
            lock (logSync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxLogSize)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

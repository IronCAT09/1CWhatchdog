using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OneCWhatchdog
{
    /// <summary>
    /// Строка журнала: «yyyy-MM-dd HH:mm:ss ! текст», где «!» — событие,
    /// о котором показывается уведомление в трее.
    /// </summary>
    sealed class LogEntry : EventArgs
    {
        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        public LogEntry(DateTime time, string text, bool alert)
        {
            Time = time;
            Text = text;
            Alert = alert;
        }

        public DateTime Time { get; private set; }
        public string Text { get; private set; }
        public bool Alert { get; private set; }

        public string Format()
        {
            return Time.ToString(TimeFormat, CultureInfo.InvariantCulture) + (Alert ? " ! " : "   ") + Text;
        }

        public static LogEntry Parse(string line)
        {
            DateTime time;
            if (line.Length > 21 && DateTime.TryParseExact(line.Substring(0, 19), TimeFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                return new LogEntry(time, line.Substring(21).Trim(), line[20] == '!');
            return new LogEntry(DateTime.MinValue, line, false);
        }

        public override string ToString()
        {
            string time = Time == DateTime.MinValue ? "" : Time.ToString("dd.MM HH:mm:ss") + "  ";
            return time + (Alert ? "[!] " : "") + Text;
        }
    }

    /// <summary>Читает новые строки журнала, который дописывает фоновый мониторинг.</summary>
    sealed class LogTail
    {
        const int InitialBytes = 64 * 1024;

        readonly string path;
        long position = -1;

        public LogTail(string path)
        {
            this.path = path;
        }

        public List<LogEntry> ReadInitial(int maxLines)
        {
            var entries = Read(true);
            return entries.Skip(Math.Max(0, entries.Count - maxLines)).ToList();
        }

        public List<LogEntry> ReadNew()
        {
            return Read(false);
        }

        List<LogEntry> Read(bool initial)
        {
            var result = new List<LogEntry>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = fs.Length;
                    bool skipFirst = false;
                    if (initial)
                    {
                        position = Math.Max(0, length - InitialBytes);
                        skipFirst = position > 0; // первая строка, скорее всего, обрезана
                    }
                    else if (position < 0 || length < position)
                    {
                        position = 0; // файла не было или журнал переименован в .old
                    }
                    if (length <= position)
                        return result;

                    fs.Seek(position, SeekOrigin.Begin);
                    var buffer = new byte[length - position];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = fs.Read(buffer, read, buffer.Length - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    if (read == 0)
                        return result;

                    // Берём только завершённые строки — последнюю могут ещё дописывать.
                    int end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                    if (end < 0)
                        return result;
                    position += end + 1;

                    var lines = Encoding.UTF8.GetString(buffer, 0, end + 1).Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i == 0 && skipFirst)
                            continue;
                        string line = lines[i].TrimEnd('\r').TrimStart('﻿');
                        if (line.Length > 0)
                            result.Add(LogEntry.Parse(line));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }
    }
}

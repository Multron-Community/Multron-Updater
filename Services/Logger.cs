using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MultronUpdater.Services
{
    public enum LogLevel { Info, Success, Warning, Error }

    public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
    {
        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
        public string LevelText => Level.ToString().ToUpperInvariant();
        public override string ToString() => $"[{TimeText}] [{LevelText}] {Message}";
    }

    public static class Logger
    {
        private static readonly object Sync = new();
        public static string LogFile => Path.Combine(AppSettings.DataFolder, "log.txt");
        private static string OldLogFile => Path.Combine(AppSettings.DataFolder, "log.old.txt");

        public static event Action<LogEntry>? EntryAdded;

        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Success(string message) => Write(LogLevel.Success, message);
        public static void Warn(string message) => Write(LogLevel.Warning, message);
        public static void Error(string message) => Write(LogLevel.Error, message);

        public static void Write(LogLevel level, string message)
        {
            var entry = new LogEntry(DateTime.Now, level, message);
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(AppSettings.DataFolder);
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                        File.Move(LogFile, OldLogFile, true);
                    File.AppendAllText(LogFile, entry + Environment.NewLine);
                }
                catch { }
            }
            EntryAdded?.Invoke(entry);
        }

        private static readonly Regex LineRx = new(@"^\[(?<t>[\d\- :]{19})\] \[(?<l>[A-Z]+)\] (?<m>.*)$", RegexOptions.Compiled);

        public static List<LogEntry> ReadRecent(int max)
        {
            var list = new List<LogEntry>();
            try
            {
                if (!File.Exists(LogFile)) return list;
                string[] lines;
                lock (Sync) lines = File.ReadAllLines(LogFile);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - max)))
                {
                    var m = LineRx.Match(line);
                    if (!m.Success) continue;
                    if (!DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    if (!Enum.TryParse<LogLevel>(m.Groups["l"].Value, true, out var lvl)) lvl = LogLevel.Info;
                    list.Add(new LogEntry(t, lvl, m.Groups["m"].Value));
                }
            }
            catch { }
            return list;
        }
    }
}

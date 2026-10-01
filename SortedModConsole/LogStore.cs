using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;

namespace SortedModConsole
{
    internal enum Severity { Debug, Info, Warning, Error }

    internal sealed class LogEntry
    {
        public string Source;
        public string Level;
        public Severity Severity;
        public string Message;
        public string FirstLine;
        public int ExtraLines;
        // Null for lines read back from the startup log, which has no timestamps.
        public DateTime? Time;
        public DateTime? LastTime;
        // Identical lines logged back to back are kept as one entry with a count.
        public int Count = 1;
        // Pushed out by a limit or cleared. Stays in LogStore.Entries until the next compaction.
        public bool Dropped;

        public bool IsProblem => Severity >= Severity.Warning;

        public LogEntry(string source, string level, Severity severity, string message, DateTime? time)
        {
            Source = source;
            Level = level;
            Severity = severity;
            Message = message;
            Time = time;
            LastTime = time;

            int nl = message.IndexOf('\n');
            if (nl < 0)
            {
                FirstLine = message;
            }
            else
            {
                FirstLine = message.Substring(0, nl).TrimEnd('\r');
                for (int i = nl; i >= 0; i = message.IndexOf('\n', i + 1))
                    ExtraLines++;
                if (message.EndsWith("\n"))
                    ExtraLines--;
            }
        }
    }

    internal sealed class SourceStats
    {
        public readonly string Name;
        // Lines currently kept, repeats included.
        public int Total, Warnings, Errors;
        // Lines pushed out by the limits, repeats included.
        public int DroppedLines, DroppedProblems;

        // The kept entries, oldest first, in two pools: a mod spamming info or debug lines only
        // pushes out its own older info and debug lines, never its warnings and errors.
        public readonly Queue<LogEntry> Lines = new Queue<LogEntry>();
        public readonly Queue<LogEntry> Problems = new Queue<LogEntry>();

        public SourceStats(string name) { Name = name; }
    }

    /// <summary>
    /// Every log line, kept per mod. Filled from any thread through the queue, read on the main
    /// thread only.
    /// </summary>
    internal static class LogStore
    {
        private static readonly ConcurrentQueue<LogEntry> incoming = new ConcurrentQueue<LogEntry>();

        // All entries in the order they came, for the All and Problems tabs. Dropped entries
        // stay in here, flagged, until there are enough of them to be worth compacting.
        public static readonly List<LogEntry> Entries = new List<LogEntry>();
        public static readonly Dictionary<string, SourceStats> Sources = new Dictionary<string, SourceStats>();
        // Goes up on every change, so views know when to rebuild.
        public static int Version;
        public static int SourcesVersion;

        private static int kept;
        private static int droppedInList;

        public static void Enqueue(LogEntry entry) => incoming.Enqueue(entry);

        public static void Drain()
        {
            bool any = false;
            while (incoming.TryDequeue(out var e))
            {
                Add(e);
                any = true;
            }
            if (any)
                EnforceTotal();
        }

        public static void Add(LogEntry e)
        {
            if (!Sources.TryGetValue(e.Source, out var src))
            {
                src = new SourceStats(e.Source);
                Sources[e.Source] = src;
                SourcesVersion++;
            }

            var last = Entries.Count > 0 ? Entries[Entries.Count - 1] : null;
            if (last != null && !last.Dropped && last.Severity == e.Severity && last.Source == e.Source && last.Message == e.Message)
            {
                last.Count++;
                last.LastTime = e.Time;
            }
            else
            {
                var pool = e.IsProblem ? src.Problems : src.Lines;
                Entries.Add(e);
                pool.Enqueue(e);
                kept++;

                int limit = e.IsProblem ? Plugin.maxProblemsPerMod.Value : Plugin.maxLinesPerMod.Value;
                while (pool.Count > Math.Max(100, limit))
                    Drop(src, pool.Dequeue(), true);
            }

            Count(src, e.Severity, 1);
            Version++;
        }

        /// <summary>
        /// The limit on everything together, for when many mods fill their pools. Takes from
        /// whichever mod keeps the most, so the quiet ones are never touched.
        /// </summary>
        public static void EnforceTotal()
        {
            int max = Math.Max(1000, Plugin.maxTotalLines.Value);
            if (kept > max)
            {
                // A tenth more than needed, so this does not run again on every new line.
                int target = max - max / 10;
                while (kept > target)
                {
                    SourceStats biggest = null;
                    bool fromLines = true;
                    foreach (var s in Sources.Values)
                    {
                        if (biggest == null || s.Lines.Count > biggest.Lines.Count)
                            biggest = s;
                    }
                    if (biggest == null || biggest.Lines.Count == 0)
                    {
                        // Only warnings and errors left anywhere.
                        fromLines = false;
                        foreach (var s in Sources.Values)
                            if (biggest == null || s.Problems.Count > biggest.Problems.Count)
                                biggest = s;
                    }
                    if (biggest == null)
                        break;
                    var pool = fromLines ? biggest.Lines : biggest.Problems;
                    if (pool.Count == 0)
                        break;
                    Drop(biggest, pool.Dequeue(), true);
                }
            }
            Compact();
        }

        /// <summary>Removes entries for good. Cleared lines do not count as dropped.</summary>
        public static void RemoveAll(Predicate<LogEntry> match)
        {
            foreach (var src in Sources.Values)
            {
                bool all = src.Lines.All(e => match(e)) && src.Problems.All(e => match(e));
                Filter(src, src.Lines, match);
                Filter(src, src.Problems, match);
                if (all)
                    src.DroppedLines = src.DroppedProblems = 0;
            }
            Compact();
            Version++;
        }

        private static void Filter(SourceStats src, Queue<LogEntry> pool, Predicate<LogEntry> match)
        {
            int n = pool.Count;
            for (int i = 0; i < n; i++)
            {
                var e = pool.Dequeue();
                if (match(e))
                    Drop(src, e, false);
                else
                    pool.Enqueue(e);
            }
        }

        private static void Drop(SourceStats src, LogEntry e, bool countAsDropped)
        {
            e.Dropped = true;
            kept--;
            droppedInList++;
            Count(src, e.Severity, -e.Count);
            if (countAsDropped)
            {
                if (e.IsProblem) src.DroppedProblems += e.Count;
                else src.DroppedLines += e.Count;
            }
            Version++;
        }

        private static void Compact()
        {
            if (droppedInList > 0 && droppedInList >= Entries.Count / 4)
            {
                Entries.RemoveAll(e => e.Dropped);
                droppedInList = 0;
            }
        }

        private static void Count(SourceStats stats, Severity severity, int n)
        {
            stats.Total += n;
            if (severity == Severity.Warning) stats.Warnings += n;
            else if (severity == Severity.Error) stats.Errors += n;
        }

        public static Severity SeverityOf(LogLevel level)
        {
            if ((level & (LogLevel.Fatal | LogLevel.Error)) != 0) return Severity.Error;
            if ((level & LogLevel.Warning) != 0) return Severity.Warning;
            if ((level & (LogLevel.Message | LogLevel.Info)) != 0) return Severity.Info;
            if ((level & LogLevel.Debug) != 0) return Severity.Debug;
            return Severity.Info;
        }
    }

    /// <summary>
    /// Hooked into BepInEx next to its console and disk listeners, so it sees every line any mod
    /// logs, tagged with the mod's name. Nothing is needed from the other mods.
    /// </summary>
    internal sealed class ConsoleLogListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                string source = eventArgs.Source?.SourceName ?? "?";
                string message = eventArgs.Data?.ToString() ?? "null";
                LogStore.Enqueue(new LogEntry(source, eventArgs.Level.ToString(),
                    LogStore.SeverityOf(eventArgs.Level), message, DateTime.Now));
            }
            catch
            {
                // Never let logging throw back into whoever logged.
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Reads back what was logged before this mod loaded, from the log file BepInEx is writing.
    /// </summary>
    internal static class StartupLog
    {
        // "[Warning:  SomeMod] message"; lines that do not match continue the previous message.
        private static readonly Regex lineStart = new Regex(
            @"^\[(Fatal|Error|Warning|Message|Info|Debug)\s*:\s*([^\]]*?)\s*\] ?(.*)$", RegexOptions.Compiled);

        public static void Load()
        {
            try
            {
                string path = FindLogFile();
                if (path == null)
                    return;

                foreach (var disk in BepInEx.Logging.Logger.Listeners.OfType<DiskLogListener>())
                {
                    try { disk.LogWriter?.Flush(); } catch { }
                }

                string level = null, source = null;
                var message = new StringBuilder();
                int count = 0;

                void Flush()
                {
                    if (level == null)
                        return;
                    var lv = (LogLevel)Enum.Parse(typeof(LogLevel), level);
                    LogStore.Add(new LogEntry(source, level, LogStore.SeverityOf(lv), message.ToString(), null));
                    count++;
                    level = null;
                    message.Clear();
                }

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var m = lineStart.Match(line);
                        if (m.Success)
                        {
                            Flush();
                            level = m.Groups[1].Value;
                            source = m.Groups[2].Value;
                            message.Append(m.Groups[3].Value);
                        }
                        else if (level != null)
                        {
                            message.Append('\n').Append(line);
                        }
                    }
                    Flush();
                }
                LogStore.EnforceTotal();
                Plugin.mlg.LogDebug($"Read {count} startup lines from {path}");
            }
            catch (Exception e)
            {
                Plugin.mlg.LogWarning($"Could not read the startup log: {e.Message}");
            }
        }

        private static string FindLogFile()
        {
            // With several game instances open BepInEx writes LogOutput.log.1, .2 ...; this
            // instance's file is the one written last.
            var dir = new DirectoryInfo(Paths.BepInExRootPath);
            if (!dir.Exists)
                return null;
            return dir.GetFiles("LogOutput.log*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
    }
}

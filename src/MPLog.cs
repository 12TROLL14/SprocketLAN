using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace SprocketMP
{
    /// <summary>Logs to the BepInEx log and to a per-process file (BepInEx/SprocketLAN_[pid].log) so two local instances don't mix.</summary>
    public static class MPLog
    {
        static ManualLogSource _src;
        static StreamWriter _file;
        static readonly object _lock = new object();

        public static void Init(ManualLogSource src)
        {
            _src = src;
            try
            {
                var dir = Paths.BepInExRootPath;
                // keep at most a handful of files around
                foreach (var old in Directory.GetFiles(dir, "SprocketLAN_*.log"))
                    try { if (System.IO.File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2)) System.IO.File.Delete(old); } catch { }
                var path = Path.Combine(dir, $"SprocketLAN_{Environment.ProcessId}.log");
                _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                // stable alias of the most recent instance
                try { System.IO.File.WriteAllText(Path.Combine(dir, "SprocketLAN_latest_pid.txt"), Environment.ProcessId.ToString()); } catch { }
            }
            catch (Exception e) { src.LogWarning("Could not open SprocketMP log file: " + e.Message); }
        }

        static void W(string lvl, string msg)
        {
            lock (_lock)
            {
                try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{lvl}] {msg}"); } catch { }
            }
        }

        public static void Info(string m) { _src?.LogInfo(m); W("I", m); }
        public static void Warn(string m) { _src?.LogWarning(m); W("W", m); }
        public static void Error(string m) { _src?.LogError(m); W("E", m); }
        public static void Debug(string m) { if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) { _src?.LogInfo(m); } W("D", m); }
        /// <summary>File-only (no console spam).</summary>
        public static void File(string m) { W("F", m); }
    }
}

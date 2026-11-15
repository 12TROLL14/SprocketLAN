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

        // ---- freeze watchdog: the main thread ticks Beat every frame and names what it is doing in Stage;
        //      a background thread reports where it got stuck if the frames stop
        public static volatile string Stage = "start";
        public static long Beat;
        static System.Threading.Thread _dog;
        public static void StartWatchdog()
        {
            if (_dog != null) return;
            _dog = new System.Threading.Thread(() =>
            {
                long last = -1; int still = 0; bool told = false;
                while (true)
                {
                    System.Threading.Thread.Sleep(500);
                    long b = System.Threading.Interlocked.Read(ref Beat);
                    if (b == last && b > 0) { still++; if (still == 6 && !told) { told = true; W("W", $"FREEZE: no frame for 3 s, main thread is in: {Stage}"); try { _src?.LogWarning($"FREEZE: no frame for 3 s, main thread is in: {Stage}"); } catch { } } }
                    else { if (told) W("I", $"Unfrozen after {still * 0.5f:F1} s (was in {Stage})"); still = 0; told = false; }
                    last = b;
                }
            }) { IsBackground = true, Name = "SprocketLAN-watchdog" };
            _dog.Start();
        }

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

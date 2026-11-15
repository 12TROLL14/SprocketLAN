using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace SprocketMP
{
    /// <summary>
    /// The game's garbage collector stops everything for ~100 ms when it runs (more with big tanks loaded), every few
    /// seconds. While a hit is worked out and its pen cam plays (a few seconds), it is paused, and runs once afterwards.
    /// Never held longer than 15 s; F7 › PenCam › PauseGarbageCollection.
    /// </summary>
    public static class GcPause
    {
        static bool _held, _logged, _failed; static float _until; static GarbageCollector.Mode _prev;
        const float MaxHold = 15f;

        public static void Hold(float seconds)
        {
            if (_failed || !Plugin.PenCamPauseGc.Value) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (!_held)
                {
                    _prev = GarbageCollector.GCMode;
                    if (_prev == GarbageCollector.Mode.Disabled) { if (!_logged) { _logged = true; MPLog.Info("Garbage collection: the game already runs with automatic collection off (it collects by itself), nothing to pause"); } return; }
                    GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                    _held = true; _start = now;
                    if (!_logged) { _logged = true; MPLog.Info($"Garbage collection paused during hits / pen cams (incremental: {GarbageCollector.isIncremental})"); }
                }
                _until = Math.Min(_start + MaxHold, Math.Max(_until, now + Math.Min(seconds, MaxHold)));
            }
            catch (Exception e) { _failed = true; MPLog.Warn("garbage collection pause: " + e.Message); }
        }
        static float _start;

        public static void Release()
        {
            if (!_held) return;
            _held = false;
            try { GarbageCollector.GCMode = _prev == GarbageCollector.Mode.Disabled ? GarbageCollector.Mode.Enabled : _prev; }
            catch (Exception e) { _failed = true; MPLog.Warn("garbage collection resume: " + e.Message); }
        }

        public static void Tick()
        {
            if (!_held) return;
            if (Time.realtimeSinceStartup > _until || !Plugin.PenCamPauseGc.Value) Release();
        }
    }
}

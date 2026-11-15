using System;
using System.Collections.Generic;
using Sprocket.WeaponFramework;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>Designs set as autocannon (designer Shells panel): after each shot the gun is loaded again once its fire
    /// interval (60 / rounds per minute) has passed, however long the crew would take.</summary>
    public static class AutoCannon
    {
        static readonly Dictionary<IntPtr, float> _firedAt = new Dictionary<IntPtr, float>();
        static readonly Dictionary<IntPtr, bool> _wasLoaded = new Dictionary<IntPtr, bool>();
        static IntPtr _tank; static bool _announced;

        public static void Tick(VehicleRig local)
        {
            if (local?.GO == null) { _tank = IntPtr.Zero; return; }
            if (_tank != local.GO.Pointer) { _tank = local.GO.Pointer; _firedAt.Clear(); _wasLoaded.Clear(); _announced = false; }
            var baseD = Shells.ShellStore.Active;   // (sets the battle design name)
            float now = Time.time;
            foreach (var w in local.Weapons)
            {
                try
                {
                    if (w == null) continue;
                    var d = Shells.ShellStore.Get(Shells.ShellStore.Key(Shells.ShellStore.ActiveName, Shells.ShellStore.SpecOfWeapon(local.GO, w)));   // this gun's own settings
                    if (d == null || !d.AutoCannon) continue;
                    float interval = 60f / Mathf.Clamp(d.FireRate, 1f, 3000f);
                    if (!_announced) { _announced = true; MPLog.Info($"Autocannon {Shells.ShellStore.SpecOfWeapon(local.GO, w)}: {d.FireRate:F0} rounds/min ({interval:F2} s per shot), penetration / {Shells.ShellConfig.AutocannonPenDivisor.Value:F2}"); }
                    var key = w.Pointer;
                    bool loaded = w.LoadState == LoadState.Loaded && !w.NeedsReload;
                    _wasLoaded.TryGetValue(key, out bool was);
                    if (was && !loaded) _firedAt[key] = now;                         // just fired (or unloaded for a shell switch)
                    _wasLoaded[key] = loaded;
                    if (loaded) continue;
                    if (!_firedAt.TryGetValue(key, out float t0)) { _firedAt[key] = now; continue; }
                    if (now - t0 < interval) continue;
                    var lt = w.LoadTask;
                    if (lt != null) { lt.LoadInstantly(); w.NeedsReload = false; _wasLoaded[key] = true; }
                }
                catch (Exception e) { MPLog.Debug("autocannon: " + e.Message); }
            }
        }
    }
}

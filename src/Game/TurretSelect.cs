using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP
{
    /// <summary>
    /// F7 › Battle › TurretsFollowFireGroup: only the turrets (and gun mounts) that carry a gun of the selected fire
    /// group follow your aim; the others hold the direction they were last given. A turret that carries another turret
    /// with such a gun turns too. AllTurretsKey (` by default, the key left of 1) makes every turret follow again,
    /// until you pick another fire group. Only your own tank: bots and other players aren't touched.
    /// </summary>
    public static class TurretSelect
    {
        static readonly HashSet<IntPtr> _mine = new HashSet<IntPtr>(), _allowed = new HashSet<IntPtr>();
        static bool _all; static int _lastGroups = int.MinValue; static float _next; static IntPtr _tank;
        static string _keyName; static bool _logged; static Key _key = Key.None;
        public static bool AllTurrets => _all;

        public static void Patch(Harmony h)
        {
            int n = 0;
            foreach (var t in new[] { typeof(Sprocket.Vehicles.Weapons.TurretBehaviour), typeof(Sprocket.Vehicles.Weapons.LayingDriveBehaviour) })
                try { foreach (var m in t.GetMethods()) if (m.Name == "AimAtDirection" && m.DeclaringType == t) { h.Patch(m, prefix: new HarmonyMethod(typeof(TurretSelect), nameof(AimPrefix))); n++; } }
                catch (Exception e) { MPLog.Warn($"Turret select {t.Name}: {e.Message}"); }
            MPLog.Info($"Turret select: {n} hooks");
        }

        static bool AimPrefix(Il2CppSystem.Object __instance)
        {
            if (!Plugin.TurretsFollowGroup.Value || _all || _allowed.Count == 0) return true;
            try
            {
                var p = __instance.Pointer;
                if (!_mine.Contains(p)) return true;   // not our tank
                if (_allowed.Contains(p)) return true;
                if (!_logged) { _logged = true; MPLog.Info($"Turret select: holding the turrets of the other fire groups ({_allowed.Count} of {_mine.Count} drives follow)"); }
                return false;                          // not this fire group's: keeps the direction it had
            }
            catch { return true; }
        }

        /// <summary>Every frame in battle (the key), and the allowed drives worked out again 4 times a second.</summary>
        public static void Tick()
        {
            var bm = BattleManager.Instance;
            if (bm == null || !bm.InBattle || bm.Local?.GO == null) { _mine.Clear(); _allowed.Clear(); _tank = IntPtr.Zero; _all = false; return; }
            if (!Plugin.TurretsFollowGroup.Value) return;
            if (_keyName != Plugin.AllTurretsKey.Value) { _keyName = Plugin.AllTurretsKey.Value; if (!Enum.TryParse(_keyName, true, out _key)) _key = Key.None; }
            var kb = Keyboard.current;
            try
            {
                if (kb != null && _key != Key.None && kb[_key].wasPressedThisFrame && !UI.MenuUI.AnyFieldFocusedStatic())
                {
                    _all = !_all;
                    MPLog.Info(_all ? "Turrets: all follow your aim" : "Turrets: only the selected fire group's follow your aim");
                }
            }
            catch { }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            var local = bm.Local;
            try
            {
                if (local.GO.Pointer != _tank) { _tank = local.GO.Pointer; _all = false; _lastGroups = int.MinValue; }
                _mine.Clear();
                foreach (var p in local.AimBehaviourPtrs) if (p != IntPtr.Zero) _mine.Add(p);
                int groups = Shells.ShellSelect.ActiveGroupsMask;
                if (groups != _lastGroups) { if (_lastGroups != int.MinValue && _all) _all = false; _lastGroups = groups; }   // another fire group: back to following it
                var guns = Shells.ShellSelect.ActiveGroupWeapons;
                _allowed.Clear();
                if (guns.Count == 0) return;   // nothing known: every turret follows
                for (int i = 0; i < local.AimXforms.Count && i < local.AimBehaviourPtrs.Count; i++)
                {
                    var xf = local.AimXforms[i]; var p = local.AimBehaviourPtrs[i];
                    if (xf == null || p == IntPtr.Zero) continue;
                    foreach (var g in guns) if (g != null && g.IsChildOf(xf)) { _allowed.Add(p); break; }
                }
            }
            catch (Exception e) { MPLog.Debug("turret select: " + e.Message); _allowed.Clear(); }
        }
    }
}

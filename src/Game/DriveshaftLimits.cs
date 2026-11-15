using System;
using HarmonyLib;
using Sprocket.Vehicles.Powertrains.Layouts;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Driveshaft thickness limit: the game's is 400 mm (PowertrainRules.MaxDriveshaftDiameter, a constant in read-only
    /// memory that can't be written). With F7 › Engine › UnlockDriveshaftThickness the limit becomes
    /// MaxDriveshaftMm (800 by default):
    /// - the maximum the game asks for (PowertrainLayoutQuery.GetMaxConnectionDiameter) is scaled up, so the label
    ///   reads "x/800mm";
    /// - a connection's "too thick" flag is only kept when its diameter is over the new limit (when the game sets
    ///   it, and when it's read);
    /// - the driveshafts' own flag the same way.
    /// </summary>
    public static class DriveshaftLimits
    {
        static bool _logged;
        static float GameMax { get { try { return Sprocket.PowertrainLayouts.PowertrainRules.MaxDriveshaftDiameter; } catch { return 0.4f; } } }
        /// <summary>The new limit in the game's units (scaled from its 400 mm).</summary>
        static float Limit => GameMax * Mathf.Max(1f, Plugin.DriveshaftMaxMm.Value) / 400f;
        static bool On => Plugin.DriveshaftUnlock.Value;
        static bool _inQuery;

        public static void Patch(Harmony h)
        {
            int n = 0;
            var t = typeof(DriveshaftBuilder);
            foreach (var name in new[] { "UpdateDriveshaftModel", "DriveshaftUpdate", "UpdateDriveShaftModels", "UpdateDriveshaftObstruction" })
                try
                {
                    foreach (var m in t.GetMethods())
                        if (m.Name == name && m.DeclaringType == t) { h.Patch(m, postfix: new HarmonyMethod(typeof(DriveshaftLimits), nameof(After))); n++; }
                }
                catch (Exception e) { MPLog.Debug($"driveshaft {name}: {e.Message}"); }
            try
            {
                var ops = typeof(Sprocket.PowertrainLayouts.PowertrainLayoutOps); var q = typeof(Sprocket.PowertrainLayouts.PowertrainLayoutQuery);
                h.Patch(AccessTools.Method(q, "GetMaxConnectionDiameter"), postfix: new HarmonyMethod(typeof(DriveshaftLimits), nameof(MaxPostfix))); n++;
                h.Patch(AccessTools.Method(ops, "SetConnectionFlags"), prefix: new HarmonyMethod(typeof(DriveshaftLimits), nameof(SetFlagsPrefix))); n++;
                h.Patch(AccessTools.Method(q, "GetConnectionFlags"), postfix: new HarmonyMethod(typeof(DriveshaftLimits), nameof(GetFlagsPostfix))); n++;
                h.Patch(AccessTools.Method(typeof(Sprocket.PowertrainLayouts.PowertrainResourceEstimation), "UpdateDriveshaftDiameters"), postfix: new HarmonyMethod(typeof(DriveshaftLimits), nameof(DiametersPostfix))); n++;
            }
            catch (Exception e) { MPLog.Warn("Driveshaft connection hooks: " + e.Message); }
            MPLog.Info($"Driveshaft thickness limit: {n} hooks");
        }

        static float Diameter(Sprocket.PowertrainLayouts.PowertrainLayout layout, int id)
        {
            if (layout == null || id < 0) return -1f;
            _inQuery = true;
            try { return Sprocket.PowertrainLayouts.PowertrainLayoutQuery.GetConnectionDiameter(layout, id); } catch { return -1f; }
            finally { _inQuery = false; }
        }
        static bool UnderLimit(Sprocket.PowertrainLayouts.PowertrainLayout layout, int id) { float d = Diameter(layout, id); return d >= 0f && d <= Limit + 1e-5f; }

        static void MaxPostfix(ref float __result)
        {
            if (!On) return;
            float gm = GameMax; if (gm <= 0f) return;
            // the game's result is its 400 mm (or less where something else limits it): scale it to the new limit
            __result = __result * Limit / gm;
            if (!_logged) { _logged = true; MPLog.Info($"Driveshaft limit: {Plugin.DriveshaftMaxMm.Value:0} mm (the game's is 400 mm = {gm} units)"); }
        }
        static void SetFlagsPrefix(Sprocket.PowertrainLayouts.PowertrainLayout __0, int __1, ref Sprocket.PowertrainLayouts.PortConnectionFlags __2)
        {
            if (!On || (__2 & Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick) == 0) return;
            if (UnderLimit(__0, __1)) __2 &= ~Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick;
        }
        static void GetFlagsPostfix(Sprocket.PowertrainLayouts.PowertrainLayout __0, int __1, ref Sprocket.PowertrainLayouts.PortConnectionFlags __result)
        {
            if (!On || _inQuery || (__result & Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick) == 0) return;
            if (UnderLimit(__0, __1)) __result &= ~Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick;
        }

        // the game decides "too thick" against its constant (inlined in its code): the connections' flag is corrected
        // right after it works the diameters out, and twice a second in the designer
        static void DiametersPostfix(Sprocket.PowertrainLayouts.PowertrainLayout __0) { if (On) FixLayout(__0); }
        static int FixLayout(Sprocket.PowertrainLayouts.PowertrainLayout layout)
        {
            int fixedN = 0;
            if (layout == null) return 0;
            try
            {
                var cs = layout.connections; if (cs == null) return 0;
                float lim = Limit;
                for (int i = 0; i < cs.Count; i++)
                {
                    var c = cs[i]; if (c == null) continue;
                    var f = c.connectionFlags;
                    if ((f & Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick) != 0 && c.diameter <= lim + 1e-5f)
                    { c.connectionFlags = f & ~Sprocket.PowertrainLayouts.PortConnectionFlags.TooThick; fixedN++; }
                }
            }
            catch (Exception e) { MPLog.Debug("driveshaft layout: " + e.Message); }
            return fixedN;
        }
        static float _nextScan; static DriveshaftBuilder[] _builders = new DriveshaftBuilder[0];
        /// <summary>Every frame outside battles: the game can mark a shaft again whenever it rebuilds the powertrain,
        /// so the builders found (every 2 s) are corrected every frame, before the warning shows.</summary>
        public static void Tick()
        {
            if (!On) return;
            if (BattleManager.Instance != null && BattleManager.Instance.InBattle) return;
            try
            {
                if (Time.realtimeSinceStartup >= _nextScan)
                {
                    _nextScan = Time.realtimeSinceStartup + 2f;
                    var found = UnityEngine.Object.FindObjectsOfType<DriveshaftBuilder>();
                    _builders = found != null ? System.Linq.Enumerable.ToArray(found) : new DriveshaftBuilder[0];
                }
                foreach (var b in _builders)
                {
                    if (b == null) continue;
                    int n = FixLayout(b.layout);
                    After(b);
                    if (n > 0 && !_loggedFix) { _loggedFix = true; MPLog.Info($"Driveshaft: {n} connection(s) under {Plugin.DriveshaftMaxMm.Value:0} mm no longer marked too thick"); }
                }
            }
            catch (Exception e) { MPLog.Debug("driveshaft tick: " + e.Message); _builders = new DriveshaftBuilder[0]; }
        }
        static bool _loggedFix;

        static void After(DriveshaftBuilder __instance)
        {
            if (!On) return;
            try
            {
                var shafts = __instance.driveShafts;
                if (shafts == null) return;
                var layout = __instance.layout;
                bool allOk = true, changed = false;
                for (int i = 0; i < shafts.Length; i++)
                {
                    var s = shafts[i]; if (s == null) continue;
                    if (s.tooThick && UnderLimit(layout, s.LayoutConnectionID)) { s.tooThick = false; changed = true; }
                    if (!(s.startAngleValid && s.endAngleValid && !s.obstructed && !s.tooThick)) allOk = false;
                }
                if (changed && allOk && !__instance.driveshaftValid) __instance.driveshaftValid = true;
            }
            catch (Exception e) { MPLog.Debug("driveshaft limit: " + e.Message); }
        }
    }
}

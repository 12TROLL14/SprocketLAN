using System;
using HarmonyLib;
using Sprocket.Engines.Editor;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Engine designer limits: more cylinders (the game stops at 24, the blueprint stores up to 255) and more
    /// displacement per cylinder (the game stops at 5 l). The properties' maximum (and its tech-based getter) is
    /// replaced after the engine properties are built or rebuilt. Off in F7 › Engine › UnlockLimits.
    /// </summary>
    public static class EngineLimits
    {
        static bool _logged;

        public static void Patch(Harmony h)
        {
            int n = 0;
            var t = typeof(EngineProperties);
            // (the game logs a harmless "Failed to init IL2CPP patch backend" warning for this constructor: it is patched the normal way)
            try { foreach (var c in t.GetConstructors()) if (c.GetParameters().Length == 4) { h.Patch(c, postfix: new HarmonyMethod(typeof(EngineLimits), nameof(Apply))); n++; } } catch (Exception e) { MPLog.Debug("engine limits ctor: " + e.Message); }
            foreach (var name in new[] { "OnRebuild", "SyncDependentValues", "UpdatePowerDisplay" })
                try { foreach (var m in t.GetMethods()) if (m.Name == name && m.DeclaringType == t) { h.Patch(m, postfix: new HarmonyMethod(typeof(EngineLimits), nameof(Apply))); n++; } } catch (Exception e) { MPLog.Debug($"engine limits {name}: {e.Message}"); }
            try
            {
                var bt = typeof(Sprocket.Vehicles.Engines.EngineBlueprint);
                foreach (var m in bt.GetMethods()) if (m.Name == "Load" && m.DeclaringType == bt && m.GetParameters().Length == 3) { h.Patch(m, postfix: new HarmonyMethod(typeof(EngineLimits), nameof(LoadPostfix))); n++; }
                foreach (var m in bt.GetMethods()) if (m.Name == "Save" && m.DeclaringType == bt && m.GetParameters().Length == 2) { h.Patch(m, prefix: new HarmonyMethod(typeof(EngineLimits), nameof(SavePrefix))); n++; }
            }
            catch (Exception e) { MPLog.Warn("Engine blueprint hooks: " + e.Message); }
            try
            {
                // the vehicle's engine applies the tech limits to its blueprint when the vehicle is built (24 cylinders, 5 l)
                var ct = typeof(Sprocket.Vehicles.Engines.CombustionEngine);
                foreach (var m in ct.GetMethods()) if (m.Name == "SyncBlueprintTech" && m.DeclaringType == ct) { h.Patch(m, prefix: new HarmonyMethod(typeof(EngineLimits), nameof(TechPrefix)), postfix: new HarmonyMethod(typeof(EngineLimits), nameof(TechPostfix))); n++; }
            }
            catch (Exception e) { MPLog.Warn("Engine tech hooks: " + e.Message); }
            MPLog.Info($"Engine limits: {n} hooks");
        }

        static void Apply(EngineProperties __instance)
        {
            if (!Plugin.EngineUnlock.Value) return;
            try
            {
                Raise(__instance.cylinderCount, Mathf.Clamp(Plugin.EngineMaxCylinders.Value, 1, 255));
                Raise(__instance.cylinderVolume, Mathf.Max(0.1f, Plugin.EngineMaxCylinderLitres.Value));
                // the editor clamps the loaded engine to the game's limits when it opens it (before the limits above
                // were raised): the values the file had are put back
                var bp = __instance.Target;
                if (bp != null && _edLogs < 20) { string k = $"{bp.Pointer}:{bp.CylinderCount}:{bp.CylinderVolume:0.##}"; if (k != _edLast) { _edLast = k; _edLogs++; MPLog.Info($"Engine editor: engine has {bp.CylinderCount} cylinders / {bp.CylinderVolume:0.##} l (last loaded from a file: {(_last.at > 0 ? $"{_last.c} / {_last.v:0.##} l" : "-")})"); } }
                (int c, float v, float at) lv = default; bool have = false;
                if (bp != null && _loaded.TryGetValue(bp.Pointer, out lv)) { have = true; _loaded.Remove(bp.Pointer); }
                else if (bp != null && _last.at > 0f && Time.realtimeSinceStartup - _last.at < 30f) { lv = _last; have = true; }   // the editor works on a copy of the loaded engine
                if (have)
                {
                    bool fix = false;
                    // only a value clamped right at the game's limit (not one the player lowered)
                    // through the editor's own properties first (like a player's edit: the engine model follows), then the
                    // blueprint itself if that didn't take
                    if (lv.c > bp.CylinderCount && bp.CylinderCount == 24) { try { __instance.cylinderCount.Value = lv.c; } catch { } if (bp.CylinderCount != lv.c) bp.CylinderCount = (byte)lv.c; fix = true; }
                    if (lv.v > bp.CylinderVolume + 1e-4f && Math.Abs(bp.CylinderVolume - 5f) < 0.01f) { try { __instance.cylinderVolume.Value = lv.v; } catch { } if (Math.Abs(bp.CylinderVolume - lv.v) > 1e-4f) bp.CylinderVolume = lv.v; fix = true; }
                    if (fix) _last = default;
                    if (fix)
                    {
                        // the engine's model (its size) is rebuilt when the blueprint is marked as changed
                        try { bp.MarkModified(); } catch (Exception e) { MPLog.Debug("engine mark modified: " + e.Message); }
                        try { __instance.SyncDependentValues(bp); } catch { }
                        try { __instance.UpdatePowerDisplay(bp); } catch { }
                        MPLog.Info($"Engine editor: {lv.c} cylinders / {lv.v:0.##} l put back (the editor had clamped them)");
                    }
                }
                if (!_logged) { _logged = true; MPLog.Info($"Engine limits raised: {Plugin.EngineMaxCylinders.Value} cylinders, {Plugin.EngineMaxCylinderLitres.Value} l per cylinder"); }
            }
            catch (Exception e) { MPLog.Debug("engine limits: " + e.Message); }
        }

        static int _saveLogs, _loadLogs;
        static readonly System.Collections.Generic.Dictionary<IntPtr, (int c, float v, float at)> _loaded = new System.Collections.Generic.Dictionary<IntPtr, (int, float, float)>();
        static (int c, float v, float at) _last; static int _edLogs; static string _edLast;
        static void SavePrefix(Sprocket.Vehicles.Engines.EngineBlueprint __instance)
        {
            if (!Plugin.EngineUnlock.Value || _saveLogs > 20) return;
            try { if (__instance.CylinderCount > 24 || __instance.CylinderVolume > 5f) { _saveLogs++; MPLog.Info($"Engine '{__instance.Name}' saved with {__instance.CylinderCount} cylinders, {__instance.CylinderVolume:0.##} l each"); } } catch { }
        }
        /// <summary>The game clamps the cylinder count / volume to its own limits when it loads an engine: the values
        /// written in the file are read again and kept (up to the mod's limits).</summary>
        static void LoadPostfix(Sprocket.Vehicles.Engines.EngineBlueprint __instance, Sprocket.Blueprints.IBlueprintDeserializer __0)
        {
            if (!Plugin.EngineUnlock.Value || __instance == null || __0 == null) return;
            try
            {
                int cur = __instance.CylinderCount; float vol = __instance.CylinderVolume;
                int raw = -1; float rawV = -1f;
                try { raw = __0.GetByte(Sprocket.Vehicles.Engines.EngineBlueprint.CylinderCountID); } catch { }
                if (raw <= 0) try { raw = __0.GetInt32(Sprocket.Vehicles.Engines.EngineBlueprint.CylinderCountID); } catch { }
                try { rawV = __0.GetSingle(Sprocket.Vehicles.Engines.EngineBlueprint.CylinderVolumeID); } catch { }
                int maxC = Mathf.Clamp(Plugin.EngineMaxCylinders.Value, 1, 255); float maxV = Mathf.Max(0.1f, Plugin.EngineMaxCylinderLitres.Value);
                bool changed = false;
                if (raw > cur) { __instance.CylinderCount = (byte)Math.Min(raw, maxC); changed = true; }
                if (rawV > vol + 1e-4f) { __instance.CylinderVolume = Math.Min(rawV, maxV); changed = true; }
                if (raw > 24 || rawV > 5f) { var e = (Math.Min(raw, maxC), Math.Min(Math.Max(rawV, vol), maxV), Time.realtimeSinceStartup); _loaded[__instance.Pointer] = e; _last = e; if (_loaded.Count > 500) _loaded.Clear(); }
                if ((changed || raw > 24 || rawV > 5f) && _loadLogs++ < 20) MPLog.Info($"Engine '{__instance.Name}' loaded: file {raw} cylinders / {rawV:0.##} l, game {cur} / {vol:0.##} l{(changed ? $" -> kept {__instance.CylinderCount} / {__instance.CylinderVolume:0.##} l" : "")}");
            }
            catch (Exception e) { MPLog.Debug("engine load: " + e.Message); }
        }

        static void TechPrefix(Sprocket.Vehicles.Engines.EngineBlueprint __0, out (int c, float v) __state)
        {
            __state = (-1, -1f);
            try { if (Plugin.EngineUnlock.Value && __0 != null) __state = (__0.CylinderCount, __0.CylinderVolume); } catch { }
        }
        static int _techLogs;
        static void TechPostfix(Sprocket.Vehicles.Engines.EngineBlueprint __0, (int c, float v) __state)
        {
            if (!Plugin.EngineUnlock.Value || __0 == null || __state.c < 0) return;
            try
            {
                int maxC = Mathf.Clamp(Plugin.EngineMaxCylinders.Value, 1, 255); float maxV = Mathf.Max(0.1f, Plugin.EngineMaxCylinderLitres.Value);
                bool fix = false;
                if (__0.CylinderCount < __state.c) { __0.CylinderCount = (byte)Math.Min(__state.c, maxC); fix = true; }
                if (__0.CylinderVolume < __state.v - 1e-4f) { __0.CylinderVolume = Math.Min(__state.v, maxV); fix = true; }
                if (fix && _techLogs++ < 20) MPLog.Info($"Engine: the game's tech limits would cut it; kept {__0.CylinderCount} cylinders / {__0.CylinderVolume:0.##} l");
            }
            catch (Exception e) { MPLog.Debug("engine tech: " + e.Message); }
        }

        static void Raise(Properties.FloatProperty p, float max)
        {
            if (p == null) return;
            if (p.getMax != null) p.getMax = null;           // tech-based maximum
            if (p.getSoftMax != null) p.getSoftMax = null;
            if (p.max < max) p.max = max;
            if (p.softMax < max) p.softMax = max;
        }
    }
}

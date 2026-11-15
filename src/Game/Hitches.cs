using System;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>Battle frames over 60 ms: logged with what the mod was last doing and whether a garbage collection ran
    /// (the game's or the mod's), at most once a second. F7 › Debug › LogHitches.</summary>
    public static class Hitches
    {
        static int _il2cppGc = -1, _netGc = -1; static float _nextLog; static string _lastStage = ""; static bool _gcFailed;
        public static void Tick()
        {
            if (!Plugin.HitchLog.Value) return;
            var bm = BattleManager.Instance; if (bm == null || !bm.InBattle) return;
            int ig = -1;
            if (!_gcFailed) try { ig = Il2CppSystem.GC.CollectionCount(0); } catch { _gcFailed = true; }
            int ng = System.GC.CollectionCount(0);
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.06f && _il2cppGc >= 0 && Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 1f;
                bool g1 = ig >= 0 && ig != _il2cppGc, g2 = ng != _netGc;
                MPLog.Info($"Hitch: {dt * 1000f:0} ms frame{(g1 ? ", the game's garbage collector ran" : "")}{(g2 ? ", the mod's (.NET) garbage collector ran" : "")}; last stage: {MPLog.Stage}");
            }
            _il2cppGc = ig; _netGc = ng; _lastStage = MPLog.Stage ?? "";
        }
    }
}

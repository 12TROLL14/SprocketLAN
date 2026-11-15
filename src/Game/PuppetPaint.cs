using System;
using HarmonyLib;

namespace SprocketMP
{
    /// <summary>
    /// Multiplayer battles start from a custom battle setup, where every team can have a paint scheme: the game paints
    /// that team's vehicles with it instead of their own paint job (the other players' tanks came out grey, without
    /// their paint and decals). In multiplayer battles the team scheme is dropped, so every tank wears its own paint.
    /// F7 › Battle › TanksKeepOwnPaint.
    /// </summary>
    public static class PuppetPaint
    {
        static int _logs;
        public static void Patch(Harmony h)
        {
            int n = 0;
            var t = typeof(VehicleDesigner.PaintJobs.PaintBlueprintAssemblyStage);
            try
            {
                foreach (var c in t.GetConstructors()) if (c.GetParameters().Length == 2) { h.Patch(c, prefix: new HarmonyMethod(typeof(PuppetPaint), nameof(CtorPrefix))); n++; }
                // No hook on set_paintjobOverride: in the interop assembly it's a field accessor (writes the native
                // field directly), which Harmony can't patch, and the game's own code never goes through it anyway.
            }
            catch (Exception e) { MPLog.Warn("Paint override hooks: " + e.Message); }
            MPLog.Info($"Paint override: {n} hooks");
        }
        static bool Active => Plugin.KeepOwnPaint.Value && (Lobby.InLobbyBattle || (Runner.Session != null && Runner.Session.Active));
        static void CtorPrefix(ref VehicleDesigner.PaintJobs.PaintJob __1)
        {
            if (__1 == null) return;
            if (_logs++ < 10) MPLog.Info($"Paint: a vehicle was given a team paint scheme{(Active ? ", dropped (it keeps its own)" : "")}");
            if (Active) __1 = null;
        }
    }
}

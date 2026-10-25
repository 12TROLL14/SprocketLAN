using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Sprocket.Vehicles.Weapons;

namespace SprocketMP
{
    /// <summary>Simple "stabiliser": your turret traverse and gun elevation get several times the speed, torque and
    /// acceleration (the game's own turret/laying-drive settings, restored when switched off).</summary>
    public static class Stabilizer
    {
        public static bool On;
        static bool _applied; static IntPtr _appliedTo;
        static readonly Dictionary<IntPtr, float[]> _orig = new Dictionary<IntPtr, float[]>();
        static bool _announced; static float _lastT, _lastE;

        public static void Toggle() { On = !On; MPLog.Info($"Stabiliser {(On ? "ON" : "OFF")}"); }

        public static void Tick(VehicleRig local)
        {
            if (!_announced) { _announced = true; MPLog.Info($"Stabiliser ready: key {Plugin.StabKey.Value}, traverse x{Plugin.StabMultiplier.Value:F1}, elevation x{Plugin.StabElevationMultiplier.Value:F1}"); }
            try
            {
                var kb = Keyboard.current;
                if (kb != null && Enum.TryParse<Key>(Plugin.StabKey.Value, true, out var k) && k != Key.None && kb[k].wasPressedThisFrame && !UI.MenuUI.AnyFieldFocusedStatic()) Toggle();
            }
            catch (Exception e) { MPLog.Debug("stab key: " + e.Message); }
            var tank = local?.GO != null ? local.GO.Pointer : IntPtr.Zero;
            if (_applied && (!On || tank != _appliedTo)) { Apply(local, false); }
            float mt = Plugin.StabMultiplier.Value, me = Plugin.StabElevationMultiplier.Value;
            bool retune = _applied && (mt != _lastT || me != _lastE);   // changed in the settings panel
            if (On && local != null && (!_applied || tank != _appliedTo || retune)) { Apply(local, true); _lastT = mt; _lastE = me; }
        }

        static void Apply(VehicleRig local, bool on)
        {
            float m = Mathf.Clamp(Plugin.StabMultiplier.Value, 1f, 20f), me = Mathf.Clamp(Plugin.StabElevationMultiplier.Value, 1f, 30f);
            int n = 0;
            try
            {
                if (local != null)
                    foreach (var p in local.AimBehaviourPtrs)
                    {
                        if (p == IntPtr.Zero) continue;
                        try
                        {
                            var obj = new Il2CppSystem.Object(p);
                            var tb = obj.TryCast<TurretBehaviour>();
                            if (tb != null)
                            {
                                if (on) { if (!_orig.ContainsKey(p)) _orig[p] = new[] { tb.MaxTraverseRate, tb.MaxAcceleration, tb.MaxBrakeAcceleration, tb.MotorTorqueMultiplier };
                                          var o = _orig[p]; tb.MaxTraverseRate = o[0] * m; tb.MaxAcceleration = o[1] * m * m; tb.MaxBrakeAcceleration = o[2] * m * m; tb.MotorTorqueMultiplier = o[3] * m * m; }
                                else if (_orig.TryGetValue(p, out var o)) { tb.MaxTraverseRate = o[0]; tb.MaxAcceleration = o[1]; tb.MaxBrakeAcceleration = o[2]; tb.MotorTorqueMultiplier = o[3]; }
                                n++; continue;
                            }
                            var ld = obj.TryCast<LayingDriveBehaviour>();
                            if (ld != null)
                            {
                                var el = ld.elevationInfo; var az = ld.azimuthInfo;
                                if (on && !_orig.ContainsKey(p)) _orig[p] = new[] { el.MotorTorque, el.MaxSpeed, az.MotorTorque, az.MaxSpeed };
                                if (_orig.TryGetValue(p, out var o))
                                {
                                    float f = on ? m : 1f;
                                    float fe = on ? me : 1f;
                                    ld.elevationInfo = new RotationAxisInfo(o[0] * fe * fe, o[1] * fe, el.Min, el.Max);
                                    ld.azimuthInfo = new RotationAxisInfo(o[2] * f * f, o[3] * f, az.Min, az.Max);
                                }
                                n++;
                            }
                        }
                        catch (Exception e) { MPLog.Warn("stabiliser: " + e.Message); }
                    }
            }
            catch (Exception e) { MPLog.Warn("stabiliser: " + e.Message); }
            _applied = on; _appliedTo = on && local?.GO != null ? local.GO.Pointer : IntPtr.Zero;
            if (!on) _orig.Clear();
            MPLog.Info($"Stabiliser {(on ? $"applied (x{m:F1})" : "removed")} on {n} turret / gun drives");
        }
    }
}

using System;
using System.Collections.Generic;
using Sprocket.Engines;
using Sprocket.Powertrains.Engines;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// A destroyed engine latches 'shut down: Destroyed' in its behaviour; healing its pool afterwards (field repair)
    /// doesn't clear that latch, so the engine stays at 0 RPM. When an engine has health again but is still latched
    /// as destroyed, clear the latch and start it.
    /// </summary>
    public static class EngineRevive
    {
        static float _next;
        static IntPtr _rigPtr;
        static readonly List<Sprocket.Vehicles.Engines.CombustionEngine> _engines = new List<Sprocket.Vehicles.Engines.CombustionEngine>();

        public static void Tick(VehicleRig rig)
        {
            if (rig == null || !rig.Alive) { _rigPtr = IntPtr.Zero; _engines.Clear(); return; }
            float now = Time.time; if (now < _next) return; _next = now + 0.5f;
            try
            {
                if (_rigPtr != rig.GO.Pointer) { _engines.Clear(); _engines.AddRange(rig.GO.GetComponentsInChildren<Sprocket.Vehicles.Engines.CombustionEngine>(true)); _rigPtr = rig.GO.Pointer; }
                Revive(rig, false);
            }
            catch (Exception e) { MPLog.Warn("EngineRevive: " + e.Message); }
        }

        /// <summary>Clear the 'destroyed' latch on every engine of the rig that has health. Returns the number restarted.</summary>
        public static int Revive(VehicleRig rig, bool force)
        {
            int n = 0;
            if (rig?.GO == null) return 0;
            var list = _rigPtr == rig.GO.Pointer && _engines.Count > 0 ? _engines : new List<Sprocket.Vehicles.Engines.CombustionEngine>(rig.GO.GetComponentsInChildren<Sprocket.Vehicles.Engines.CombustionEngine>(true));
            foreach (var c in list)
            {
                try
                {
                    if (c == null || c.WasCollected) continue;
                    var eb = c.Behaviour?.TryCast<EngineBehaviour>(); if (eb == null) continue;
                    float frac = 1f; try { var h = eb.health; if (h != null) frac = h.HealthInfo.Fraction; } catch { }
                    var reason = eb.shutdownReason; var st = eb.state;
                    bool latched = (reason & EngineShutdownReason.Destroyed) != 0;
                    if (frac < 0.05f || !(latched || (force && st != EngineState.Running))) continue;
                    float rpm0 = 0f; try { rpm0 = eb.RPM; } catch { }
                    eb.shutdownReason = EngineShutdownReason.None;
                    try { var j = eb.physicsJob; j.healthFraction = frac; eb.physicsJob = j; } catch (Exception e) { MPLog.Warn("engine job: " + e.Message); }
                    try { eb.ignitionFraction = 1f; } catch { }
                    eb.requestedState = EngineState.Running;
                    try { eb.SetStateInternal(EngineState.Running); } catch (Exception e) { MPLog.Warn("engine state: " + e.Message); eb.state = EngineState.Running; }
                    try { rig.VB.Flags |= VehicleFlags.Mobile | VehicleFlags.SelfPropelled; } catch { }
                    try { rig.Driver?.StartupInstant(); } catch { }
                    n++;
                    MPLog.Info($"Engine '{c.name}' on {rig.RawName} revived: health {frac:P0}, was {st}/{reason} at {rpm0:F0} rpm -> {eb.state}/{eb.shutdownReason}");
                }
                catch (Exception e) { MPLog.Warn("engine revive: " + e.Message); }
            }
            return n;
        }
    }
}

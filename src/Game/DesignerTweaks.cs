using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Vehicle designer helpers:
    /// - the orbit camera: its clip prevention (pushing the camera out of the vehicle's geometry) makes it jump around
    ///   very big vehicles, so it can be turned off; the zoom-out and pan limits can be raised;
    /// - a performance probe: when the designer runs slowly, the log says where the frame time goes (the editor's
    ///   update, its selection gizmos, the mod) and how many gizmos the editor draws.
    /// </summary>
    public static class DesignerTweaks
    {
        // =============================================================== performance probe
        sealed class Probe { public string Name; public long Ticks, Start; public int Calls; public bool Timed; }
        static readonly Dictionary<MethodBase, Probe> _probes = new Dictionary<MethodBase, Probe>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static int _frames; static double _frameMs, _worstMs, _modMs; static float _nextReport;
        static bool _patched;

        public static void Patch(Harmony h)
        {
            if (_patched) return; _patched = true;
            int ok = 0;
            void Timed(Type t, string name, Type[] args = null)
            {
                try
                {
                    var ms = args != null ? new[] { (MethodBase)AccessTools.Method(t, name, args) } : t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == name).Cast<MethodBase>().ToArray();
                    foreach (var m in ms)
                    {
                        if (m == null || _probes.ContainsKey(m)) continue;
                        _probes[m] = new Probe { Name = t.Name + "." + name, Timed = true };
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DesignerTweaks), nameof(TimedPrefix)), postfix: new HarmonyMethod(typeof(DesignerTweaks), nameof(TimedPostfix)));
                        ok++;
                    }
                }
                catch (Exception e) { MPLog.Debug($"designer probe {t.Name}.{name}: {e.Message}"); }
            }
            void Counted(Type t, string name)
            {
                try
                {
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
                    {
                        if (_probes.ContainsKey(m)) continue;
                        _probes[m] = new Probe { Name = t.Name + "." + name };
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DesignerTweaks), nameof(CountPrefix)));
                        ok++;
                    }
                }
                catch (Exception e) { MPLog.Debug($"designer probe {t.Name}.{name}: {e.Message}"); }
            }
            var fe = typeof(Sprocket.Vehicles.PlateStructures.Design.FreeformPlateStructureEditor);
            Timed(fe, "Update", new[] { typeof(Sprocket.MeshEditing.MeshOperatorContext) });
            Timed(fe, "OnEditGUI"); Timed(fe, "DrawGizmos"); Timed(fe, "OnSelectionChanged");
            Timed(fe, "UpdateSelectionInfo"); Timed(fe, "UpdateVertexSelectInfo"); Timed(fe, "UpdateFaceSelectInfo"); Timed(fe, "OnComponentRebuilt");
            var pe = typeof(Sprocket.Vehicles.PlateStructures.Design.PlateStructureEditor);
            Timed(pe, "UpdateGizmos"); Timed(pe, "DrawGizmos");
            var vis = typeof(Sprocket.GizmoRendering.Visualizer);
            Timed(vis, "Apply"); Timed(vis, "Clear");
            Timed(typeof(Sprocket.GizmoRendering.EdgeRenderer), "Apply");
            Timed(typeof(Sprocket.GizmoRendering.UIGizmos), "Apply");
            Timed(typeof(Sprocket.GizmoRendering.MeshGizmos), "Apply");
            Timed(typeof(Sprocket.GizmoRendering.ProceduralMeshRenderer), "Apply");
            foreach (var n in new[] { "DrawGUI", "DrawGUI2D", "DrawGUILine", "DrawEdge", "DrawSphere", "DrawScreenText", "DrawMesh", "DrawWireCube", "DrawCube", "DrawDisc" }) Counted(vis, n);
            MPLog.Info($"Designer: performance probe on ({ok} hooks)");
        }

        static void TimedPrefix(MethodBase __originalMethod) { if (_probes.TryGetValue(__originalMethod, out var p)) p.Start = Clock.ElapsedTicks; }
        static void TimedPostfix(MethodBase __originalMethod) { if (_probes.TryGetValue(__originalMethod, out var p) && p.Start != 0) { p.Ticks += Clock.ElapsedTicks - p.Start; p.Start = 0; p.Calls++; } }
        static void CountPrefix(MethodBase __originalMethod) { if (_probes.TryGetValue(__originalMethod, out var p)) p.Calls++; }

        /// <summary>Every frame (main thread), with how long the mod's own update took.</summary>
        public static void Tick(double modMs)
        {
            try { CameraTick(); } catch (Exception e) { MPLog.Debug("designer camera: " + e.Message); }
            if (!_patched) return;
            bool designer = ModelTools.EditorOpen || (BattleManager.Instance != null && !BattleManager.Instance.InBattle && _cam != null);
            if (!designer || !Plugin.DesignerPerfLog.Value) { Reset(); return; }
            double f = Time.unscaledDeltaTime * 1000.0;
            _frames++; _frameMs += f; _modMs += modMs; if (f > _worstMs) _worstMs = f;
            if (Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = Time.realtimeSinceStartup + 5f;
            double avg = _frames > 0 ? _frameMs / _frames : 0;
            if (_frames > 0 && avg > Plugin.DesignerPerfLogAboveMs.Value)
            {
                var sb = new StringBuilder($"Designer slow: {avg:F0} ms a frame ({1000 / Math.Max(1, avg):F0} fps, worst {_worstMs:F0} ms), structure editor {(ModelTools.EditorOpen ? "open" : "closed")}. Per frame: mod {_modMs / _frames:F2} ms");
                foreach (var p in _probes.Values.GroupBy(x => x.Name).Select(g => (name: g.Key, ms: g.Sum(x => x.Ticks) * 1000.0 / Stopwatch.Frequency / _frames, calls: g.Sum(x => x.Calls) / (double)_frames, timed: g.First().Timed)).OrderByDescending(x => x.ms).ThenByDescending(x => x.calls))
                {
                    if (p.calls < 0.01) continue;
                    sb.Append(p.timed ? $"; {p.name} {p.ms:F2} ms ({p.calls:0.##}x)" : $"; {p.name} {p.calls:0.#}x");
                }
                MPLog.Info(sb.ToString());
            }
            Reset();
        }
        static void Reset()
        {
            _frames = 0; _frameMs = _worstMs = _modMs = 0;
            foreach (var p in _probes.Values) { p.Ticks = 0; p.Calls = 0; }
        }

        // =============================================================== designer orbit camera
        static Sprocket.OrbitalMovementController _cam; static IntPtr _camPtr; static float _nextCam;
        static int _origMask; static float _origMaxDist, _origMaxPan;

        static void Restore()
        {
            if (_cam != null)
                try { var lm = _cam.clipPreventionLayers; lm.value = _origMask; _cam.clipPreventionLayers = lm; _cam.maxDistance = _origMaxDist; _cam.maxPanDistance = _origMaxPan; } catch { }
            _cam = null; _camPtr = IntPtr.Zero;
        }

        static void CameraTick()
        {
            var bm = BattleManager.Instance;
            if (bm != null && bm.InBattle) { Restore(); return; }   // only the designer / menus: battle cameras keep the game's values
            if (Time.realtimeSinceStartup < _nextCam) return;
            _nextCam = Time.realtimeSinceStartup + 2f;
            var c = UnityEngine.Object.FindObjectOfType<Sprocket.OrbitalMovementController>();
            if (c == null) { _cam = null; _camPtr = IntPtr.Zero; return; }
            if (c.Pointer != _camPtr)
            {
                _cam = c; _camPtr = c.Pointer;
                _origMask = c.clipPreventionLayers.value; _origMaxDist = c.maxDistance; _origMaxPan = c.maxPanDistance;
                MPLog.Info($"Designer camera: distance {c.minDistance:0.#}-{c.maxDistance:0.#} m, pan up to {c.maxPanDistance:0.#} m, min pan height {c.minPanHeight:0.##} m, clip prevention layers {_origMask} (padding {c.clipPreventionPadding:0.##} m), smoothing {c.SmoothingEnabled} ({c.smoothPeriod:0.##} s), position based {c.PositionBasedSmoothing}");
            }
            int mask = Plugin.DesignerCamClip.Value ? _origMask : 0;
            if (c.clipPreventionLayers.value != mask) { var lm = c.clipPreventionLayers; lm.value = mask; c.clipPreventionLayers = lm; }
            float maxD = Mathf.Max(_origMaxDist, Plugin.DesignerCamMaxDistance.Value), maxP = Mathf.Max(_origMaxPan, Plugin.DesignerCamMaxPan.Value);
            if (Mathf.Abs(c.maxDistance - maxD) > 0.01f) c.maxDistance = maxD;
            if (Mathf.Abs(c.maxPanDistance - maxP) > 0.01f) c.maxPanDistance = maxP;
        }
    }
}

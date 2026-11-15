using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.GizmoRendering;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP
{
    /// <summary>
    /// Faster selection display in the structure editor.
    ///
    /// The editor draws the selected structure's edges and points as one game object each (a LineRenderer per edge,
    /// a cube per point), and redraws all of them whenever anything changes, every frame while the mouse moves over
    /// the vehicle. On a big structure that's 20 000 edges and 8 000 points a frame (measured on a Ratte body: 175 ms
    /// a frame in the editor's DrawGizmos alone, 4 fps).
    ///
    /// For big structures, the mod records what the editor draws, without creating the objects, and turns it into a few
    /// meshes: one line mesh and one cube mesh per colour, drawn with the game's own gizmo renderer. Later redraws reuse
    /// them, and they are recorded again when the selection, the mesh or the select mode changes, the structure moves,
    /// and (while the mouse moves, for the hover highlight) a few times a second. Small structures are left alone.
    /// </summary>
    public static class FastGizmos
    {
        enum Pass { None, Count, Capture, Cached }
        static Pass _pass;
        static int _count, _others;
        static bool _fast, _dirty = true;
        static IntPtr _editor;
        static float _lastCapture = -10f, _mouseAt = -10f;
        static Vector2 _mouse;
        static Matrix4x4 _lastMatrix;

        struct Line { public Vector3 A, B; public Color32 C; }
        struct Box { public Vector3 P; public Quaternion R; public Vector3 S; public Color32 C; public Material M; }
        struct Wire { public Matrix4x4 M; public Color C; public Vector3 P; public Quaternion R; public Vector3 S; public bool F; }
        static readonly List<Line> _lines = new List<Line>();
        static readonly List<Box> _boxes = new List<Box>();
        static readonly List<Wire> _wires = new List<Wire>();
        sealed class Group { public Mesh Mesh; public Color Colour; public Material Mat; }
        static readonly List<Group> _groups = new List<Group>();
        static readonly List<Mesh> _meshPool = new List<Mesh>();

        static bool On => Plugin.FastGizmos.Value;
        static int MinItems => Mathf.Max(100, Plugin.FastGizmosMinItems.Value);

        public static void Patch(Harmony h)
        {
            int n = 0;
            try
            {
                var fe = typeof(FreeformPlateStructureEditor);
                h.Patch(AccessTools.Method(fe, nameof(FreeformPlateStructureEditor.DrawGizmos)), prefix: new HarmonyMethod(typeof(FastGizmos), nameof(DrawPrefix)), postfix: new HarmonyMethod(typeof(FastGizmos), nameof(DrawPostfix))); n++;
                foreach (var name in new[] { "OnSelectionChanged", "OnComponentRebuilt", "SetSelectMode" })
                    try { foreach (var m in fe.GetMethods()) if (m.Name == name && m.DeclaringType == fe) { h.Patch(m, postfix: new HarmonyMethod(typeof(FastGizmos), nameof(Dirty))); n++; } } catch (Exception e) { MPLog.Debug($"fast gizmos {name}: {e.Message}"); }
                var vis = typeof(Visualizer);
                foreach (var m in vis.GetMethods())
                {
                    if (m.DeclaringType != vis) continue;
                    var ps = m.GetParameters();
                    string pre = null;
                    if (m.Name == "DrawEdge" && ps.Length == 5 && ps[0].ParameterType == typeof(Vector3)) pre = nameof(EdgePrefix);
                    else if (m.Name == "DrawEdge" && ps.Length >= 4 && ps[0].ParameterType == typeof(Il2CppStructArray<Vector3>)) pre = nameof(PolyPrefix);
                    else if (m.Name == "DrawCube" && ps.Length == 4) pre = nameof(CubePrefix);
                    else if (m.Name == "DrawWireCube" && ps.Length == 4) pre = nameof(WirePrefix);
                    else if (m.Name.StartsWith("Draw") && m.Name != "DrawEdge" && m.Name != "DrawCube" && m.Name != "DrawWireCube" && !m.Name.Contains("_")) pre = nameof(OtherPrefix);
                    if (pre == null) continue;
                    try { h.Patch(m, prefix: new HarmonyMethod(typeof(FastGizmos), pre)); n++; } catch (Exception e) { MPLog.Debug($"fast gizmos {m.Name}: {e.Message}"); }
                }
                try
                {
                    var me = typeof(Sprocket.MeshEditing.MeshEditor);
                    foreach (var m in me.GetMethods()) if ((m.Name == "DrawGizmos" || m.Name == "UpdateGizmos") && m.DeclaringType == me) { h.Patch(m, prefix: new HarmonyMethod(typeof(FastGizmos), nameof(MeshDrawPrefix))); n++; }
                }
                catch (Exception e) { MPLog.Debug("mesh editor gizmos: " + e.Message); }
                MPLog.Info($"Designer: fast selection display hooked ({n} hooks)");
            }
            catch (Exception e) { MPLog.Warn("Fast selection display: " + e.Message); }
        }

        static void Dirty() { _dirty = true; }

        // ------------------------------------------------------------------ the editor's redraw
        static bool DrawPrefix(FreeformPlateStructureEditor __instance, IGizmoDrawer __0)
        {
            _pass = Pass.None; SkipMesh = false;
            if (!On) { _fast = false; return true; }
            Visualizer vis = null;
            try { vis = __0?.TryCast<Visualizer>(); } catch { }
            if (vis == null) return true;
            // Points / Edges modes on a big structure: our own display, the game's redraw is skipped
            // (the editor's redraw still runs, for the move / rotate / scale handles and anything else it draws; only
            //  the points and edges it would draw one by one are dropped, and the mesh editor's own drawing skipped)
            try { if (SelectionView.Take(__instance, vis)) { _pass = Pass.None; SkipMesh = true; return true; } } catch (Exception e) { MPLog.Debug("selection view: " + e.Message); }
            if (SelectionView.Calibrating) { _pass = Pass.Count; _count = 0; _others = 0; return true; }   // the game draws once: sizes measured

            if (__instance.Pointer != _editor) { _editor = __instance.Pointer; _fast = false; _dirty = true; }
            Matrix4x4 m = default; try { m = __instance.Component.transform.localToWorldMatrix; } catch { }
            if (m != _lastMatrix) { _lastMatrix = m; _dirty = true; }
            float now = Time.realtimeSinceStartup;
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var p = mouse.position.ReadValue();
                if ((p - _mouse).sqrMagnitude > 1f || mouse.leftButton.isPressed || mouse.rightButton.isPressed) _mouseAt = now;
                _mouse = p;
            }

            if (!_fast) { _pass = Pass.Count; _count = 0; _others = 0; return true; }   // small structure (or not measured yet): the game's own way

            bool hover = now - _mouseAt < 0.5f && now - _lastCapture >= Mathf.Max(0.05f, Plugin.FastGizmosRefresh.Value);
            if (_dirty || hover || _groups.Count == 0)
            {
                _pass = Pass.Capture; _dirty = false; _lastCapture = now;
                _lines.Clear(); _boxes.Clear(); _wires.Clear(); _count = 0; _others = 0;
                return true;
            }
            _pass = Pass.Cached;
            Submit(vis, true);
            return false;
        }

        public static bool SkipMesh;
        static bool MeshDrawPrefix() => !SkipMesh;
        static void DrawPostfix(IGizmoDrawer __0)
        {
            SkipMesh = false;
            var pass = _pass; _pass = Pass.None;
            if (pass == Pass.Count && SelectionView.Calibrating) { SelectionView.EndCalibration(_others > 0); return; }
            if (pass == Pass.Count)
            {
                if (_others == 0 && _count >= MinItems) { _fast = true; _dirty = true; MPLog.Info($"Designer: big selection ({_count} edges / points): fast selection display on"); }
                return;
            }
            if (pass != Pass.Capture) return;
            Visualizer vis = null; try { vis = __0?.TryCast<Visualizer>(); } catch { }
            if (_others > 0 || _count < MinItems / 2)
            {
                // the editor drew something we don't record, or the structure got small: back to the game's own way
                _fast = false; ClearGroups();
                if (_others > 0) MPLog.Info("Designer: the editor draws other gizmos here: fast selection display off for this structure");
                return;
            }
            try { Build(); } catch (Exception e) { MPLog.Warn("Fast selection display: " + e.Message); _fast = false; ClearGroups(); return; }
            if (vis != null) Submit(vis, false);
        }

        // ------------------------------------------------------------------ recording (instead of one object each)
        static bool EdgePrefix(Visualizer __instance, Vector3 __0, Vector3 __1, float __2)
        {
            if (SkipMesh) return false;
            if (_pass == Pass.Count) { _count++; if (SelectionView.Calibrating) try { SelectionView.Sample(__2, __instance.Matrix.MultiplyPoint3x4((__0 + __1) * 0.5f), true); } catch { } return true; }
            if (_pass != Pass.Capture) return true;   // our own replays on reused frames go through
            var m = __instance.Matrix; Color32 c = __instance.Colour;
            _lines.Add(new Line { A = m.MultiplyPoint3x4(__0), B = m.MultiplyPoint3x4(__1), C = c }); _count++;
            return false;
        }
        static bool PolyPrefix(Visualizer __instance, Il2CppStructArray<Vector3> __0)
        {
            if (SkipMesh) return false;
            if (_pass == Pass.Count) { _count++; return true; }
            if (_pass != Pass.Capture) return true;   // our own replays on reused frames go through
            if (__0 == null || __0.Length < 2) return false;
            var m = __instance.Matrix; Color32 c = __instance.Colour;
            Vector3 prev = m.MultiplyPoint3x4(__0[0]);
            for (int i = 1; i < __0.Length; i++) { var p = m.MultiplyPoint3x4(__0[i]); _lines.Add(new Line { A = prev, B = p, C = c }); prev = p; }
            _count++;
            return false;
        }
        static bool CubePrefix(Visualizer __instance, Vector3 __0, Quaternion __1, Vector3 __2, Material __3)
        {
            if (SkipMesh) return false;
            if (_pass == Pass.Count) { _count++; if (SelectionView.Calibrating) try { var mx = __instance.Matrix; SelectionView.Sample(Vector3.Scale(mx.lossyScale, __2).x, mx.MultiplyPoint3x4(__0), false); } catch { } return true; }
            if (_pass != Pass.Capture) return true;   // our own replays on reused frames go through
            var m = __instance.Matrix;
            _boxes.Add(new Box { P = m.MultiplyPoint3x4(__0), R = m.rotation * __1, S = Vector3.Scale(m.lossyScale, __2), C = __instance.Colour, M = __3 }); _count++;
            return false;
        }
        static bool WirePrefix(Visualizer __instance, Vector3 __0, Quaternion __1, Vector3 __2, bool __3)
        {
            if (SkipMesh) return false;
            if (_pass == Pass.Capture) _wires.Add(new Wire { M = __instance.Matrix, C = __instance.Colour, P = __0, R = __1, S = __2, F = __3 });
            return true;
        }
        static void OtherPrefix() { if (_pass == Pass.Count || _pass == Pass.Capture) _others++; }

        // ------------------------------------------------------------------ meshes
        static void ClearGroups() { foreach (var g in _groups) if (g.Mesh != null) _meshPool.Add(g.Mesh); _groups.Clear(); }
        static Mesh NewMesh()
        {
            Mesh m;
            if (_meshPool.Count > 0) { m = _meshPool[_meshPool.Count - 1]; _meshPool.RemoveAt(_meshPool.Count - 1); m.Clear(); }
            else { m = new Mesh(); m.hideFlags = HideFlags.HideAndDontSave; m.MarkDynamic(); }
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            return m;
        }
        static int Key(Color32 c) => c.r | (c.g << 8) | (c.b << 16) | (c.a << 24);

        static void Build()
        {
            ClearGroups();
            // lines, one mesh per colour
            var byCol = new Dictionary<int, List<int>>();
            for (int i = 0; i < _lines.Count; i++) { int k = Key(_lines[i].C); if (!byCol.TryGetValue(k, out var l)) byCol[k] = l = new List<int>(); l.Add(i); }
            foreach (var kv in byCol)
            {
                var v = new Vector3[kv.Value.Count * 2]; var idx = new int[v.Length];
                for (int j = 0; j < kv.Value.Count; j++) { var e = _lines[kv.Value[j]]; v[2 * j] = e.A; v[2 * j + 1] = e.B; idx[2 * j] = 2 * j; idx[2 * j + 1] = 2 * j + 1; }
                var mesh = NewMesh(); mesh.vertices = v; mesh.SetIndices(idx, MeshTopology.Lines, 0); mesh.RecalculateBounds();
                _groups.Add(new Group { Mesh = mesh, Colour = _lines[kv.Value[0]].C, Mat = null });
            }
            // cubes, one mesh per colour + material
            var byBox = new Dictionary<(int, IntPtr), List<int>>();
            for (int i = 0; i < _boxes.Count; i++) { var k = (Key(_boxes[i].C), _boxes[i].M?.Pointer ?? IntPtr.Zero); if (!byBox.TryGetValue(k, out var l)) byBox[k] = l = new List<int>(); l.Add(i); }
            foreach (var kv in byBox)
            {
                int n = kv.Value.Count;
                var v = new Vector3[n * 24]; var nr = new Vector3[n * 24]; var idx = new int[n * 36];
                for (int j = 0; j < n; j++) CubeInto(_boxes[kv.Value[j]], v, nr, idx, j);
                var mesh = NewMesh(); mesh.vertices = v; mesh.normals = nr; mesh.SetIndices(idx, MeshTopology.Triangles, 0); mesh.RecalculateBounds();
                var b0 = _boxes[kv.Value[0]];
                _groups.Add(new Group { Mesh = mesh, Colour = b0.C, Mat = b0.M });
            }
        }

        static readonly Vector3[] FaceN = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static void CubeInto(Box b, Vector3[] v, Vector3[] nr, int[] idx, int j)
        {
            var h = b.S * 0.5f;
            for (int f = 0; f < 6; f++)
            {
                var n = FaceN[f];
                var u = f < 2 ? Vector3.up : Vector3.right;
                var w = Vector3.Cross(n, u);
                int o = j * 24 + f * 4;
                for (int c = 0; c < 4; c++)
                {
                    float su = (c == 1 || c == 2) ? 1 : -1, sw = c >= 2 ? 1 : -1;
                    var local = Vector3.Scale(n + u * su + w * sw, h);
                    v[o + c] = b.P + b.R * local; nr[o + c] = b.R * n;
                }
                int t = j * 36 + f * 6;
                // front side outwards (Unity: the normal of (a, b, c) is cross(b - a, c - a))
                bool flip = Vector3.Dot(Vector3.Cross(v[o + 1] - v[o], v[o + 2] - v[o]), nr[o]) < 0f;
                int i1 = flip ? 3 : 1, i3 = flip ? 1 : 3;
                idx[t] = o; idx[t + 1] = o + i1; idx[t + 2] = o + 2; idx[t + 3] = o; idx[t + 4] = o + 2; idx[t + 5] = o + i3;
            }
        }

        /// <summary>Hand the meshes (and, on reused frames, the editor's few other gizmos) to the game's gizmo renderer.</summary>
        static void Submit(Visualizer vis, bool replay)
        {
            try
            {
                var mg = vis.meshGizmoRenderer;
                if (mg != null) foreach (var g in _groups) mg.Draw(g.Mesh, Vector3.zero, Quaternion.identity, Vector3.one, g.Colour, g.Mat);
                if (replay && _wires.Count > 0)
                {
                    var m0 = vis.Matrix; var c0 = vis.Colour;
                    foreach (var w in _wires) { vis.Matrix = w.M; vis.Colour = w.C; vis.DrawWireCube(w.P, w.R, w.S, w.F); }
                    vis.Matrix = m0; vis.Colour = c0;
                }
            }
            catch (Exception e) { MPLog.Warn("Fast selection display draw: " + e.Message); _fast = false; ClearGroups(); }
        }
    }
}

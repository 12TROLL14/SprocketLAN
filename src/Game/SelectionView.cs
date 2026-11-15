using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sprocket.GizmoRendering;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP
{
    /// <summary>
    /// The structure editor's selection display, drawn the way 3D programs do it, for big structures (Points, Edges and
    /// Faces modes; Corners is left to the game).
    ///
    /// The game rebuilds every point and edge as its own game object (a cube / a LineRenderer) on every redraw. Instead
    /// the mod reads the mesh from the editor once and keeps it in a few static meshes, drawn with the game's own gizmo
    /// material:
    /// - edges: one line mesh (MeshTopology.Lines) for normal and one for selected edges, so they don't depend on the
    ///   camera at all;
    /// - points (Points mode): small cubes, resized only when the camera distance changes a lot;
    /// - selected faces (Faces mode): a filled mesh.
    /// Nothing is rebuilt while nothing changes: the topology when the mesh changes, the selection when it changes,
    /// the selected points while dragging. The game's own redraw is skipped completely for those modes.
    /// </summary>
    public static class SelectionView
    {
        static bool _active;
        static IntPtr _editorPtr; static FreeformPlateStructureEditor _editorObj; static MeshEditType _mode;
        static float _seenAt = -10f;
        static Matrix4x4 _meshToWorld = Matrix4x4.identity;
        static Camera _cam;
        public static bool Calibrating => false;   // (kept for FastGizmos)

        // mesh data (mesh space)
        static Vector3[] _pos = new Vector3[0];
        static bool[] _vSel = new bool[0];
        static int[] _e0 = new int[0], _e1 = new int[0];
        static bool[] _eSel = new bool[0];
        static int _vCount = -1, _eCount = -1, _fCount = -1;
        static bool _topoDirty = true, _selDirty = true, _edgesDirty = true, _pointsDirty = true, _facesDirty = true, _hoverDirty = true, _resizeOnly;
        static float _builtPx = -1f;
        static readonly List<int> _selectedVerts = new List<int>();
        static readonly List<int> _faceTris = new List<int>();   // vertex indices of the selected faces' triangles
        static int _hoverV = -1, _hoverE = -1; static float _nextHover; static double _hoverMs; static int _hoverN; static bool _hoverOff;
        static float _builtDist = -1f; static Vector3 _centre;

        // display
        sealed class Layer { public GameObject Go; public Mesh Mesh; public MeshRenderer R; public bool Has; }
        static Layer _edges, _edgesSel, _pts, _ptsSel, _faces, _hover;
        static Matrix4x4 _builtM;
        static int _layer, _colourId = -1;
        static Material _mat;
        static Color _cSel = new Color(1f, 0.2f, 0.1f), _cDesel = new Color(1f, 0.55f, 0.1f), _ceSel = new Color(1f, 0.2f, 0.1f), _ceDesel = new Color(1f, 0.6f, 0.2f, 0.8f);
        static readonly Stopwatch _sw = new Stopwatch();

        public static void Patch(Harmony h)
        {
            try
            {
                var fe = typeof(FreeformPlateStructureEditor);
                h.Patch(AccessTools.Method(fe, nameof(FreeformPlateStructureEditor.Update), new[] { typeof(MeshOperatorContext) }), postfix: new HarmonyMethod(typeof(SelectionView), nameof(UpdatePostfix)));
                foreach (var m in fe.GetMethods())
                {
                    if (m.DeclaringType != fe) continue;
                    if (m.Name == "OnSelectionChanged" || m.Name == "UpdateSelectionInfo") h.Patch(m, postfix: new HarmonyMethod(typeof(SelectionView), nameof(SelChanged)));
                    else if (m.Name == "OnComponentRebuilt" || m.Name == "SetSelectMode") h.Patch(m, postfix: new HarmonyMethod(typeof(SelectionView), nameof(MeshChanged)));
                }
                MPLog.Info("Designer: own selection display hooked");
            }
            catch (Exception e) { MPLog.Warn("Own selection display: " + e.Message); }
        }
        static void SelChanged() { _selDirty = true; }
        static void MeshChanged() { _topoDirty = true; _selDirty = true; }

        static void UpdatePostfix(FreeformPlateStructureEditor __instance, MeshOperatorContext __0)
        {
            _seenAt = Time.realtimeSinceStartup;
            try { _meshToWorld = __0.MeshToWorld; _cam = __0.Observer; } catch { }
            if (__instance.Pointer != _editorPtr) { _editorPtr = __instance.Pointer; _editorObj = __instance; _active = false; _topoDirty = _selDirty = true; }
        }

        /// <summary>From FastGizmos' DrawGizmos prefix. true: we draw, the game's redraw is skipped.</summary>
        public static bool Take(FreeformPlateStructureEditor ed, Visualizer vis)
        {
            if (!Plugin.OwnSelectionView.Value) { _active = false; return false; }
            MeshEditor me = null; MeshEditType mode = MeshEditType.None; EditMesh em = null;
            try { me = ed.MeshEditor; mode = me.SelectType; em = me.Mesh?.EditMesh; } catch { }
            if (em == null || (mode != MeshEditType.Vertex && mode != MeshEditType.Edge && mode != MeshEditType.Face)) { _active = false; return false; }
            if (em.VertexCount + em.EdgeCount < Math.Max(100, Plugin.FastGizmosMinItems.Value)) { _active = false; return false; }
            if (mode != _mode) { _mode = mode; _selDirty = true; }
            if (!_active)
            {
                _active = true; _topoDirty = _selDirty = true;
                try
                {
                    var mg = vis.meshGizmoRenderer;
                    _colourId = mg.colourPropertyID; _layer = mg.gameObject.layer;
                    _mat = mg.defaultMat ?? mg.defaultMaterialSource;   // the material the game draws its gizmo meshes with
                    var vm = me.VertexMaterials;
                    if (vm != null) { _cSel = vm.SelectedColour; _cDesel = vm.DeselectedColour; _ceSel = vm.EdgeSelectedColour; _ceDesel = vm.EdgeDeselectedColour; }
                }
                catch (Exception e) { MPLog.Debug("selection view setup: " + e.Message); }
                MPLog.Info($"Designer: own selection display on ({em.VertexCount} points, {em.EdgeCount} edges, {mode} mode, material {(_mat != null ? _mat.shader.name : "none")})");
            }
            return true;
        }
        public static void EndCalibration(bool others) { }
        public static void Sample(float size, Vector3 worldPos, bool edge) { }

        // ------------------------------------------------------------------ every frame
        public static void Tick()
        {
            bool show = _active && Time.realtimeSinceStartup - _seenAt < 0.3f && Plugin.OwnSelectionView.Value;
            if (!show) { Hide(); if (Time.realtimeSinceStartup - _seenAt > 1f) _active = false; return; }
            MeshEditor me = null; EditMesh em = null;
            try { me = _editorObj?.MeshEditor; em = me?.Mesh?.EditMesh; } catch { }
            if (em == null) { Hide(); _active = false; return; }
            try
            {
                _sw.Restart();
                int vc = em.VertexCount, ec = em.EdgeCount, fc = em.FaceCount;
                if (vc != _vCount || ec != _eCount || fc != _fCount) { _topoDirty = true; _selDirty = true; }
                if (_topoDirty) { ReadTopology(em); _topoDirty = false; _edgesDirty = _pointsDirty = _facesDirty = true; }
                if (_selDirty) { ReadSelection(em); _selDirty = false; _edgesDirty = _pointsDirty = _facesDirty = true; }
                var mouse = Mouse.current;
                bool held = mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed);
                if (held && _selectedVerts.Count > 0 && _selectedVerts.Count < 5000)
                {
                    var list = em.vertices; bool moved = false;
                    foreach (int i in _selectedVerts) { var v = list[i]; if (v == null) continue; var p = v.position; if (p != _pos[i]) { _pos[i] = p; moved = true; } }
                    if (moved) { _edgesDirty = _pointsDirty = _facesDirty = true; }
                }
                if (_meshToWorld != _builtM) { _builtM = _meshToWorld; _edgesDirty = _pointsDirty = _facesDirty = true; }
                if (_mode == MeshEditType.Vertex && _cam != null)
                {
                    float d = Vector3.Distance(_cam.transform.position, _meshToWorld.MultiplyPoint3x4(_centre));
                    if (_builtDist < 0 || d > _builtDist * 1.3f || d < _builtDist / 1.3f) { _builtDist = d; _resizeOnly = true; _hoverDirty = true; }
                    if (Plugin.SelectionPointPixels.Value != _builtPx) { _builtPx = Plugin.SelectionPointPixels.Value; _resizeOnly = true; _hoverDirty = true; }
                }
                Hover(me, mouse);
                Build();
                double ms = _sw.Elapsed.TotalMilliseconds;
                if (ms > 30) MPLog.Debug($"selection view: {ms:F0} ms this frame (selection read: {_selDebug})");
                Show();
            }
            catch (Exception e) { MPLog.Warn("Own selection display: " + e.Message + " - back to the game's display"); Hide(); _active = false; Plugin.OwnSelectionView.Value = false; }
        }

        static void ReadTopology(EditMesh em)
        {
            int vc = em.VertexCount, ec = em.EdgeCount;
            if (_pos.Length != vc) { _pos = new Vector3[vc]; _vSel = new bool[vc]; }
            if (_e0.Length != ec) { _e0 = new int[ec]; _e1 = new int[ec]; _eSel = new bool[ec]; }
            var vs = em.vertices; for (int i = 0; i < vc; i++) { var v = vs[i]; _pos[i] = v != null ? v.position : Vector3.zero; }
            var es = em.edges;
            for (int i = 0; i < ec; i++)
            {
                var e = es[i];
                if (e == null) { _e0[i] = _e1[i] = -1; continue; }
                var a = e.v0; var b = e.v1;
                _e0[i] = a != null ? a.index : -1; _e1[i] = b != null ? b.index : -1;
            }
            _vCount = vc; _eCount = ec; _fCount = em.FaceCount;
            var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity; for (int i = 0; i < vc; i++) { bmin = Vector3.Min(bmin, _pos[i]); bmax = Vector3.Max(bmax, _pos[i]); }
            _centre = vc > 0 ? (bmin + bmax) * 0.5f : Vector3.zero;
        }

        static void ReadSelection(EditMesh em)
        {
            _selectedVerts.Clear(); _faceTris.Clear();
            Array.Clear(_vSel, 0, _vSel.Length); Array.Clear(_eSel, 0, _eSel.Length);
            int sv = em.selectedVertexCount, se = em.selectedEdgeCount, sf = em.selectedFaceCount, sl = em.selectedLoopCount;
            var faces = new List<Face>();
            // the editor's own list of selected elements: only the selection is visited, not the whole mesh
            bool fromList = false;
            try
            {
                var sel = em.selection;
                if (sel != null && sel.Count == sv + se + sf + sl)
                {
                    fromList = true;
                    for (int i = 0; i < sel.Count; i++)
                    {
                        var el = sel[i]; if (el == null) continue;
                        int ix = el.index;
                        switch (el.Type)
                        {
                            case ElementType.Vertex: if (ix < _vCount) { _vSel[ix] = true; _selectedVerts.Add(ix); } break;
                            case ElementType.Edge: if (ix < _eCount) _eSel[ix] = true; break;
                            case ElementType.Face: if (_mode == MeshEditType.Face) { var f = el.TryCast<Face>(); if (f != null) faces.Add(f); } break;
                        }
                    }
                }
            }
            catch { fromList = false; }
            if (!fromList)
            {
                var vs = em.vertices;
                if (sv > 0) for (int i = 0; i < _vCount; i++) { var v = vs[i]; if (v != null && v.HasFlag(ElementFlags.Selected)) { _vSel[i] = true; _selectedVerts.Add(i); } }
                var es = em.edges;
                if (se > 0) for (int i = 0; i < _eCount; i++) _eSel[i] = es[i] != null && es[i].HasFlag(ElementFlags.Selected);
                if (_mode == MeshEditType.Face && sf > 0) { var fs = em.faces; int fc = em.FaceCount; for (int i = 0; i < fc; i++) { var f = fs[i]; if (f != null && f.HasFlag(ElementFlags.Selected)) faces.Add(f); } }
            }
            if (_mode == MeshEditType.Face)
            {
                var ring = new List<int>();
                foreach (var f in faces)
                {
                    ring.Clear();
                    var l0 = f.firstLoop; var l = l0; int guard = 0;
                    while (l != null && guard++ < 512) { var v = l.vertex; if (v != null) ring.Add(v.index); l = l.next; if (l == null || l.Pointer == l0.Pointer) break; }
                    for (int k = 1; k + 1 < ring.Count; k++) { _faceTris.Add(ring[0]); _faceTris.Add(ring[k]); _faceTris.Add(ring[k + 1]); }
                }
            }
            // in the Points / Faces modes, the edges between selected points / of selected faces show as selected (the flush may not be in the list)
            if (_mode == MeshEditType.Vertex && _selectedVerts.Count > 0) for (int i = 0; i < _eCount; i++) { int a = _e0[i], b = _e1[i]; if (a >= 0 && b >= 0 && a < _vCount && b < _vCount && _vSel[a] && _vSel[b]) _eSel[i] = true; }
            _selDebug = fromList ? "list" : "scan";
        }
        static string _selDebug = "";

        static void Hover(MeshEditor me, Mouse mouse)
        {
            if (_hoverOff || mouse == null || _mode == MeshEditType.Face || Time.realtimeSinceStartup < _nextHover) return;
            _nextHover = Time.realtimeSinceStartup + 1f / 15f;
            var t = Stopwatch.StartNew();
            int hv = -1, he = -1;
            try { var s = me.Sample(false); if (s.HasPick) { hv = s.vertexIndex; he = s.edgeIndex; } } catch { _hoverOff = true; }
            _hoverMs += t.Elapsed.TotalMilliseconds; _hoverN++;
            if (_hoverN == 30 && _hoverMs / _hoverN > 4) { _hoverOff = true; MPLog.Info($"Designer: hover highlight off ({_hoverMs / _hoverN:F1} ms per check)"); }
            if (_mode == MeshEditType.Vertex) he = -1; else hv = -1;
            if (hv != _hoverV || he != _hoverE) { _hoverV = hv; _hoverE = he; _hoverDirty = true; }   // only the small hover mesh
        }

        // ------------------------------------------------------------------ meshes
        static Layer NewLayer(string name)
        {
            var go = new GameObject("SprocketLAN " + name); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave;
            var mf = go.AddComponent<MeshFilter>(); var r = go.AddComponent<MeshRenderer>();
            var m = new Mesh(); m.name = name; m.hideFlags = HideFlags.HideAndDontSave; m.MarkDynamic(); m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mf.sharedMesh = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return new Layer { Go = go, Mesh = m, R = r };
        }

        static void Build()
        {
            _edges ??= NewLayer("edges"); _edgesSel ??= NewLayer("selected edges"); _pts ??= NewLayer("points"); _ptsSel ??= NewLayer("selected points");
            _faces ??= NewLayer("selected faces"); _hover ??= NewLayer("hover");
            var m = _meshToWorld;
            if (_edgesDirty)
            {
                _edgesDirty = false;
                Lines(_edges, m, false); Lines(_edgesSel, m, true);
                Style(_edges, _ceDesel); Style(_edgesSel, _ceSel);
            }
            float size = PointSize();
            if (_pointsDirty)
            {
                _pointsDirty = false; _resizeOnly = false;
                if (_mode == MeshEditType.Vertex)
                {
                    Points(_pts, m, false, size); Points(_ptsSel, m, true, size * 1.15f);
                    Style(_pts, _cDesel); Style(_ptsSel, _cSel);
                }
                else { _pts.Has = _ptsSel.Has = false; }
                _hoverDirty = true;
            }
            else if (_resizeOnly)
            {
                // zoomed: same points, new size (vertex positions only)
                _resizeOnly = false;
                if (_mode == MeshEditType.Vertex) { Resize(_pts, size); Resize(_ptsSel, size * 1.15f); }
            }
            if (_hoverDirty)
            {
                _hoverDirty = false;
                if (_mode == MeshEditType.Vertex && _hoverV >= 0 && _hoverV < _vCount)
                {
                    var p = m.MultiplyPoint3x4(_pos[_hoverV]); var v = new Vector3[6]; var idx = new int[24];
                    Octa(p, size * 0.8f, v, idx, 0); Set(_hover, v, idx, MeshTopology.Triangles); Style(_hover, Color.white);
                }
                else if (_mode == MeshEditType.Edge && _hoverE >= 0 && _hoverE < _eCount && _e0[_hoverE] >= 0 && _e1[_hoverE] >= 0)
                { var v = new[] { m.MultiplyPoint3x4(_pos[_e0[_hoverE]]), m.MultiplyPoint3x4(_pos[_e1[_hoverE]]) }; Set(_hover, v, new[] { 0, 1 }, MeshTopology.Lines); Style(_hover, Color.white); }
                else _hover.Has = false;
            }
            if (_facesDirty)
            {
                _facesDirty = false;
                if (_mode == MeshEditType.Face && _faceTris.Count > 0)
                {
                    // the selected faces' vertices, both sides
                    var map = new Dictionary<int, int>(); var v = new List<Vector3>(); var idx = new int[_faceTris.Count * 2];
                    for (int i = 0; i < _faceTris.Count; i++) { int vi = _faceTris[i]; if (vi < 0 || vi >= _vCount) vi = 0; if (!map.TryGetValue(vi, out int k)) { k = v.Count; map[vi] = k; v.Add(m.MultiplyPoint3x4(_pos[vi])); } idx[i] = k; }
                    for (int i = 0; i < _faceTris.Count; i += 3) { idx[_faceTris.Count + i] = idx[i]; idx[_faceTris.Count + i + 1] = idx[i + 2]; idx[_faceTris.Count + i + 2] = idx[i + 1]; }
                    Set(_faces, v.ToArray(), idx, MeshTopology.Triangles);
                    var c = _cSel; c.a = 0.45f; Style(_faces, c);
                }
                else _faces.Has = false;
            }
        }

        static float PointSize()
        {
            // a few pixels on screen at the structure's distance (cubes keep that size until the distance changes by 30 %)
            float d = _builtDist > 0 ? _builtDist : 10f;
            float fov = _cam != null ? _cam.fieldOfView : 60f;
            float pxPerM = Screen.height / (2f * d * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            return Mathf.Max(0.0005f, Plugin.SelectionPointPixels.Value / Mathf.Max(1f, pxPerM));
        }

        static void Lines(Layer l, Matrix4x4 m, bool selected)
        {
            int n = 0; for (int i = 0; i < _eCount; i++) if (_eSel[i] == selected && _e0[i] >= 0 && _e1[i] >= 0) n++;
            if (n == 0) { l.Has = false; return; }
            var v = new Vector3[n * 2]; var idx = new int[n * 2]; int k = 0;
            for (int i = 0; i < _eCount; i++)
            {
                if (_eSel[i] != selected || _e0[i] < 0 || _e1[i] < 0 || _e0[i] >= _vCount || _e1[i] >= _vCount) continue;
                v[k] = m.MultiplyPoint3x4(_pos[_e0[i]]); v[k + 1] = m.MultiplyPoint3x4(_pos[_e1[i]]); idx[k] = k; idx[k + 1] = k + 1; k += 2;
            }
            if (k < v.Length) { Array.Resize(ref v, k); Array.Resize(ref idx, k); }
            Set(l, v, idx, MeshTopology.Lines);
        }

        // points: small octahedra (6 corners, 8 triangles: round-looking and a third of a cube's indices)
        static readonly Vector3[] Oct = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly int[] OctTris = { 0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5 };
        static void Octa(Vector3 p, float h, Vector3[] v, int[] idx, int c)
        {
            int o = c * 6; for (int j = 0; j < 6; j++) v[o + j] = p + Oct[j] * h;
            int t = c * 24; for (int j = 0; j < 24; j++) idx[t + j] = o + OctTris[j];
        }
        static readonly Dictionary<Layer, Vector3[]> _centres = new Dictionary<Layer, Vector3[]>();
        static void Points(Layer l, Matrix4x4 m, bool selected, float size)
        {
            int n = 0; for (int i = 0; i < _vCount; i++) if (_vSel[i] == selected) n++;
            if (n == 0) { l.Has = false; _centres[l] = new Vector3[0]; return; }
            var ctr = new Vector3[n]; var v = new Vector3[n * 6]; var idx = new int[n * 24]; float h = size * 0.5f; int c = 0;
            for (int i = 0; i < _vCount; i++) { if (_vSel[i] != selected) continue; var p = m.MultiplyPoint3x4(_pos[i]); ctr[c] = p; Octa(p, h, v, idx, c); c++; }
            _centres[l] = ctr;
            Set(l, v, idx, MeshTopology.Triangles);
        }
        static void Resize(Layer l, float size)
        {
            if (!l.Has || !_centres.TryGetValue(l, out var ctr) || ctr.Length == 0) return;
            var v = new Vector3[ctr.Length * 6]; float h = size * 0.5f;
            for (int c = 0; c < ctr.Length; c++) { int o = c * 6; var p = ctr[c]; for (int j = 0; j < 6; j++) v[o + j] = p + Oct[j] * h; }
            l.Mesh.vertices = v;   // same count, same triangles
        }

        static void Set(Layer l, Vector3[] v, int[] idx, MeshTopology topo)
        {
            var mesh = l.Mesh; mesh.Clear();
            mesh.vertices = v; mesh.SetIndices(idx, topo, 0);
            mesh.bounds = new Bounds(_meshToWorld.MultiplyPoint3x4(Vector3.zero), Vector3.one * 100000f);   // never culled
            l.Has = v.Length > 0;
        }

        static MaterialPropertyBlock _mpb;
        static void Style(Layer l, Color c)
        {
            if (_mat != null && l.R.sharedMaterial != _mat) l.R.sharedMaterial = _mat;
            l.Go.layer = _layer;
            _mpb ??= new MaterialPropertyBlock();
            _mpb.Clear();
            if (_colourId != -1) _mpb.SetColor(_colourId, c);
            _mpb.SetColor("_BaseColor", c); _mpb.SetColor("_UnlitColor", c); _mpb.SetColor("_Color", c);
            l.R.SetPropertyBlock(_mpb);
        }

        static Layer[] All => new[] { _edges, _edgesSel, _pts, _ptsSel, _faces, _hover };
        static void Show() { foreach (var l in All) if (l != null && l.Go.activeSelf != l.Has) l.Go.SetActive(l.Has); }
        static void Hide() { foreach (var l in All) if (l != null && l.Go.activeSelf) l.Go.SetActive(false); }
    }
}

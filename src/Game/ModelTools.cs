using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Extra modelling tools for the compartment (freeform plate structure) editor: inset faces, polygon / round holes,
    /// knife (connect vertices, cut across a face), subdivide edges, bevel edges.
    ///
    /// Each tool runs as a real editor operation, so it goes into the game's undo history: the mod can't define new
    /// game classes (class injection crashes this build), so it hands the editor one of the game's own operations
    /// (InvertOp) tagged with our tool's name, and a Harmony prefix runs our code instead of the inversion.
    /// </summary>
    public static class ModelTools
    {
        public enum Tool { Inset, PolyHole, RoundHole, RoundInset, Connect, CutAcross, Subdivide, Bevel }
        public static float InsetMm = 50f, HoleRadiusMm = 100f, BevelMm = 30f, CutAt = 0.5f;
        public static int HoleEdges = 16, SubdivideCuts = 1;
        public static string Status = "";
        const string Tag = "SprocketLAN:";

        static FreeformPlateStructureEditor _editor; static float _seen = -10f;
        public static bool EditorOpen => _editor != null && Time.realtimeSinceStartup - _seen < 0.5f;

        public static void Patch(Harmony h)
        {
            try
            {
                // Update has overloads (the editor's own and its base class's): the one taking the mesh operator context
                h.Patch(AccessTools.Method(typeof(FreeformPlateStructureEditor), nameof(FreeformPlateStructureEditor.Update), new[] { typeof(Sprocket.MeshEditing.MeshOperatorContext) }), postfix: new HarmonyMethod(typeof(ModelTools), nameof(Editor_Postfix)));
                MPLog.Info("Modelling tools: editor hook ok");
                // also seen while its menu is drawn, in case Update doesn't run every frame
                try { h.Patch(AccessTools.Method(typeof(FreeformPlateStructureEditor), nameof(FreeformPlateStructureEditor.OnEditGUI)), postfix: new HarmonyMethod(typeof(ModelTools), nameof(Editor_Postfix))); } catch (Exception e2) { MPLog.Debug("editor GUI hook: " + e2.Message); }
            }
            catch (Exception e) { MPLog.Warn("Modelling tools editor hook: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(InvertOp), nameof(InvertOp.ExecuteInternal), new[] { typeof(EditMesh) }), prefix: new HarmonyMethod(typeof(ModelTools), nameof(Invert_Prefix)));
                MPLog.Info("Modelling tools: operation hook ok");
            }
            catch (Exception e) { MPLog.Warn("Modelling tools operation hook: " + e.Message); }
        }

        static void Editor_Postfix(FreeformPlateStructureEditor __instance) { _editor = __instance; _seen = Time.realtimeSinceStartup; }

        static Tool _pending;
        /// <summary>Runs a tool on the current selection as an undoable editor operation.</summary>
        public static void Run(Tool t)
        {
            if (!EditorOpen) { Status = "<color=#f88>Open a compartment in the editor first</color>"; return; }
            try
            {
                var ops = _editor.selectPointerOperator?.operations;
                var me = ops?.meshEditor;
                if (ops == null || me == null) { Status = "<color=#f88>The editor isn't ready</color>"; return; }
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var mop = new InvertOp(); mop.Name = Tag + string.Join("|", t.ToString(), InsetMm.ToString(ci), HoleRadiusMm.ToString(ci), HoleEdges.ToString(ci), BevelMm.ToString(ci), CutAt.ToString(ci), SubdivideCuts.ToString(ci));
                try { mop.Symmetry = me.Symmetry; } catch { }
                Status = "";
                var topo = me.CreateTopoOp(mop);
                ops.Execute(topo, StructureEditOperationOptions.None, ops.GetNewGroupID());
                try { me.SelectionChanged = true; me.RequiresGizmoRedraw = true; _editor.OnSelectionChanged(me); } catch (Exception e) { MPLog.Debug("selection refresh: " + e.Message); }   // the editor's selection display / info
                if (Status == "") Status = $"<color=#8f8>{t} done</color>";
                MPLog.Info($"Modelling tool {t}: {Status.Replace("<color=#8f8>", "").Replace("<color=#f88>", "").Replace("<color=#fd8>", "").Replace("</color>", "")}");
            }
            catch (Exception e) { Status = "<color=#f88>" + t + " failed: " + e.Message + "</color>"; MPLog.Warn("Modelling tool " + t + ": " + e); }
        }

        static bool Invert_Prefix(InvertOp __instance, EditMesh mesh)
        {
            string n = null; try { n = __instance.Name; } catch { }
            if (n == null || !n.StartsWith(Tag)) return true;
            try
            {
                // the tool and its settings as they were when it was first run (undo / redo replays it)
                var parts = n.Substring(Tag.Length).Split('|'); var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (!Enum.TryParse(parts[0], out Tool tool)) return false;
                float F(int i, float def) => parts.Length > i && float.TryParse(parts[i], System.Globalization.NumberStyles.Float, ci, out var x) ? x : def;
                float insetMm = F(1, InsetMm), radMm = F(2, HoleRadiusMm), bevelMm = F(4, BevelMm), cutAt = F(5, CutAt);
                int edges = (int)F(3, HoleEdges), cuts = (int)F(6, SubdivideCuts);
                _pending = tool;
                int v0 = mesh.VertexCount, f0 = mesh.FaceCount;
                // dry run on a copy first: a tool that would leave a broken mesh (which the game can't rebuild) is refused
                string err = null;
                try
                {
                    var copy = new EditMesh(mesh);
                    string saved = Status;
                    Apply(copy, __instance, tool, insetMm, radMm, edges, bevelMm, cutAt, cuts);
                    try { copy.DeleteMarked(); } catch { }
                    int bad = Problems(copy, out var what);
                    if (bad > 0) err = $"the result would be broken ({bad} bad elements: {what})";
                    Status = saved;
                }
                catch (Exception de) { err = de.Message; }
                bool inconclusive = err != null && err.Contains("made no faces");   // the copy may not carry the editor's input marks
                if (inconclusive) { MPLog.Info($"Modelling tool {tool}: dry run inconclusive ({err}), running on the real mesh"); err = null; }
                if (err != null) { Status = $"<color=#f88>{tool} not done: {err}</color>"; MPLog.Warn($"Modelling tool {tool}: {err}"); return false; }
                Apply(mesh, __instance, tool, insetMm, radMm, edges, bevelMm, cutAt, cuts);
                try { mesh.DeleteMarked(); } catch { }
                mesh.MarkDirty(MeshDirtyFlags.All);
                int after = Problems(mesh, out var w2);
                if (after > 0) { Status = $"<color=#f88>{tool}: the mesh has {after} broken elements ({w2}) - press Ctrl+Z</color>"; MPLog.Warn($"Modelling tool {tool}: {after} broken elements after: {w2}"); }
                MPLog.Info($"Modelling tool {_pending}: vertices {v0} -> {mesh.VertexCount}, faces {f0} -> {mesh.FaceCount}");
            }
            catch (Exception e) { Status = $"<color=#f88>{_pending} failed: {e.Message}</color>"; MPLog.Warn($"Modelling tool {_pending}: {e}"); }
            return false;
        }

        static void Apply(EditMesh m, MeshOperation op, Tool tool, float insetMm, float radMm, int edges, float bevelMm, float cutAt, int cuts)
        {
            switch (tool)
            {
                case Tool.Inset: Inset(m, op, insetMm / 1000f, false); break;
                case Tool.PolyHole: Inset(m, op, insetMm / 1000f, true); break;
                case Tool.RoundHole: Round(m, op, radMm / 1000f, Mathf.Clamp(edges, 3, 256), false); break;
                case Tool.RoundInset: Round(m, op, radMm / 1000f, Mathf.Clamp(edges, 3, 256), true); break;
                case Tool.Connect: Connect(m, op); break;
                case Tool.CutAcross: CutAcross(m, op, Mathf.Clamp(cutAt, 0.02f, 0.98f)); break;
                case Tool.Subdivide: Subdivide(m, op, Mathf.Clamp(cuts, 1, 32)); break;
                case Tool.Bevel: Bevel(m, op, bevelMm / 1000f); break;
            }
            try { m.DeleteMarked(); } catch { }
            int cut = Legalize(m);
            if (cut > 0) MPLog.Info($"Modelling tool {tool}: {cut} cuts to keep every face at 3-4 corners");
            int fixedTe = FixThickenEdges(m);
            if (fixedTe > 0) MPLog.Info($"Modelling tool {tool}: {fixedTe} corners re-linked to their thickness edge");
            Tidy(m);
        }

        /// <summary>After a tool: element numbers redone (the editor's selection history reads by index - stale numbers
        /// threw on every click) and a clean selection: new elements copy the "selected" mark of what they were made from,
        /// which the editor's own selection list didn't know about (they stayed selected for good).</summary>
        static void Tidy(EditMesh m)
        {
            var keep = new List<Face>();
            foreach (var f in AllFaces(m)) if (f.HasFlag(ElementFlags.Selected) && _keepSel.Contains(f.Pointer)) keep.Add(f);
            try { m.ReIndex(ElementType.Vertex | ElementType.Edge | ElementType.Face | ElementType.Loop); } catch (Exception e) { MPLog.Warn("reindex: " + e.Message); }
            try
            {
                for (int i = 0; i < m.VertexCount; i++) m.GetVertex(i)?.DisableFlag(ElementFlags.Selected);
                for (int i = 0; i < m.EdgeCount; i++) m.GetEdge(i)?.DisableFlag(ElementFlags.Selected);
                for (int i = 0; i < m.FaceCount; i++) m.GetFace(i)?.DisableFlag(ElementFlags.Selected);
                for (int i = 0; i < m.LoopCount; i++) { var l = m.GetLoop(i); if (l != null) l.flags &= ~LoopFlags.Selected; }
                m.selection?.Clear();
                m.selectedVertexCount = 0; m.selectedEdgeCount = 0; m.selectedFaceCount = 0; m.selectedLoopCount = 0;
                SelectOperations.DeselectAll(m);
                foreach (var f in keep) SelectOperations.PickFace(m, SelectionMode.Add, f, true);
                SelectOperations.SelectFlush(m);
            }
            catch (Exception e) { MPLog.Warn("selection tidy: " + e.Message); }
            _keepSel.Clear();
        }
        static readonly HashSet<IntPtr> _keepSel = new HashSet<IntPtr>();

        /// <summary>Corners thickened along an edge must name an edge at their own vertex: an edge split leaves some naming
        /// the old (now shorter, elsewhere) edge. They get the edge at their vertex that runs most like the old one.</summary>
        static int FixThickenEdges(EditMesh m)
        {
            var at = EdgeMap(m); int n = 0;
            for (int i = 0; i < m.LoopCount; i++)
            {
                var l = m.GetLoop(i); if (l == null || l.vertex == null) continue;
                var te = l.thickenEdge;
                if (te == null || te.ContainsVertex(l.vertex)) continue;
                var dir = (te.v1.position - te.v0.position).normalized;
                Edge best = null; float bd = -1f;
                if (at.TryGetValue(l.vertex.Pointer, out var es))
                    foreach (var e in es) { var d = (e.GetOtherVertex(l.vertex).position - l.vertex.position).normalized; float sc = Mathf.Abs(Vector3.Dot(d, dir)); if (sc > bd) { bd = sc; best = e; } }
                l.thickenEdge = best; n++;
            }
            return n;
        }
        /// <summary>Every vertex's edges (by scanning the edge list - doesn't depend on the disk links).</summary>
        static Dictionary<IntPtr, List<Edge>> EdgeMap(EditMesh m)
        {
            var d = new Dictionary<IntPtr, List<Edge>>();
            for (int i = 0; i < m.EdgeCount; i++)
            {
                var e = m.GetEdge(i); if (e == null || e.HasFlag(ElementFlags.Delete) || e.v0 == null || e.v1 == null) continue;
                foreach (var v in new[] { e.v0, e.v1 }) { if (!d.TryGetValue(v.Pointer, out var l)) d[v.Pointer] = l = new List<Edge>(); l.Add(e); }
            }
            return d;
        }

        /// <summary>The game's faces have at most 4 corners (its tessellation buffers hold 4) and must not be concave:
        /// every face with more, or a concave quad, is cut into triangles / quads with the game's own face split (ear cutting).</summary>
        static int Legalize(EditMesh m)
        {
            int cuts = 0;
            for (int guard = 0; guard < 4000; guard++)
            {
                Face bad = null;
                foreach (var f in AllFaces(m)) { var n = LoopsOf(f).Count; if (n > 4 || (n == 4 && !Convex(f))) { bad = f; break; } }
                if (bad == null) break;
                var ls = LoopsOf(bad); int k = ls.Count;
                var p = ls.Select(l => l.vertex.position).ToList(); var N = Newell(p);
                // an ear: a convex corner whose triangle holds no other corner
                int ear = -1;
                for (int i = 0; i < k && ear < 0; i++)
                {
                    var a = p[(i + k - 1) % k]; var b = p[i]; var c = p[(i + 1) % k];
                    var cr = Vector3.Cross(b - a, c - b);
                    if (Vector3.Dot(cr, N) <= 1e-9f) continue;   // reflex (or straight)
                    if (cr.magnitude < 1e-6f * Mathf.Max(1e-6f, (c - a).sqrMagnitude) || Area(new List<Vector3> { a, b, c }) < 1e-6f) continue;   // a sliver with (almost) no area
                    bool empty = true;
                    for (int j = 0; j < k && empty; j++) { if (j == i || j == (i + k - 1) % k || j == (i + 1) % k) continue; if (InTri(p[j], a, b, c, N)) empty = false; }
                    if (empty) ear = i;
                }
                if (ear < 0) { MPLog.Warn($"Modelling tools: a {k}-corner face couldn't be cut into 3-4 corner faces"); break; }
                TopoOperations.SubdivideFace(m, bad, ls[(ear + k - 1) % k], ls[(ear + 1) % k], out _);
                cuts++;
            }
            return cuts;
        }
        static bool Convex(Face f)
        {
            var p = LoopsOf(f).Select(l => l.vertex.position).ToList(); int k = p.Count; var N = Newell(p);
            for (int i = 0; i < k; i++) if (Vector3.Dot(Vector3.Cross(p[i] - p[(i + k - 1) % k], p[(i + 1) % k] - p[i]), N) < -1e-9f) return false;
            return true;
        }
        static bool InTri(Vector3 q, Vector3 a, Vector3 b, Vector3 c, Vector3 N)
        {
            float s1 = Vector3.Dot(Vector3.Cross(b - a, q - a), N), s2 = Vector3.Dot(Vector3.Cross(c - b, q - b), N), s3 = Vector3.Dot(Vector3.Cross(a - c, q - c), N);
            return s1 > 0f && s2 > 0f && s3 > 0f;
        }

        /// <summary>Elements the game's own checks call broken (or faces with no area), with a short description.</summary>
        static int Problems(EditMesh m, out string what)
        {
            int bad = 0; var kinds = new HashSet<string>();
            for (int i = 0; i < m.FaceCount; i++)
            {
                var f = m.GetFace(i); if (f == null || f.HasFlag(ElementFlags.Delete)) continue;
                var fe = Face.Validate(f); if (fe != 0) { bad++; kinds.Add(fe.ToString()); continue; }
                var ls = LoopsOf(f);
                if (ls.Count < 3) { bad++; kinds.Add("face with < 3 corners"); continue; }
                if (ls.Count > 4) { bad++; kinds.Add("face with > 4 corners"); continue; }
                var p = ls.Select(l => l.vertex.position).ToList();
                if (Area(p) < 1e-7f) { bad++; kinds.Add("face with no area"); }
                foreach (var l in ls) { var le = Loop.Validate(l); if (le != 0) { bad++; kinds.Add(le.ToString()); } }
            }
            for (int i = 0; i < m.EdgeCount; i++) { var e = m.GetEdge(i); if (e == null || e.HasFlag(ElementFlags.Delete)) continue; var ee = Edge.Validate(e); if (ee != 0) { bad++; kinds.Add(ee.ToString()); } }
            what = string.Join(", ", kinds.Take(4));
            return bad;
        }
        static float Area(List<Vector3> p) { var n = Vector3.zero; for (int i = 1; i + 1 < p.Count; i++) n += Vector3.Cross(p[i] - p[0], p[i + 1] - p[0]); return n.magnitude * 0.5f; }

        // ============================================================================================ mesh helpers
        static bool Same(Il2CppSystem.Object a, Il2CppSystem.Object b) => a != null && b != null && a.Pointer == b.Pointer;

        static List<Loop> LoopsOf(Face f)
        {
            var r = new List<Loop>(); var s = f.firstLoop; var l = s;
            for (int g = 0; l != null && g < 4096; g++) { r.Add(l); l = l.next; if (Same(l, s)) break; }
            return r;
        }
        static Vector3 Newell(List<Vector3> p)
        {
            var n = Vector3.zero;
            for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; n.x += (a.y - b.y) * (a.z + b.z); n.y += (a.z - b.z) * (a.x + b.x); n.z += (a.x - b.x) * (a.y + b.y); }
            return n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }
        static Vector3 Centroid(List<Vector3> p) { var c = Vector3.zero; foreach (var x in p) c += x; return c / Math.Max(1, p.Count); }

        static List<Face> AllFaces(EditMesh m) { var r = new List<Face>(); for (int i = 0; i < m.FaceCount; i++) { var f = m.GetFace(i); if (f != null && !f.HasFlag(ElementFlags.Delete)) r.Add(f); } return r; }
        static List<Face> SelectedFaces(EditMesh m)
        {
            var r = new List<Face>();
            try { var a = ElementSelection.GetSelectedFaces(m, false); if (a != null) foreach (var f in a) if (f != null) r.Add(f); } catch { }
            return r;
        }
        static List<Edge> InputEdges(EditMesh m, MeshOperation op)
        {
            Il2CppReferenceArray<Edge> a = null;
            try { a = op.GetInputEdges(m, op.Symmetry ? MeshOperation.InputFlags.IncludeMirrors : MeshOperation.InputFlags.None); } catch { }
            if (a == null || a.Length == 0) try { a = ElementSelection.GetSelectedEdges(m, false); if (op.Symmetry && a != null && a.Length > 0) a = ElementSelection.AppendMirrors(m, a); } catch { }
            var r = new List<Edge>(); if (a != null) foreach (var e in a) if (e != null && !r.Any(x => Same(x, e))) r.Add(e);
            return r;
        }
        static List<Vertex> InputVertices(EditMesh m, MeshOperation op)
        {
            Il2CppReferenceArray<Vertex> a = null;
            try { a = op.GetInputVertices(m, MeshOperation.InputFlags.None); } catch { }
            if (a == null || a.Length == 0) try { a = ElementSelection.GetSelectedVertices(m, false); } catch { }
            var r = new List<Vertex>(); if (a != null) foreach (var v in a) if (v != null && !r.Any(x => Same(x, v))) r.Add(v);
            return r;
        }
        static void SelectOnly(EditMesh m, List<Face> faces)
        {
            try { SelectOperations.DeselectAll(m); foreach (var f in faces) SelectOperations.PickFace(m, SelectionMode.Add, f, false); SelectOperations.SelectFlush(m); }
            catch (Exception e) { MPLog.Debug("select: " + e.Message); }
            _keepSel.Clear(); foreach (var f in faces) _keepSel.Add(f.Pointer);
        }

        /// <summary>The game's own extrude, by 0: every selected face gets a copy on top of it ("cap"), joined to the
        /// rest by new side faces. Returns the caps (all their corners are new vertices).</summary>
        static List<Face> ExtrudeCaps(EditMesh m, MeshOperation op)
        {
            var oldF = new HashSet<IntPtr>(AllFaces(m).Select(f => f.Pointer));
            var oldV = new HashSet<IntPtr>(); for (int i = 0; i < m.VertexCount; i++) { var v = m.GetVertex(i); if (v != null) oldV.Add(v.Pointer); }
            // the planes of the faces being extruded (with their mirrors): the caps are put back flat onto them
            var planes = new List<(Vector3 n, Vector3 c)>();
            var src = SelectedFaces(m);
            if (op.Symmetry) try { var arr = ElementSelection.AppendMirrors(m, new Il2CppReferenceArray<Face>(src.ToArray())); src = new List<Face>(); foreach (var f in arr) if (f != null) src.Add(f); } catch { }
            foreach (var f in src) { var p = LoopsOf(f).Select(l => l.vertex.position).ToList(); planes.Add((Newell(p), Centroid(p))); }
            var ext = new ExtrudeOp(); ext.Name = "Inset";
            try { ext.Symmetry = op.Symmetry; } catch { }
            op.ExecuteSubRoutine(m, ext);   // (moves the copies out by the game's own extrude distance)
            var caps = new List<Face>();
            foreach (var f in AllFaces(m))
            {
                if (oldF.Contains(f.Pointer)) continue;
                if (LoopsOf(f).All(l => !oldV.Contains(l.vertex.Pointer))) caps.Add(f);
            }
            foreach (var cap in caps)
            {
                var ls = LoopsOf(cap); var p = ls.Select(l => l.vertex.position).ToList();
                var n = Newell(p); var c = Centroid(p);
                // its original face: parallel, and nearest once both are seen along the normal
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < planes.Count; i++)
                {
                    if (Mathf.Abs(Vector3.Dot(planes[i].n, n)) < 0.98f) continue;
                    var off = c - planes[i].c; float side = Vector3.ProjectOnPlane(off, planes[i].n).sqrMagnitude;
                    if (side < bd) { bd = side; best = i; }
                }
                if (best < 0) continue;
                var (pn, pc) = planes[best];
                foreach (var l in ls) { var v = l.vertex.position; l.vertex.position = v - pn * Vector3.Dot(v - pc, pn); }
            }
            return caps;
        }

        /// <summary>Moves a cap's corners into its plane by d (edges stay parallel to the outline).</summary>
        static int _clipped;
        static void ShrinkCap(Face cap, float d)
        {
            var ls = LoopsOf(cap); int n = ls.Count; if (n < 3) return;
            var p = ls.Select(l => l.vertex.position).ToList();
            var N = Newell(p); var c = Centroid(p);
            // never deeper than ~half the face's narrowest width (deeper, the inner outline turns inside out)
            float inr = float.MaxValue;
            for (int i = 0; i < n; i++) { var e = (p[(i + 1) % n] - p[i]).normalized; var off = c - p[i]; inr = Mathf.Min(inr, (off - e * Vector3.Dot(off, e)).magnitude); }
            if (d > inr * 0.9f) { d = inr * 0.9f; _clipped++; }
            var np = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var a = p[(i + n - 1) % n]; var b = p[i]; var cn = p[(i + 1) % n];
                var n1 = Vector3.Cross(N, (b - a).normalized).normalized; var n2 = Vector3.Cross(N, (cn - b).normalized).normalized;   // pointing inside
                var bis = n1 + n2; bis = bis.sqrMagnitude > 1e-8f ? bis.normalized : n1;
                float k = Mathf.Max(0.2f, Vector3.Dot(bis, n1));
                var w = b + bis * (d / k);
                float maxIn = (c - b).magnitude * 0.9f;
                if ((w - b).magnitude > maxIn) w = b + (w - b).normalized * maxIn;   // never past the middle
                np[i] = w;
            }
            for (int i = 0; i < n; i++) ls[i].vertex.position = np[i];
        }

        static void DeleteFaces(EditMesh m, MeshOperation op, List<Face> faces)
        {
            SelectOnly(m, faces);
            var del = new DeleteOp(DeleteType.Faces);
            try { del.Symmetry = false; } catch { }
            op.ExecuteSubRoutine(m, del);
        }

        // ============================================================================================ inset / polygon hole
        static void Inset(EditMesh m, MeshOperation op, float d, bool hole)
        {
            if (SelectedFaces(m).Count == 0) { Status = "<color=#f88>Select one or more faces (faces mode)</color>"; return; }
            var caps = ExtrudeCaps(m, op);
            if (caps.Count == 0) throw new Exception("the extrude made no faces");
            _clipped = 0;
            foreach (var c in caps) ShrinkCap(c, d);
            if (hole) DeleteFaces(m, op, caps); else SelectOnly(m, caps);
            Status = $"<color=#8f8>{(hole ? "Hole" : "Inset")}: {caps.Count} face(s), {d * 1000f:0} mm{(_clipped > 0 ? $" <color=#fd8>(less on {_clipped} face(s): the face is too narrow)</color>" : "")}</color>";
        }

        // ============================================================================================ round hole / round inset
        static void Round(EditMesh m, MeshOperation op, float r, int count, bool keep)
        {
            var sel = SelectedFaces(m);
            if (sel.Count == 0) { Status = "<color=#f88>Select one or more faces (faces mode)</color>"; return; }
            if (sel.Any(f => !Convex(f))) { Status = "<color=#f88>Round holes need convex faces (no corner pointing inwards): split the face first</color>"; return; }
            var caps = ExtrudeCaps(m, op);
            if (caps.Count == 0) throw new Exception("the extrude made no faces");
            int clipped = 0;
            foreach (var cap in caps)
            {
                var ls = LoopsOf(cap); int n = ls.Count; if (n < 3) continue;
                var corners = ls.Select(l => l.vertex).ToList();
                var p = corners.Select(v => v.position).ToList();
                var N = Newell(p); var c = Centroid(p);
                float fit = float.MaxValue; for (int i = 0; i < n; i++) fit = Mathf.Min(fit, Vector3.Distance(c, ClosestOnSeg(c, p[i], p[(i + 1) % n])));
                float rr = Mathf.Min(r, fit * 0.85f); if (rr < r - 1e-4f) clipped++;
                int N2 = Math.Max(count, n);
                var u = Vector3.ProjectOnPlane(p[0] - c, N).normalized; var v = Vector3.Cross(N, u);
                // each corner to a slot of the regular N2-gon, in order round the face
                float step = Mathf.PI * 2f / N2;
                var slot = new int[n]; slot[0] = 0; float prev = 0f;
                for (int i = 1; i < n; i++)
                {
                    float a = Ang(p[i] - c, u, v); while (a < prev) a += Mathf.PI * 2f; prev = a;
                    int sl = Mathf.RoundToInt(a / step);
                    sl = Mathf.Clamp(sl, slot[i - 1] + 1, N2 - (n - i));
                    slot[i] = sl;
                }
                // the cap's edges get the vertices between the slots (the game's own edge split)
                for (int i = 0; i < n; i++)
                {
                    int k = (i + 1 < n ? slot[i + 1] : N2) - slot[i] - 1;
                    var a = corners[i]; var b = corners[(i + 1) % n];
                    var e = Edge.GetConnectingEdge(a, b); var from = a;
                    for (int j = 0; j < k && e != null; j++)
                    {
                        var nv = TopoOperations.SubdivideEdge(m, from, e, out _);
                        if (nv == null) break;
                        e = Edge.GetConnectingEdge(nv, b); from = nv;
                    }
                }
                // every corner of the cap onto the circle (slot order = loop order)
                var ls2 = LoopsOf(cap);
                int start = ls2.FindIndex(l => Same(l.vertex, corners[0]));
                for (int j = 0; j < ls2.Count; j++)
                {
                    var vx = ls2[(start + j) % ls2.Count].vertex;
                    float a = step * j;
                    vx.position = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * rr;
                }
            }
            if (!keep) DeleteFaces(m, op, caps); else SelectOnly(m, caps);
            Status = $"<color=#8f8>Round {(keep ? "inset" : "hole")}: {caps.Count} face(s), {count} edges, {r * 1000f:0} mm radius{(clipped > 0 ? $" <color=#fd8>(smaller on {clipped} face(s) to fit)</color>" : "")}</color>";
        }
        static float Ang(Vector3 d, Vector3 u, Vector3 v) { float a = Mathf.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u)); return a < 0f ? a + Mathf.PI * 2f : a; }
        static Vector3 ClosestOnSeg(Vector3 p, Vector3 a, Vector3 b) { var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-9f, ab.sqrMagnitude)); return a + ab * t; }

        // ============================================================================================ knife
        static Face CommonFace(EditMesh m, Vertex a, Vertex b, out Loop la, out Loop lb)
        {
            la = lb = null;
            foreach (var f in AllFaces(m))
            {
                Loop x = null, y = null;
                foreach (var l in LoopsOf(f)) { if (Same(l.vertex, a)) x = l; else if (Same(l.vertex, b)) y = l; }
                if (x != null && y != null && !Same(x.next, y) && !Same(y.next, x)) { la = x; lb = y; return f; }
            }
            return null;
        }

        static void Connect(EditMesh m, MeshOperation op)
        {
            var vs = InputVertices(m, op);
            if (vs.Count != 2) { Status = "<color=#f88>Select exactly 2 vertices of the same face (points mode)</color>"; return; }
            var f = CommonFace(m, vs[0], vs[1], out var la, out var lb);
            if (f == null) { Status = "<color=#f88>Those 2 vertices aren't on one face (or are already joined by an edge)</color>"; return; }
            TopoOperations.SubdivideFace(m, f, la, lb, out _);
            Status = "<color=#8f8>Face cut between the 2 vertices</color>";
        }

        static Vertex SplitAt(EditMesh m, Edge e, Vertex from, float dist, bool fraction)
        {
            var a = from.position; var b = e.GetOtherVertex(from).position;
            var nv = TopoOperations.SubdivideEdge(m, from, e, out _);
            if (nv != null) { float len = (b - a).magnitude; float t = fraction ? dist : Mathf.Min(dist, len * 0.45f) / Mathf.Max(1e-6f, len); nv.position = Vector3.Lerp(a, b, t); }
            return nv;
        }

        static void CutAcross(EditMesh m, MeshOperation op, float t)
        {
            var es = InputEdges(m, op);
            if (es.Count != 2) { Status = "<color=#f88>Select exactly 2 edges of the same face (edges mode)</color>"; return; }
            var a = SplitAt(m, es[0], es[0].v0, t, true); var b = SplitAt(m, es[1], es[1].v0, t, true);
            if (a == null || b == null) throw new Exception("couldn't split those edges");
            var f = CommonFace(m, a, b, out var la, out var lb);
            if (f == null) throw new Exception("the 2 edges aren't on one face");
            TopoOperations.SubdivideFace(m, f, la, lb, out _);
            Status = $"<color=#8f8>Face cut across at {t * 100f:0}% of both edges</color>";
        }

        static void Subdivide(EditMesh m, MeshOperation op, int cuts)
        {
            var es = InputEdges(m, op);
            if (es.Count == 0) { Status = "<color=#f88>Select one or more edges (edges mode)</color>"; return; }
            foreach (var e0 in es)
            {
                var start = e0.v0; var end = e0.v1; var pa = start.position; var pb = end.position;
                var cur = e0; var from = start;
                for (int k = 1; k <= cuts && cur != null; k++)
                {
                    var nv = TopoOperations.SubdivideEdge(m, from, cur, out _);
                    if (nv == null) break;
                    nv.position = Vector3.Lerp(pa, pb, (float)k / (cuts + 1));
                    cur = Edge.GetConnectingEdge(nv, end); from = nv;
                }
            }
            Status = $"<color=#8f8>{es.Count} edge(s) cut into {cuts + 1}</color>";
        }

        // ============================================================================================ bevel
        static List<Edge> EdgesAt(Vertex v)
        {
            var r = new List<Edge>(); var s = v.firstEdge; var e = s;
            for (int g = 0; e != null && g < 64; g++) { r.Add(e); e = e.GetNextDiskEdge(v); if (Same(e, s)) break; }
            return r;
        }
        static bool FaceHasEdge(Face f, Edge e) => LoopsOf(f).Any(l => Same(l.edge, e));
        static List<Face> FacesOfEdge(EditMesh m, Edge e) => AllFaces(m).Where(f => FaceHasEdge(f, e)).ToList();

        /// <summary>Chamfer: the edge becomes a flat strip of width ~d between its two faces (the game's own edge / face
        /// splits, then the edge's ends are moved onto the strip). Ends must join exactly 3 edges (box-like corners).</summary>
        static void Bevel(EditMesh m, MeshOperation op, float d)
        {
            var es = InputEdges(m, op);
            if (es.Count == 0) { Status = "<color=#f88>Select one or more edges (edges mode)</color>"; return; }
            int ok = 0, skipped = 0;
            foreach (var e in es) { if (BevelEdge(m, e, d)) ok++; else skipped++; }
            Status = skipped == 0 ? $"<color=#8f8>{ok} edge(s) bevelled by {d * 1000f:0} mm</color>" : $"<color=#fd8>{ok} edge(s) bevelled, {skipped} skipped (each end must join exactly 3 edges and the edge must have 2 faces)</color>";
        }

        static bool BevelEdge(EditMesh m, Edge e, float d)
        {
            var v0 = e.v0; var v1 = e.v1;
            var map = EdgeMap(m);
            var at0 = map.TryGetValue(v0.Pointer, out var x0) ? x0 : new List<Edge>(); var at1 = map.TryGetValue(v1.Pointer, out var x1) ? x1 : new List<Edge>();
            var fs = FacesOfEdge(m, e);
            if (at0.Count != 3 || at1.Count != 3 || fs.Count != 2) { MPLog.Info($"Bevel: edge skipped - {at0.Count} and {at1.Count} edges at its ends (need 3 and 3), {fs.Count} faces on it (need 2)"); return false; }
            var F1 = fs[0]; var F2 = fs[1];
            Edge SideEdge(List<Edge> at, Face f) => at.FirstOrDefault(x => !Same(x, e) && FaceHasEdge(f, x));
            var g1 = SideEdge(at0, F1); var g2 = SideEdge(at0, F2); var h1 = SideEdge(at1, F1); var h2 = SideEdge(at1, F2);
            if (g1 == null || g2 == null || h1 == null || h2 == null) return false;
            var a0 = SplitAt(m, g1, v0, d, false); var b0 = SplitAt(m, g2, v0, d, false);
            var a1 = SplitAt(m, h1, v1, d, false); var b1 = SplitAt(m, h2, v1, d, false);
            if (a0 == null || b0 == null || a1 == null || b1 == null) throw new Exception("edge split failed");
            void Cut(Face f, Vertex x, Vertex y)
            {
                var ls = LoopsOf(f); var lx = ls.FirstOrDefault(l => Same(l.vertex, x)); var ly = ls.FirstOrDefault(l => Same(l.vertex, y));
                if (lx == null || ly == null) throw new Exception("bevel: corner not on its face");
                TopoOperations.SubdivideFace(m, f, lx, ly, out _);
            }
            Cut(F1, a0, a1); Cut(F2, b0, b1);
            // the old edge's ends onto the middle of the strip: the two thin faces become one flat chamfer
            v0.position = (a0.position + b0.position) * 0.5f;
            v1.position = (a1.position + b1.position) * 0.5f;
            return true;
        }
    }
}

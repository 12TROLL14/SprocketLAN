using System;
using System.Collections.Generic;
using Sprocket.DamageModelling;
using Sprocket.Vehicles.CrewSystems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMP
{
    /// <summary>War Thunder style hit camera (upper right), drawn entirely by a small software renderer into uGUI meshes
    /// (CanvasRenderer.SetMesh with the default UI material = unlit texture * vertex colour, i.e. full-bright albedo).
    ///  0..2 s  chase: the victim's tank, opaque and textured (albedo only), shell flying the last 2 m into the plate.
    ///  2..4 s  swing: camera turns to the side and zooms out while the tank fades to an x-ray glow; spall in slow motion.
    ///  4..+N s linger: slow orbit, damaged module list.
    /// Geometry comes from the shooter's own copy of the victim (same design): render meshes for the hull look, and the
    /// game's damage-model meshes (exactly what the penetration simulation hit) for modules and the struck plate.
    /// NOTE: all per-vertex math is plain float arithmetic - Unity struct methods are native calls under IL2CPP interop.</summary>
    public class PenCamView
    {
        readonly Transform _canvas; readonly TMP_FontAsset _font;
        GameObject _panel;
        TextMeshProUGUI _title, _list;
        PenRecord _rec;
        float _start;
        string _listText, _baseTitle; bool _titled; int _titledKill = -1;

        const float W = 560, H = 400, DW = 544, DH = 300;
        const float TIn = 2.0f / 1.5f, TSwing = 2.0f;
        float Total => TIn + TSwing + Mathf.Max(1f, Plugin.PenCamSeconds.Value);
        const float Approach = 2f;     // shell appears 2 m before the plate
        const float ShellSpeed = Approach / TIn;
        float _tPen = TIn; Vector3 _exitPt;   // when/where the shell reaches the last plate it goes through

        // ------------------------------------------------------------------ scene
        class Shape
        {
            public Vector3[] V, N; public Vector2[] UV; public int[] T;
            public IntPtr Tex; public Texture TexObj; public Color Tint = Color.white;
            public int Mod = -1; public Color Col;
            public float Cx, Cy, Cz;
            public float[] Sx, Sy; public bool[] Ok;     // per-frame projection
            public int PartType; public IntPtr OwnerGO; public bool Crew;
            public Transform Src; public Vector3[] LV, LN; public Matrix4x4 Built;
            public Dictionary<(long, long), List<int>> EdgeTris;
            public bool[] Front; public Edge[] Edges;
        }
        class Mod { public bool Hit, Prior; public float Before, After, Max; }
        List<Shape> _hull = new List<Shape>();       // exterior render meshes (from the cached Geo)
        List<Shape> _modules = new List<Shape>();    // module meshes (Shape.Mod = pool index)
        readonly Dictionary<int, Mod> _mods = new Dictionary<int, Mod>();
        readonly List<Vector3> _ammoBlasts = new List<Vector3>();
        // victim -> pool -> health fraction after your last hit (cleared when that player dies)
        static readonly Dictionary<byte, Dictionary<int, float>> _known = new Dictionary<byte, Dictionary<int, float>>();
        public static void ForgetVictim(byte victim) => _known.Remove(victim);
        bool _ammoSoundPlayed, _killSoundPlayed;
        const float TAmmo = TIn + 2.0f;    // module damage flashing ends here
        Geo _geo;
        readonly HashSet<int> _deadCrew = new HashSet<int>();
        readonly List<(Vector3 a, Vector3 b)> _plateEdges = new List<(Vector3, Vector3)>();
        readonly List<Vector3> _plateTris = new List<Vector3>();
        struct FragLine { public Vector3 A, B; public Color Col; public Color32 C32; public float W, T0, T1; }
        readonly List<FragLine> _frags = new List<FragLine>();
        Vector3 _entry, _dir, _up, _side, _focus; float _radius;

        // ------------------------------------------------------------------ output layers
        class Chunk { public CanvasRenderer CR; public Mesh M; public IntPtr Tex; public Vector3[] V = new Vector3[ChunkVerts]; public Color32[] C = new Color32[ChunkVerts]; public Vector2[] UV = new Vector2[ChunkVerts]; public int Vn; public List<int> T = new List<int>(); }
        class Layer { public Transform Parent; public List<Chunk> Chunks = new List<Chunk>(); }
        Layer _texLayer, _fxLayer;
        const int ChunkVerts = 60000;

        public PenCamView(Transform canvas, TMP_FontAsset font) { _canvas = canvas; _font = font; PreJit(); }

        static bool _jitted;
        /// <summary>Compile the pen cam code ahead of time on a worker thread (first-use JIT caused a hitch on the first pen cam).</summary>
        static void PreJit()
        {
            if (_jitted) return; _jitted = true;
            var types = new[] { typeof(PenCamView), typeof(PenCapture), typeof(PenRecord), typeof(Sounds) };
            new System.Threading.Thread(() =>
            {
                int n = 0;
                foreach (var ty in types)
                    foreach (var nt in System.Linq.Enumerable.Concat(new[] { ty }, ty.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)))
                        foreach (var m in nt.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters || nt.ContainsGenericParameters) continue;
                            try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); n++; } catch { }
                        }
                MPLog.Debug($"PenCam pre-JIT: {n} methods");
            }) { IsBackground = true }.Start();
        }

        public void Update()
        {
            var bm = Runner.Battle;
            var rec = bm?.LatestPenCam;
            if (rec != null && rec != _rec) { _rec = rec; try { Build(rec); } catch (Exception e) { MPLog.Warn("PenCam view: " + e); Teardown(); } }
            if (_panel == null) { try { Prewarm(); } catch (Exception e) { MPLog.Debug("PenCam prewarm: " + e.Message); _nextPrewarm = Time.time + 5f; } return; }
            float t = Time.time - _start;
            if (t > Total || (!bm.InBattle && _rec.Victim != PenCapture.TestVictimId)) { Teardown(); return; }
            try { Render(t); } catch (Exception e) { MPLog.Warn("PenCam render: " + e); Teardown(); }
        }

        void Teardown()
        {
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            _panel = null; _texLayer = _fxLayer = null;

        }

        // ================================================================== build
        void Build(PenRecord rec)
        {
            Teardown();
            _plates.Clear(); _mods.Clear(); _tPen = TIn; _frags.Clear(); _plateEdges.Clear(); _plateTris.Clear();
            _start = Time.time; _titled = false;
            float t0 = Time.realtimeSinceStartup;

            float sc = Mathf.Clamp(Plugin.PenCamScale.Value, 0.4f, 3f);
            _panel = new GameObject("PenCam"); _panel.transform.SetParent(_canvas, false);
            var prt = _panel.AddComponent<RectTransform>();
            bool test = rec.Victim == PenCapture.TestVictimId;   // sandbox: keep clear of the designer's panels on the right
            prt.anchorMin = prt.anchorMax = prt.pivot = test ? new Vector2(0.5f, 1) : new Vector2(1, 1);
            prt.anchoredPosition = test ? new Vector2(0, -70) : new Vector2(-12, -96); prt.sizeDelta = new Vector2(W, H); prt.localScale = Vector3.one * sc;
            var bg = _panel.AddComponent<Image>(); bg.color = new Color(0.03f, 0.05f, 0.07f, 0.9f); bg.raycastTarget = false;
            _panel.AddComponent<CanvasGroup>();
            var rawGO = new GameObject("Solid"); rawGO.transform.SetParent(_panel.transform, false);
            var rrt = rawGO.AddComponent<RectTransform>();
            rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0.5f, 1); rrt.anchoredPosition = new Vector2(0, -36); rrt.sizeDelta = new Vector2(DW, DH);
            _raw = rawGO.AddComponent<RawImage>(); _raw.raycastTarget = false; _raw.color = new Color(1, 1, 1, 0);
            _texLayer = NewLayer("Tex"); _fxLayer = NewLayer("Fx");
            _title = Text(_panel.transform, new Vector2(0, -4), new Vector2(W - 16, 30), 22, TextAlignmentOptions.Top);
            _title.text = $"<color=#9aa>{(rec.Victim == PenCapture.TestVictimId ? "sandbox test" : Runner.Battle.NameOf(rec.Victim))}</color>";
            _list = Text(_panel.transform, new Vector2(0, -36 - DH - 2), new Vector2(W - 16, H - DH - 42), 14, TextAlignmentOptions.TopLeft);
            _list.text = "";

            _entry = rec.Entry;
            _dir = rec.ShellDir.sqrMagnitude > 0.01f ? rec.ShellDir.normalized : Vector3.forward;
            foreach (var f in rec.Frags) if (f.Original) { _entry = f.Start; if (f.Dir.sqrMagnitude > 0.01f) _dir = f.Dir.normalized; break; }

            VehicleRig rig;
            if (rec.Victim == PenCapture.TestVictimId) rig = PenCapture.TestRig;
            else Runner.Battle.PuppetByPlayer.TryGetValue(rec.Victim, out rig);
            var geo = GeoFor(rig, finishNow: true);
            bool killed = rec.Victim != PenCapture.TestVictimId && Runner.Battle.KillNear(rec.Victim, Time.time) >= 0;
            if (geo != null && rig?.GO != null && !killed)   // a destroyed tank may have its turret blown off: keep the cached pose
            {
                // the turret/gun may be pointing elsewhere than when the model was cached: move those parts
                var done = new HashSet<Shape>();
                bool moved = Repose(geo.Hull, rig.GO.transform, done);
                moved |= Repose(geo.DmHull, rig.GO.transform, done);
                moved |= Repose(geo.Modules, rig.GO.transform, done);
                if (moved)
                {
                    foreach (var m in geo.Modules) if (m.Src == null && m.V.Length > 0) { float cx = 0, cy = 0, cz = 0; foreach (var p in m.V) { cx += p.x; cy += p.y; cz += p.z; } m.Cx = cx / m.V.Length; m.Cy = cy / m.V.Length; m.Cz = cz / m.V.Length; }
                    if (geo.Gpu != null) UnityEngine.Object.Destroy(geo.Gpu); if (geo.Lines != null) UnityEngine.Object.Destroy(geo.Lines);
                    geo.Gpu = geo.Lines = null;
                    BuildGpu(geo);
                }
            }
            _geo = geo;
            _hull = geo != null ? geo.Hull : new List<Shape>();
            _modules = geo != null ? geo.Modules : new List<Shape>();
            var names = geo != null ? geo.Names : new Dictionary<int, string>();
            var dmHull = geo != null ? geo.DmHull : new List<Shape>();
            bool layoutOk = rig != null && rig.PoolLayoutHash == rec.Layout;
            _mods.Clear();
            foreach (var d in rec.Damage) if (layoutOk) _mods[d.Pool] = new Mod { Hit = true, Before = d.Before, After = d.After, Max = Mathf.Max(d.Max, 0.001f) };
            if (layoutOk && rec.Victim != PenCapture.TestVictimId)
            {
                if (!_known.TryGetValue(rec.Victim, out var kn)) _known[rec.Victim] = kn = new Dictionary<int, float>();
                foreach (var d in rec.Damage)
                {
                    bool crew = false; if (geo != null) foreach (var m in geo.Modules) if (m.Mod == d.Pool) { crew = m.Crew; break; }
                    if (!crew) kn[d.Pool] = d.After / Mathf.Max(d.Max, 0.001f);   // crew heal / swap seats: don't carry their state over
                }
            }
            if (geo != null) { _focus = geo.Focus; _radius = geo.Radius; }
            _ammoBlasts.Clear(); _ammoSoundPlayed = false; _killSoundPlayed = false;
            if (geo != null)
                foreach (var kv in _mods)
                {
                    if (!geo.Ammo.Contains(kv.Key) || kv.Value.After > 0.01f * kv.Value.Max) continue;
                    float sx = 0, sy = 0, sz = 0; int n = 0;
                    foreach (var s in geo.Modules) if (s.Mod == kv.Key && !s.Crew) { sx += s.Cx; sy += s.Cy; sz += s.Cz; n++; }
                    if (n > 0) _ammoBlasts.Add(new Vector3(sx / n, sy / n, sz / n));
                }
            // crew already dead before this hit are not drawn (synced health of the puppet; this hit's own kills stay)
            _deadCrew.Clear();
            foreach (var dc in rec.DeadCrew) _deadCrew.Add(dc);            // reported by the target itself
            if (rig != null)
            {
                var hitBefore = new Dictionary<int, PenRecord.Dmg>(); foreach (var d in rec.Damage) hitBefore[d.Pool] = d;
                foreach (var m in _modules)
                {
                    if (!m.Crew || m.Mod < 0 || _deadCrew.Contains(m.Mod) || m.Mod >= rig.Pools.Count || rig.Pools[m.Mod] == null) continue;
                    float max = 1f, cur = 1f;
                    try { max = Mathf.Max(rig.Pools[m.Mod].max, 0.001f); cur = rig.Pools[m.Mod].Current; } catch { continue; }
                    if (hitBefore.TryGetValue(m.Mod, out var hd)) cur = hd.Before;
                    if (cur <= 0.01f * max) _deadCrew.Add(m.Mod);
                }
                // modules damaged by your earlier hits on this tank (remembered from previous pen cams) keep their damage colour
                if (layoutOk && _known.TryGetValue(rec.Victim, out var known))
                    foreach (var kv in known)
                        if (!_mods.ContainsKey(kv.Key)) _mods[kv.Key] = new Mod { Prior = true, Before = kv.Value, After = kv.Value, Max = 1f };
            }
            else { _focus = _entry; _radius = 3f; }

            // ---- snap the impact onto the armour along the shell path and find the struck plate
            FindPlate(geo != null && geo.PlateArmour.Count > 0 ? geo.PlateArmour : dmHull.Count > 0 ? dmHull : _hull);
            if (geo != null && geo.Gpu != null && _rt == null)
            {
                _rt = new RenderTexture((int)DW * 2, (int)DH * 2, 24); _rt.antiAliasing = 4;
                _cb = new UnityEngine.Rendering.CommandBuffer();
            }
            if (_rt != null) _raw.texture = _rt;

            _up = Vector3.up - _dir * Vector3.Dot(Vector3.up, _dir); if (_up.sqrMagnitude < 0.01f) _up = Vector3.forward; _up.Normalize();
            _side = Vector3.Cross(_up, _dir).normalized;

            // ---- fragments
            int fi = 0;
            foreach (var f in rec.Frags)
            {
                float len = Mathf.Min(f.Length, 10f);
                if (len < 0.02f && !f.Original) continue;
                bool absorbed = (f.Flags & 4) != 0, bounced = (f.Flags & 8) != 0;
                var col = f.Original ? (bounced ? new Color(0.5f, 1f, 0.8f, 1f) : new Color(0.35f, 1f, 0.35f, 1f))      // shell path: green
                                     : (f.Flags & 128) != 0 ? new Color(1f, 0.92f, 0.2f, 0.95f)                                               // spall cone: yellow
                                     : (f.Flags & 64) != 0 ? (rec.ShellKind == 2 ? new Color(1f, 0.5f, 0.1f, 0.95f) : new Color(1f, 0.3f, 0.9f, 0.95f))   // HE burst orange, APHE burst magenta
                                     : (absorbed ? new Color(1f, 0.55f, 0.15f, 0.7f) : new Color(1f, 0.35f, 0.08f, 0.95f));
                float a = _tPen + (f.Original ? 0f : (f.Flags & 64) != 0 ? 0.3f : 0.12f) + (fi++ % 9) * 0.015f;
                float b = a + Mathf.Clamp(len / Mathf.Max(f.Speed, 80f) * 15f, 0.6f, 2.0f);
                _frags.Add(new FragLine { A = f.Start, B = f.Start + f.Dir * len, Col = col, C32 = To32(col), W = f.Original ? 3f : 1.6f, T0 = a, T1 = b });
            }

            // ---- sandbox: nothing is really damaged, so estimate which modules the fragments pass through
            if (rec.Victim == PenCapture.TestVictimId && rec.Damage.Count == 0 && _modules.Count > 0)
            {
                var boxes = new Dictionary<int, (Vector3 mn, Vector3 mx)>();
                foreach (var m in _modules)
                {
                    if (m.Mod < 0) continue;
                    var mn = boxes.TryGetValue(m.Mod, out var bb) ? bb.mn : V3(1e9f, 1e9f, 1e9f); var mx = boxes.ContainsKey(m.Mod) ? bb.mx : V3(-1e9f, -1e9f, -1e9f);
                    foreach (var p in m.V) { mn = V3(Math.Min(mn.x, p.x), Math.Min(mn.y, p.y), Math.Min(mn.z, p.z)); mx = V3(Math.Max(mx.x, p.x), Math.Max(mx.y, p.y), Math.Max(mx.z, p.z)); }
                    boxes[m.Mod] = (mn, mx);
                }
                var hits = new Dictionary<int, int>();
                foreach (var f in rec.Frags)
                {
                    if (f.Original && (f.Flags & 64) == 0) continue;          // the shell itself is shown, spall/burst does the damage
                    var b = f.Start + f.Dir * Mathf.Min(f.Length, 10f);
                    foreach (var kv in boxes) if (SegBox(f.Start, b, kv.Value.mn, kv.Value.mx)) hits[kv.Key] = (hits.TryGetValue(kv.Key, out var c) ? c : 0) + 1;
                }
                foreach (var kv in hits)
                {
                    float after = Mathf.Max(0f, 1f - 0.34f * kv.Value);
                    rec.Damage.Add(new PenRecord.Dmg { Pool = (ushort)kv.Key, Before = 1f, After = after, Max = 1f });
                    _mods[kv.Key] = new Mod { Hit = true, Before = 1f, After = after, Max = 1f };
                }
            }

            // ---- module list
            var sb = new System.Text.StringBuilder();
            foreach (var d in rec.Damage)
            {
                string name = $"module {d.Pool}";
                if (names.TryGetValue(d.Pool, out var nm)) name = nm;
                else if (rig != null && rig.PoolLayoutHash == rec.Layout && d.Pool < rig.PoolOwners.Count && rig.PoolOwners[d.Pool] != null)
                    name = VehicleRig.StripName(rig.PoolOwners[d.Pool].gameObject.name);
                bool dead = d.After <= 0.01f * Mathf.Max(d.Max, 0.001f);
                bool crew = false; foreach (var m in _modules) if (m.Mod == d.Pool) { crew = m.Crew; break; }
                string state = crew ? (dead ? "killed" : "wounded") : (dead ? "destroyed" : "damaged");
                sb.Append($"<color={(dead ? "#ff3030" : "#ff9030")}>{name}</color> {state}   ");
            }
            if (rec.Damage.Count == 0) sb.Append("<color=#9aa>no module damage</color>");
            _listText = sb.ToString();
            MPLog.Info($"PenCam built in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms: entry {(_entry - _focus).magnitude:F1} m from tank centre, plate edges {_plateEdges.Count}");
        }

        // ================================================================== cached per-tank geometry
        // Everything that depends only on the victim's tank (not on the hit) is built once per puppet, spread over
        // several frames in the background (2 ms per frame) so the pen cam opens without a hitch.
        class Geo
        {
            public List<Shape> Hull = new List<Shape>(), Modules = new List<Shape>(), DmHull = new List<Shape>(), PlateArmour = new List<Shape>();
            public Dictionary<int, string> Names = new Dictionary<int, string>();
            public HashSet<int> Ammo = new HashSet<int>();
            public Vector3 Focus; public float Radius = 3f;
            public Mesh Gpu, Lines;
            public bool Done; public int Layout;
            public IEnumerator<int> Builder;
        }
        static readonly Dictionary<IntPtr, Geo> _geos = new Dictionary<IntPtr, Geo>();
        float _nextPrewarm;

        public static void Invalidate(VehicleRig rig)
        {
            try { if (rig?.GO != null && _geos.Remove(rig.GO.Pointer, out var g)) { if (g.Gpu != null) UnityEngine.Object.Destroy(g.Gpu); if (g.Lines != null) UnityEngine.Object.Destroy(g.Lines); } } catch { }
        }

        Geo GeoFor(VehicleRig rig, bool finishNow)
        {
            if (rig?.GO == null) return null;
            var key = rig.GO.Pointer;
            if (_geos.TryGetValue(key, out var g) && g.Layout != rig.PoolLayoutHash) { _geos.Remove(key); g = null; }
            if (g == null) { g = new Geo { Layout = rig.PoolLayoutHash }; g.Builder = BuildGeo(rig, g); _geos[key] = g; }
            if (!g.Done)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (g.Builder.MoveNext()) if (!finishNow && sw.Elapsed.TotalMilliseconds > 2.0) return null;
                }
                catch (Exception e) { MPLog.Warn("PenCam geometry: " + e); }
                g.Done = true;
                MPLog.Info($"PenCam geometry for '{rig.GO.name}' ready ({(finishNow ? "on demand" : "prewarmed")})");
            }
            return g;
        }

        /// <summary>Background prewarm: build geometry for every puppet a little at a time while no pen cam is showing.</summary>
        void Prewarm()
        {
            var bm = Runner.Battle;
            if (bm == null || !bm.InBattle || Time.time < _nextPrewarm) return;
            if (_rt == null)
            {
                _rt = new RenderTexture((int)DW * 2, (int)DH * 2, 24); _rt.antiAliasing = 4; _rt.Create();
                _cb = new UnityEngine.Rendering.CommandBuffer();
                if (_matSolid == null) _matSolid = ColoredMat(false);
                if (_matLine == null) _matLine = ColoredMat(true);
                return;
            }
            foreach (var kv in bm.PuppetByPlayer)
            {
                var rig = kv.Value; if (rig?.GO == null || rig.PoolOwners.Count == 0) continue;
                if (_geos.TryGetValue(rig.GO.Pointer, out var g) && g.Done && g.Layout == rig.PoolLayoutHash) continue;
                GeoFor(rig, finishNow: false);
                return;   // one tank at a time
            }
            _nextPrewarm = Time.time + 1f;
        }

        IEnumerator<int> BuildGeo(VehicleRig rig, Geo geo)
        {
            Transform root = rig.GO.transform;
            float bx0 = 1e9f, by0 = 1e9f, bz0 = 1e9f, bx1 = -1e9f, by1 = -1e9f, bz1 = -1e9f;

            // ---- part types of every model renderer (interior parts are left out of the exterior view)
            var partOf = new Dictionary<IntPtr, int>(); var ownerOfR = new Dictionary<IntPtr, IntPtr>();
            foreach (var vc in rig.GO.GetComponentsInChildren<Sprocket.Vehicles.VehicleComponent>(true))
            {
                Il2CppSystem.Collections.Generic.List<Sprocket.Vehicles.VehicleRendererGroup> groups = null;
                try { groups = vc?.models; } catch { }
                if (groups == null) continue;
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi]; if (g == null) continue;
                    var lods = g.lods; if (lods == null) continue;
                    for (int li = 0; li < lods.Length; li++) if (lods[li]?.renderer != null) { partOf[lods[li].renderer.Pointer] = (int)g.partType; ownerOfR[lods[li].renderer.Pointer] = vc.gameObject.Pointer; }
                }
            }
            yield return 0;
            const int InteriorTypes = 4 | 16 | 32 | 64 | 128 | 256 | 1024 | 16384;   // powertrain crew ammo fuel equipment stowage motor plate-interior

            // ---- exterior render meshes
            int n = 0;
            foreach (var r in rig.GO.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || n++ > 1500) continue;
                partOf.TryGetValue(r.Pointer, out int pt);
                if ((pt & InteriorTypes) != 0 || IsEffect(r.transform, root)) continue;
                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) { var smr = r.TryCast<SkinnedMeshRenderer>(); if (smr != null) mesh = smr.sharedMesh; }
                if (!ReadMesh(mesh, out var v, out var uv, out var tri, out var nr)) continue;
                var sh = MakeShape(v, nr, tri, r.transform, root);
                sh.PartType = pt; ownerOfR.TryGetValue(r.Pointer, out sh.OwnerGO);
                geo.Hull.Add(sh);
                if ((pt & 1) != 0) geo.PlateArmour.Add(sh);      // PlateStructure: real armour plates (no tracks, wheels, add-ons)
                foreach (var p in sh.V) { bx0 = Math.Min(bx0, p.x); by0 = Math.Min(by0, p.y); bz0 = Math.Min(bz0, p.z); bx1 = Math.Max(bx1, p.x); by1 = Math.Max(by1, p.y); bz1 = Math.Max(bz1, p.z); }
                yield return 0;
            }
            {
                var hist = new Dictionary<int, int>(); foreach (var h in geo.Hull) hist[h.PartType] = hist.TryGetValue(h.PartType, out var c) ? c + 1 : 1;
                var sb = new System.Text.StringBuilder(); foreach (var kv in hist) sb.Append($" {kv.Key}x{kv.Value}");
                MPLog.Info("PenCam exterior part types:" + sb);
                // plate structures report PlateStructure, or no type at all (not part of any model group)
                if (geo.PlateArmour.Count == 0)
                    foreach (var h in geo.Hull) if (h.PartType == 0) geo.PlateArmour.Add(h);
            }
            if (bx1 >= bx0)
            {
                geo.Focus = V3((bx0 + bx1) * 0.5f, (by0 + by1) * 0.5f, (bz0 + bz1) * 0.5f);
                float ex = (bx1 - bx0) * 0.5f, ey = (by1 - by0) * 0.5f, ez = (bz1 - bz0) * 0.5f;
                geo.Radius = Mathf.Clamp((float)Math.Sqrt(ex * ex + ey * ey + ez * ez), 1.2f, 8f);
            }

            // ---- damage model armour (fallback for the plate search)
            foreach (var cs in rig.GO.GetComponentsInChildren<CompoundStructure>(true))
                foreach (var (tr, v, tri) in DamageModelMeshes(cs))
                    if (!IsRunningGear(PathName(tr, root))) geo.DmHull.Add(MakeShape(v, null, tri, tr, root));
            yield return 0;

            // ---- modules
            var crew = PtrSet(rig.CrewPools); var ammo = PtrSet(rig.AmmoPools);
            for (int i = 0; i < rig.PoolOwners.Count; i++)
            {
                var o = rig.PoolOwners[i]; if (o == null) continue;
                var ptr = i < rig.Pools.Count && rig.Pools[i] != null ? rig.Pools[i].Pointer : IntPtr.Zero;
                bool isCrew = crew.Contains(ptr), isAmmo = ammo.Contains(ptr);
                var seat = o.TryCast<CrewSeat>();
                if (seat != null) { isCrew = true; geo.Names[i] = CrewRole(seat); }
                if (isAmmo && !isCrew) geo.Ammo.Add(i);
                // exterior modules (tracks...) reuse their visible model from the exterior meshes
                var shapes = new List<Shape>();
                if (seat == null)
                {
                    var og = o.gameObject.Pointer;
                    foreach (var h in geo.Hull) if (h.OwnerGO == og && (h.PartType & 1) == 0) shapes.Add(new Shape { V = h.V, N = h.N, T = h.T, Cx = h.Cx, Cy = h.Cy, Cz = h.Cz, Sx = new float[h.V.Length], Sy = new float[h.V.Length], Ok = new bool[h.V.Length] });
                }
                bool exterior = shapes.Count > 0;
                if (!exterior) shapes = FallbackModuleShapes(o, seat, root);
                if (shapes.Count == 0) continue;
                float ext = 0; foreach (var s in shapes) ext = Math.Max(ext, Extent(s));
                if (ext > 4.5f && !exterior) continue;
                var col = isCrew ? new Color(0.3f, 0.9f, 0.45f, 1f) : isAmmo ? new Color(1f, 0.8f, 0.25f, 1f) : new Color(0.75f, 0.78f, 0.82f, 1f);
                foreach (var s in shapes) { s.Mod = i; s.Col = col; s.Crew = isCrew; geo.Modules.Add(s); }
                yield return 0;
            }
            foreach (var s in geo.PlateArmour) { EdgeTris(s); yield return 0; }
            BuildGpu(geo);
            MPLog.Info($"PenCam geometry: {geo.Hull.Count} exterior meshes ({geo.PlateArmour.Count} armour plates), {geo.Modules.Count} module meshes");
        }

        // ------------------------------------------------------------------ geometry helpers
        static bool SegBox(Vector3 a, Vector3 b, Vector3 mn, Vector3 mx)
        {
            float t0 = 0f, t1 = 1f;
            for (int ax = 0; ax < 3; ax++)
            {
                float s = ax == 0 ? a.x : ax == 1 ? a.y : a.z, d = (ax == 0 ? b.x : ax == 1 ? b.y : b.z) - s;
                float lo = ax == 0 ? mn.x : ax == 1 ? mn.y : mn.z, hi = ax == 0 ? mx.x : ax == 1 ? mx.y : mx.z;
                if (Math.Abs(d) < 1e-6f) { if (s < lo || s > hi) return false; continue; }
                float u0 = (lo - s) / d, u1 = (hi - s) / d; if (u0 > u1) { var t = u0; u0 = u1; u1 = t; }
                t0 = Math.Max(t0, u0); t1 = Math.Min(t1, u1); if (t0 > t1) return false;
            }
            return true;
        }

        static Vector3 V3(float x, float y, float z) { Vector3 v; v.x = x; v.y = y; v.z = z; return v; }
        static Vector2 V2(float x, float y) { Vector2 v; v.x = x; v.y = y; return v; }

        static float Extent(Shape s)
        {
            float x0 = 1e9f, y0 = 1e9f, z0 = 1e9f, x1 = -1e9f, y1 = -1e9f, z1 = -1e9f;
            foreach (var p in s.V) { x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); z0 = Math.Min(z0, p.z); x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); z1 = Math.Max(z1, p.z); }
            return (x1 - x0) + (y1 - y0) + (z1 - z0);
        }

        static Shape MakeShape(Vector3[] v, Vector3[] nrm, int[] tri, Transform t, Transform root)
        {
            var s = new Shape { V = new Vector3[v.Length], N = new Vector3[v.Length], T = tri, Src = t, LV = v, LN = nrm != null && nrm.Length == v.Length ? nrm : null };
            var m = root.worldToLocalMatrix * t.localToWorldMatrix;
            float det = m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);
            if (det < 0)   // mirrored part: flip winding so "front" stays outward
            {
                var t2 = new int[tri.Length]; for (int i = 0; i + 2 < tri.Length; i += 3) { t2[i] = tri[i]; t2[i + 1] = tri[i + 2]; t2[i + 2] = tri[i + 1]; }
                s.T = t2;
            }
            s.Sx = new float[v.Length]; s.Sy = new float[v.Length]; s.Ok = new bool[v.Length];
            Place(s, m);
            return s;
        }

        /// <summary>(Re)computes the shape's vehicle-space vertices from its own mesh space (in place: copies share arrays).</summary>
        static void Place(Shape s, Matrix4x4 m)
        {
            s.Built = m;
            var v = s.LV; var nrm = s.LN;
            float m00 = m.m00, m01 = m.m01, m02 = m.m02, m03 = m.m03, m10 = m.m10, m11 = m.m11, m12 = m.m12, m13 = m.m13, m20 = m.m20, m21 = m.m21, m22 = m.m22, m23 = m.m23;
            float cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < v.Length; i++)
            {
                float x = v[i].x, y = v[i].y, z = v[i].z;
                float px = m00 * x + m01 * y + m02 * z + m03, py = m10 * x + m11 * y + m12 * z + m13, pz = m20 * x + m21 * y + m22 * z + m23;
                s.V[i] = V3(px, py, pz); cx += px; cy += py; cz += pz;
                if (nrm != null)
                {
                    float nx = nrm[i].x, ny = nrm[i].y, nz = nrm[i].z;
                    float qx = m00 * nx + m01 * ny + m02 * nz, qy = m10 * nx + m11 * ny + m12 * nz, qz = m20 * nx + m21 * ny + m22 * nz;
                    float l = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz) + 1e-9f;
                    s.N[i] = V3(qx / l, qy / l, qz / l);
                }
            }
            if (nrm == null) RecomputeNormals(s);
            s.Cx = cx / Math.Max(1, v.Length); s.Cy = cy / Math.Max(1, v.Length); s.Cz = cz / Math.Max(1, v.Length);
            s.EdgeTris = null;
        }

        /// <summary>Moves shapes whose part moved since they were built (turret traverse, gun elevation). True if any did.</summary>
        static bool Repose(IEnumerable<Shape> shapes, Transform root, HashSet<Shape> done)
        {
            bool any = false;
            var w2l = root.worldToLocalMatrix;
            foreach (var s in shapes)
            {
                if (s.Src == null || s.LV == null || !done.Add(s)) continue;
                Matrix4x4 m;
                try { if (s.Src.WasCollected) continue; m = w2l * s.Src.localToWorldMatrix; } catch { continue; }
                var b = s.Built;
                float d = Math.Abs(m.m00 - b.m00) + Math.Abs(m.m01 - b.m01) + Math.Abs(m.m02 - b.m02) + Math.Abs(m.m03 - b.m03)
                        + Math.Abs(m.m10 - b.m10) + Math.Abs(m.m11 - b.m11) + Math.Abs(m.m12 - b.m12) + Math.Abs(m.m13 - b.m13)
                        + Math.Abs(m.m20 - b.m20) + Math.Abs(m.m21 - b.m21) + Math.Abs(m.m22 - b.m22) + Math.Abs(m.m23 - b.m23);
                if (d < 1e-3f) continue;
                Place(s, m); any = true;
            }
            return any;
        }

        static void RecomputeNormals(Shape s)
        {
            int n = s.V.Length; var acc = new float[n * 3]; var V = s.V; var T = s.T;
            for (int i = 0; i + 2 < T.Length; i += 3)
            {
                int ia = T[i], ib = T[i + 1], ic = T[i + 2];
                float ux = V[ib].x - V[ia].x, uy = V[ib].y - V[ia].y, uz = V[ib].z - V[ia].z;
                float vx = V[ic].x - V[ia].x, vy = V[ic].y - V[ia].y, vz = V[ic].z - V[ia].z;
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                acc[ia * 3] += nx; acc[ia * 3 + 1] += ny; acc[ia * 3 + 2] += nz;
                acc[ib * 3] += nx; acc[ib * 3 + 1] += ny; acc[ib * 3 + 2] += nz;
                acc[ic * 3] += nx; acc[ic * 3 + 1] += ny; acc[ic * 3 + 2] += nz;
            }
            for (int i = 0; i < n; i++)
            {
                float x = acc[i * 3], y = acc[i * 3 + 1], z = acc[i * 3 + 2], l = (float)Math.Sqrt(x * x + y * y + z * z) + 1e-9f;
                s.N[i] = V3(x / l, y / l, z / l);
            }
        }

        /// <summary>Mesh geometry; for non-readable (imported) meshes, read the GPU vertex/index buffers back.</summary>
        static bool ReadMesh(Mesh m, out Vector3[] v, out Vector2[] uv, out int[] tri, out Vector3[] nrm)
        {
            v = null; uv = null; tri = null; nrm = null;
            if (m == null) return false;
            try
            {
                if (m.isReadable)
                {
                    v = m.vertices; tri = m.triangles; uv = m.uv; nrm = m.normals;
                    return v != null && tri != null && v.Length > 0 && tri.Length >= 3;
                }
                int vc = m.vertexCount; if (vc <= 0 || vc > 250000) return false;
                var P = UnityEngine.Rendering.VertexAttribute.Position;
                if (m.GetVertexAttributeFormat(P) != UnityEngine.Rendering.VertexAttributeFormat.Float32) return false;
                int stream = m.GetVertexAttributeStream(P), off = m.GetVertexAttributeOffset(P), stride = m.GetVertexBufferStride(stream);
                var vbytes = GpuBytes(m.GetVertexBuffer(stream), vc * stride);
                if (vbytes == null) return false;
                v = new Vector3[vc];
                for (int i = 0; i < vc; i++) { int o = i * stride + off; v[i] = V3(BitConverter.ToSingle(vbytes, o), BitConverter.ToSingle(vbytes, o + 4), BitConverter.ToSingle(vbytes, o + 8)); }
                var U = UnityEngine.Rendering.VertexAttribute.TexCoord0;
                if (m.HasVertexAttribute(U) && m.GetVertexAttributeFormat(U) == UnityEngine.Rendering.VertexAttributeFormat.Float32)
                {
                    int us = m.GetVertexAttributeStream(U), uo = m.GetVertexAttributeOffset(U), ust = m.GetVertexBufferStride(us);
                    var ub = us == stream ? vbytes : GpuBytes(m.GetVertexBuffer(us), vc * ust);
                    if (ub != null) { uv = new Vector2[vc]; for (int i = 0; i < vc; i++) { int o = i * ust + uo; uv[i] = V2(BitConverter.ToSingle(ub, o), BitConverter.ToSingle(ub, o + 4)); } }
                }
                var ib = m.GetIndexBuffer();
                int ic = ib.count, isz = ib.stride;
                var ibytes = GpuBytes(ib, ic * isz);
                if (ibytes == null) return false;
                tri = new int[ic];
                for (int i = 0; i < ic; i++) { tri[i] = isz == 2 ? BitConverter.ToUInt16(ibytes, i * 2) : BitConverter.ToInt32(ibytes, i * 4); if ((uint)tri[i] >= (uint)vc) return false; }
                return tri.Length >= 3;
            }
            catch (Exception e) { MPLog.Debug("PenCam mesh read: " + e.Message); return false; }
        }

        static byte[] GpuBytes(GraphicsBuffer gb, int len)
        {
            if (gb == null) return null;
            try
            {
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(len);
                gb.GetData(new Il2CppSystem.Array(arr.Pointer));
                var b = new byte[len];
                System.Runtime.InteropServices.Marshal.Copy((IntPtr)((long)arr.Pointer + 4 * IntPtr.Size), b, 0, len);
                return b;
            }
            finally { gb.Dispose(); }
        }

        static readonly string[] TexProps = { "_BaseColorMap", "_BaseMap", "_MainTex", "_Albedo", "_AlbedoMap", "_BaseTexture", "_Diffuse" };
        static readonly string[] ColProps = { "_BaseColor", "_Color" };

        static void MaterialTexture(Renderer r, Shape s)
        {
            Texture tex = null; Color tint = Color.white;
            try
            {
                var mat = r.sharedMaterial;
                if (mat != null)
                {
                    foreach (var p in TexProps) if (mat.HasProperty(p)) { tex = mat.GetTexture(p); if (tex != null) break; }
                    if (tex == null) tex = mat.mainTexture;
                    foreach (var p in ColProps) if (mat.HasProperty(p)) { tint = mat.GetColor(p); break; }
                }
            }
            catch { }
            if (tex == null) tex = Texture2D.whiteTexture;
            s.TexObj = tex; s.Tex = tex.Pointer; s.Tint = new Color(tint.r, tint.g, tint.b, 1f);
        }

        // ---- raw read of CompoundStructure.meshes (MeshStructure is a non-blittable struct; interop wrappers are unsafe)
        static bool _dmTried, _dmOk;
        static int _offMeshes, _msSize, _msTransform, _msVerts, _msTris;

        static int FieldOff(IntPtr klass, string name, bool valueType)
        {
            var f = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            if (f == IntPtr.Zero) throw new Exception("field not found: " + name);
            int off = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f);
            return valueType ? off - 2 * IntPtr.Size : off;
        }

        static bool DmInit()
        {
            if (_dmTried) return _dmOk;
            _dmTried = true;
            try
            {
                var csK = Il2CppInterop.Runtime.Il2CppClassPointerStore<CompoundStructure>.NativeClassPtr;
                var msK = Il2CppInterop.Runtime.Il2CppClassPointerStore<MeshStructure>.NativeClassPtr;
                _offMeshes = FieldOff(csK, "meshes", false);
                uint align = 0; _msSize = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_value_size(msK, ref align);
                _msTransform = FieldOff(msK, "transform", true); _msVerts = FieldOff(msK, "vertices", true); _msTris = FieldOff(msK, "triangles", true);
                _dmOk = _msSize > 0 && _msSize < 1024;
                MPLog.Info($"PenCam damage-model offsets: meshes={_offMeshes} size={_msSize} tr={_msTransform} v={_msVerts} t={_msTris} ok={_dmOk}");
            }
            catch (Exception e) { MPLog.Warn("PenCam damage-model offsets: " + e.Message); }
            return _dmOk;
        }

        static unsafe List<(Transform, Vector3[], int[])> DamageModelMeshes(CompoundStructure cs)
        {
            var list = new List<(Transform, Vector3[], int[])>();
            if (cs == null || !DmInit()) return list;
            byte* obj = (byte*)cs.Pointer;
            IntPtr arr = *(IntPtr*)(obj + _offMeshes);
            if (arr == IntPtr.Zero) return list;
            long len = *(long*)((byte*)arr + 3 * IntPtr.Size);
            byte* data = (byte*)arr + 4 * IntPtr.Size;
            for (long i = 0; i < len && i < 2000; i++)
            {
                byte* e = data + i * _msSize;
                IntPtr trP = *(IntPtr*)(e + _msTransform), vP = *(IntPtr*)(e + _msVerts), tP = *(IntPtr*)(e + _msTris);
                if (vP == IntPtr.Zero || tP == IntPtr.Zero) continue;
                long vn = *(long*)((byte*)vP + 3 * IntPtr.Size), tn = *(long*)((byte*)tP + 3 * IntPtr.Size);
                if (vn <= 0 || vn > 50000 || tn < 3 || tn > 300000) continue;
                var v = new Vector3[vn]; float* vf = (float*)((byte*)vP + 4 * IntPtr.Size);
                for (long k = 0; k < vn; k++) v[k] = V3(vf[k * 3], vf[k * 3 + 1], vf[k * 3 + 2]);
                var t = new int[tn]; int* ti = (int*)((byte*)tP + 4 * IntPtr.Size);
                bool bad = false;
                for (long k = 0; k < tn; k++) { t[k] = ti[k]; if ((uint)t[k] >= (uint)vn) { bad = true; break; } }
                if (bad) continue;
                Transform tr = trP != IntPtr.Zero ? new Transform(trP) : cs.transform;
                list.Add((tr, v, t));
            }
            return list;
        }

        // ---- fallback module shapes (crew collision body, then a box)
        List<Shape> FallbackModuleShapes(Sprocket.Vehicles.VehicleComponent o, CrewSeat seat, Transform root)
        {
            var list = new List<Shape>();
            if (seat != null)
            {
                try
                {
                    var cv = seat.collisionVertices; var ct = seat.collisionTriangles;
                    if (cv != null && ct != null && cv.Length > 0 && ct.Length >= 3)
                    {
                        var v = new Vector3[cv.Length]; for (int i = 0; i < v.Length; i++) v[i] = cv[i];
                        var tr = new int[ct.Length]; for (int i = 0; i < tr.Length; i++) tr[i] = ct[i];
                        list.Add(MakeShape(v, null, tr, seat.transform, root));
                        return list;
                    }
                }
                catch { }
            }
            // the component's own model renderers (shown by the game's "O" x-ray overlay); they are not always children
            try
            {
                var groups = o.models;
                if (groups != null)
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        var g = groups[gi]; if (g == null) continue;
                        bool got = false;
                        var lods = g.lods;
                        if (lods != null && lods.Length > 0 && lods[0] != null && lods[0].renderer != null)
                        {
                            var rr = lods[0].renderer;
                            var mf = rr.GetComponent<MeshFilter>();
                            Mesh mesh = mf != null ? mf.sharedMesh : null;
                            if (mesh == null) { var smr = rr.TryCast<SkinnedMeshRenderer>(); if (smr != null) mesh = smr.sharedMesh; }
                            if (ReadMesh(mesh, out var v, out _, out var tri, out var nr)) { list.Add(MakeShape(v, nr, tri, rr.transform, root)); got = true; }
                        }
                        if (!got && g.CollisionMesh != null && ReadMesh(g.CollisionMesh, out var cv2, out _, out var ct2, out var cn2)) list.Add(MakeShape(cv2, cn2, ct2, g.transform, root));
                    }
            }
            catch (Exception e) { MPLog.Debug("PenCam models: " + e.Message); }
            if (list.Count > 0) return list;
            int rcount = 0;
            foreach (var r in o.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue; rcount++;
                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) { var smr = r.TryCast<SkinnedMeshRenderer>(); if (smr != null) mesh = smr.sharedMesh; }
                if (ReadMesh(mesh, out var v, out _, out var tri, out var nr)) list.Add(MakeShape(v, nr, tri, r.transform, root));
            }
            if (list.Count == 0)
                foreach (var mc in o.GetComponentsInChildren<MeshCollider>(true))
                    if (mc != null && ReadMesh(mc.sharedMesh, out var v, out _, out var tri, out var nr)) list.Add(MakeShape(v, nr, tri, mc.transform, root));
            if (Plugin.VerboseLogging.Value) MPLog.Info($"PenCam module '{o.gameObject.name}': {rcount} renderers, {list.Count} meshes read");
            if (list.Count > 0) return list;
            Bounds b = default; bool any = false;
            foreach (var c in o.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue; var wb = c.bounds; if (wb.size.sqrMagnitude < 1e-6f) continue;
                var lb = new Bounds(root.InverseTransformPoint(wb.center), root.InverseTransformVector(wb.size));
                lb.size = new Vector3(Mathf.Abs(lb.size.x), Mathf.Abs(lb.size.y), Mathf.Abs(lb.size.z));
                if (!any) { b = lb; any = true; } else b.Encapsulate(lb);
            }
            if (!any) b = new Bounds(root.InverseTransformPoint(o.transform.position), Vector3.one * 0.4f);
            var bv = new Vector3[8];
            for (int k = 0; k < 8; k++) bv[k] = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1));
            var bt = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            list.Add(MakeShape(bv, null, bt, root, root));
            return list;
        }

        // ---- struck plate: ray along the shell path against the armour, then the coplanar connected face around it
        class Plate { public List<(Vector3 a, Vector3 b)> Edges = new List<(Vector3, Vector3)>(); public List<Vector3> Tris = new List<Vector3>(); public float THit; public bool Final; }
        readonly List<Plate> _plates = new List<Plate>();

        void FindPlate(List<Shape> armour)
        {
            float ox = _entry.x - _dir.x * 4f, oy = _entry.y - _dir.y * 4f, oz = _entry.z - _dir.z * 4f;
            float dx = _dir.x, dy = _dir.y, dz = _dir.z;
            var hits = new List<(float t, Shape s, int tri)>();
            foreach (var s in armour)
            {
                var V = s.V; var T = s.T;
                for (int i = 0; i + 2 < T.Length; i += 3)
                {
                    var a = V[T[i]]; var b = V[T[i + 1]]; var c = V[T[i + 2]];
                    float e1x = b.x - a.x, e1y = b.y - a.y, e1z = b.z - a.z, e2x = c.x - a.x, e2y = c.y - a.y, e2z = c.z - a.z;
                    float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                    float det = e1x * px + e1y * py + e1z * pz;
                    if (Math.Abs(det) < 1e-9f) continue;
                    float inv = 1f / det;
                    float sx = ox - a.x, sy = oy - a.y, sz = oz - a.z;
                    float u = (sx * px + sy * py + sz * pz) * inv; if (u < -1e-4f || u > 1.0001f) continue;
                    float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                    float w = (dx * qx + dy * qy + dz * qz) * inv; if (w < -1e-4f || u + w > 1.0001f) continue;
                    float tt = (e2x * qx + e2y * qy + e2z * qz) * inv;
                    if (tt < 0 || tt > 12f) continue;
                    // only surfaces facing the shell (entry faces), not the back of plates
                    var n = TriNormal(V, T, i);
                    if (n.x * dx + n.y * dy + n.z * dz > 0.02f && det < 0) { }
                    hits.Add((tt, s, i));
                }
            }
            if (hits.Count == 0) { MPLog.Info("PenCam: shell path does not cross the armour mesh"); return; }
            hits.Sort((x, y) => x.t.CompareTo(y.t));
            // the armour plate the simulation reported: the last surface in a short window around the reported impact
            // (tracks, sprockets, skirts in front of it are passed through first)
            int fin = -1;
            for (int i = 0; i < hits.Count; i++) if (hits[i].t >= 4f - 0.6f) { fin = i; break; }   // first armour surface at the impact
            if (fin < 0) { float bd = float.MaxValue; for (int i = 0; i < hits.Count; i++) { float d = Math.Abs(hits[i].t - 4f); if (d < bd) { bd = d; fin = i; } } }
            float tFin = hits[fin].t;
            _entry = V3(ox + dx * tFin, oy + dy * tFin, oz + dz * tFin);
            // the plates the shell passes through: from the first one up to where the spall starts (inside the last plate)
            // how deep the shell got: where the spall starts (inside the last plate it went through), else the end of
            // the shell's own path; at most 1.5 m past the first surface
            float tSpall = float.MaxValue, tOrig = tFin;
            foreach (var f in _rec.Frags)
            {
                float ts = (f.Start.x - ox) * dx + (f.Start.y - oy) * dy + (f.Start.z - oz) * dz;
                if (!f.Original) { if (ts > tFin - 0.05f && ts < tSpall) tSpall = ts; }
                else { float te = ts + Math.Min(f.Length, 20f); if (te > tOrig) tOrig = te; }
            }
            float tLast = Math.Max(tSpall != float.MaxValue ? tSpall : tFin, tOrig);   // the deepest the shell itself got
            tLast = Math.Min(tLast, tFin + 1.5f);
            var dbg = new System.Text.StringBuilder();
            foreach (var h in hits) if (h.t > tFin - 0.5f && h.t < tFin + 2f) dbg.Append($" {h.t - tFin:F2}");
            MPLog.Info($"PenCam plates: surfaces after first:{dbg}; shell depth {tLast - tFin:F2} m (spall {(tSpall == float.MaxValue ? "none" : (tSpall - tFin).ToString("F2"))}, path {tOrig - tFin:F2})");
            var done = new HashSet<(Shape, int)>();
            float lastKept = -1f;
            for (int i = fin; i < hits.Count; i++)
            {
                var h = hits[i];
                if (h.t > tLast + 0.05f) break;
                if (lastKept >= 0 && h.t - lastKept < 0.12f) continue;      // other face of the same plate
                var p = FloodPlate(h.s, h.tri, done);
                if (p == null) continue;
                lastKept = h.t;
                p.THit = TIn + (h.t - tFin) / ShellSpeed;
                _plates.Add(p);
            }
            if (_plates.Count > 0) _plates[_plates.Count - 1].Final = true;
            _tPen = _plates.Count > 0 ? _plates[_plates.Count - 1].THit : TIn;
            _exitPt = V3(ox + dx * Math.Max(tFin, lastKept), oy + dy * Math.Max(tFin, lastKept), oz + dz * Math.Max(tFin, lastKept));
            foreach (var p in _plates) if (p.Final) { _plateEdges.AddRange(p.Edges); _plateTris.AddRange(p.Tris); }
        }

        static string PathName(Transform t, Transform root)
        {
            var sb = new System.Text.StringBuilder();
            for (; t != null && t != root; t = t.parent) sb.Insert(0, "/" + t.gameObject.name);
            return sb.ToString();
        }

        static bool IsRunningGear(string path)
        {
            var p = path.ToLowerInvariant();
            return p.Contains("track") || p.Contains("sprocket") || p.Contains("wheel") || p.Contains("idler") || p.Contains("suspension")
                || p.Contains("addon") || p.Contains("add-on") || p.Contains("applique") || p.Contains("skirt") || p.Contains("belt");
        }

        static Dictionary<(long, long), List<int>> EdgeTris(Shape s)
        {
            if (s.EdgeTris != null) return s.EdgeTris;
            var Vs = s.V; var Ts = s.T; var d = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i + 2 < Ts.Length; i += 3)
                for (int k = 0; k < 3; k++) { var key = EdgeKey(Vs[Ts[i + k]], Vs[Ts[i + (k + 1) % 3]]); if (!d.TryGetValue(key, out var l)) d[key] = l = new List<int>(); l.Add(i); }
            return s.EdgeTris = d;
        }

        Plate FloodPlate(Shape best, int bestTri, HashSet<(Shape, int)> done)
        {
            if (done.Contains((best, bestTri))) return null;
            var Vs = best.V; var Ts = best.T;
            Vector3 n0 = TriNormal(Vs, Ts, bestTri); var a0 = Vs[Ts[bestTri]];
            var edgeTris = EdgeTris(best);
            var inPlate = new HashSet<int> { bestTri }; var q = new Queue<int>(); q.Enqueue(bestTri);
            while (q.Count > 0 && inPlate.Count < 4000)
            {
                int ti = q.Dequeue();
                for (int k = 0; k < 3; k++)
                    foreach (var nb in edgeTris[EdgeKey(Vs[Ts[ti + k]], Vs[Ts[ti + (k + 1) % 3]])])
                    {
                        if (inPlate.Contains(nb)) continue;
                        var nn = TriNormal(Vs, Ts, nb);
                        if (nn.x * n0.x + nn.y * n0.y + nn.z * n0.z < 0.995f) continue;
                        var p = Vs[Ts[nb]];
                        if (Math.Abs((p.x - a0.x) * n0.x + (p.y - a0.y) * n0.y + (p.z - a0.z) * n0.z) > 0.01f) continue;
                        inPlate.Add(nb); q.Enqueue(nb);
                    }
            }
            var plate = new Plate();
            var count = new Dictionary<(long, long), int>(); var edgePos = new Dictionary<(long, long), (Vector3, Vector3)>();
            foreach (var ti in inPlate)
            {
                done.Add((best, ti));
                for (int k = 0; k < 3; k++)
                {
                    var pa = Vs[Ts[ti + k]]; var pb = Vs[Ts[ti + (k + 1) % 3]];
                    var key = EdgeKey(pa, pb); count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1; edgePos[key] = (pa, pb);
                }
                plate.Tris.Add(Vs[Ts[ti]]); plate.Tris.Add(Vs[Ts[ti + 1]]); plate.Tris.Add(Vs[Ts[ti + 2]]);
            }
            foreach (var kv in count) if (kv.Value == 1) plate.Edges.Add(edgePos[kv.Key]);
            return plate;
        }

        static Vector3 TriNormal(Vector3[] V, int[] T, int i)
        {
            var a = V[T[i]]; var b = V[T[i + 1]]; var c = V[T[i + 2]];
            float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12f;
            return V3(nx / l, ny / l, nz / l);
        }

        static long Q(Vector3 p) => ((long)Math.Round(p.x * 1000) & 0x1FFFFF) | (((long)Math.Round(p.y * 1000) & 0x1FFFFF) << 21) | (((long)Math.Round(p.z * 1000) & 0x1FFFFF) << 42);
        static (long, long) EdgeKey(Vector3 a, Vector3 b) { long qa = Q(a), qb = Q(b); return qa < qb ? (qa, qb) : (qb, qa); }

        static HashSet<IntPtr> PtrSet(List<Sprocket.IDurable> l)
        {
            var h = new HashSet<IntPtr>();
            foreach (var x in l) h.Add(((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)x).Pointer);
            return h;
        }

        static string CrewRole(CrewSeat seat)
        {
            var roles = new List<string>();
            try
            {
                var targets = seat.Operator?.operateTargets;
                if (targets != null)
                    for (int i = 0; i < targets.Length; i++)
                    {
                        string r = RoleName(targets[i]?.TaskIdentifier ?? "");
                        if (r != null && !roles.Contains(r)) roles.Add(r);
                    }
            }
            catch { }
            return roles.Count == 0 ? "Crew member" : string.Join(" / ", roles);
        }

        static string RoleName(string task)
        {
            var t = task.ToLowerInvariant();
            if (t.Contains("driv")) return "Driver";
            if (t.Contains("load")) return "Loader";
            if (t.Contains("command")) return "Commander";
            if (t.Contains("gun") || t.Contains("lay") || t.Contains("aim")) return "Gunner";
            if (t.Contains("radio")) return "Radio operator";
            return t.Length > 0 ? char.ToUpperInvariant(task[0]) + task.Substring(1) : null;
        }

        static bool IsEffect(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
            {
                var n = t.gameObject.name.ToLowerInvariant();
                if (n.Contains("effect") || n.Contains("smoke") || n.Contains("fx") || n.Contains("particle") || n.Contains("flame") || n.Contains("fire")
                    || n.Contains("exhaust") || n.Contains("dust") || n.Contains("muzzle") || n.Contains("blast") || n.Contains("decal") || n.Contains("shadow")) return true;
            }
            return false;
        }

        static Color DamageCol(float frac) => frac <= 0.01f ? new Color(0.35f, 0.02f, 0.02f, 1f) : Color.Lerp(new Color(1f, 0.12f, 0.08f, 1f), new Color(1f, 0.5f, 0.1f, 1f), frac);   // any damage reads orange..red (ammo racks are yellow when intact)

        // ================================================================== camera
        Vector3 _camPos; Quaternion _camRot; float _fov, _xray;
        float _r00, _r01, _r02, _r10, _r11, _r12, _r20, _r21, _r22, _cx, _cy, _cz, _f;

        void CameraAt(float t)
        {
            float s = Mathf.Clamp01(t / TIn);
            Vector3 camAnchor = _entry - _dir * (Approach * (1f - 0.5f * s));
            Vector3 chasePos = camAnchor - _dir * (1.2f + _radius * 0.3f) + _up * (0.3f + _radius * 0.1f);
            Quaternion chaseRot = Quaternion.LookRotation(_entry - chasePos, _up);
            float orbit = Mathf.Max(0, t - TIn) * 6f;
            // swing to the side of the tank that was hit (left or right of the shell path)
            float sgn = Vector3.Dot(_entry - _focus, _side) >= 0 ? 1f : -1f;
            Vector3 sideDir = Quaternion.AngleAxis(sgn * (-30f + orbit), _up) * (_side * sgn);
            const float sideFov = 38f;
            float dist = _radius / Mathf.Sin(sideFov * 0.5f * Mathf.Deg2Rad) * 0.95f / 1.5f;
            Vector3 sidePos = _focus + (sideDir + _up * 0.45f).normalized * dist;
            Quaternion sideRot = Quaternion.LookRotation(_focus - sidePos, _up);
            float k = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(TIn, TIn + TSwing, t));
            _camPos = Vector3.Lerp(chasePos, sidePos, k);
            _camRot = Quaternion.Slerp(chaseRot, sideRot, k);
            _fov = Mathf.Lerp(50f, sideFov, k);
            _xray = k;

            var right = _camRot * Vector3.right; var up = _camRot * Vector3.up; var fwd = _camRot * Vector3.forward;
            _r00 = right.x; _r01 = right.y; _r02 = right.z; _r10 = up.x; _r11 = up.y; _r12 = up.z; _r20 = fwd.x; _r21 = fwd.y; _r22 = fwd.z;
            _cx = _camPos.x; _cy = _camPos.y; _cz = _camPos.z;
            _f = (DH * 0.5f) / (float)Math.Tan(_fov * 0.5f * Math.PI / 180.0);
        }

        bool Proj(Vector3 p, out Vector2 o)
        {
            float dx = p.x - _cx, dy = p.y - _cy, dz = p.z - _cz;
            float z = _r20 * dx + _r21 * dy + _r22 * dz;
            if (z < 0.05f) { o = default; return false; }
            float x = _r00 * dx + _r01 * dy + _r02 * dz, y = _r10 * dx + _r11 * dy + _r12 * dz;
            o = V2(x / z * _f, y / z * _f - DH * 0.5f);
            return true;
        }

        void ProjectShape(Shape s)
        {
            var V = s.V; var Sx = s.Sx; var Sy = s.Sy; var Ok = s.Ok;
            for (int i = 0; i < V.Length; i++)
            {
                float dx = V[i].x - _cx, dy = V[i].y - _cy, dz = V[i].z - _cz;
                float z = _r20 * dx + _r21 * dy + _r22 * dz;
                if (z < 0.05f) { Ok[i] = false; continue; }
                Ok[i] = true;
                Sx[i] = (_r00 * dx + _r01 * dy + _r02 * dz) / z * _f;
                Sy[i] = (_r10 * dx + _r11 * dy + _r12 * dz) / z * _f - DH * 0.5f;
            }
        }

        // ================================================================== render
        struct TriRef { public Shape S; public int I; }
        readonly List<TriRef> _tris = new List<TriRef>(); float[] _keys = new float[0]; TriRef[] _items = new TriRef[0];
        readonly List<(float, int)> _order = new List<(float, int)>();

        void Render(float t)
        {
            CameraAt(t);
            Begin(_texLayer); Begin(_fxLayer);

            float solid = 1f - Mathf.Clamp01(_xray / 0.6f);              // opaque textured tank fades out during the swing
            float xa = Mathf.Clamp01((_xray - 0.15f) / 0.6f);            // x-ray fades in (overlapping, no gap)

            foreach (var s in _hull) ProjectShape(s);

            if (solid > 0.01f) RenderGpu();
            _raw.color = new Color(1, 1, 1, _rt != null ? solid : 0f);
            if (xa > 0.01f) foreach (var s in _hull) DrawGlow(s, new Color(0.55f, 0.75f, 0.95f, 1f), 0.015f * xa, 0.28f * xa);

            float h = Mathf.Clamp01((t - TIn - 0.5f) / 1.5f);
            if (xa > 0.01f)
            {
                _order.Clear();
                for (int i = 0; i < _modules.Count; i++) { var s = _modules[i]; _order.Add((_r20 * (s.Cx - _cx) + _r21 * (s.Cy - _cy) + _r22 * (s.Cz - _cz), i)); }
                _order.Sort((a, b) => b.Item1.CompareTo(a.Item1));
                foreach (var (_, i) in _order)
                {
                    var s = _modules[i];
                    if (s.Crew && _deadCrew.Contains(s.Mod)) continue;
                    _mods.TryGetValue(s.Mod, out var m);
                    var col = s.Col;
                    if (m != null && m.Prior) col = DamageCol(m.After / m.Max);
                    else if (m != null && m.Hit)
                    {
                        col = Color.Lerp(col, DamageCol(m.After / m.Max), h);
                        if (h > 0 && h < 1) col = Color.Lerp(col, Color.white, (0.5f + 0.5f * Mathf.Sin(t * 25f)) * 0.4f);
                    }
                    ProjectShape(s);
                    DrawGlow(s, col, 0.5f * xa, 0.9f * xa);
                }
            }

            // struck plate: bright outline + faint fill, from the moment of impact
            // every part the shell passes through lights up as the shell reaches it; earlier ones fade, the final plate stays
            foreach (var pl in _plates)
            {
                if (t < pl.THit - 0.02f) continue;
                float pulse = pl.Final ? 0.75f + 0.25f * Mathf.Sin(t * 7f) : Mathf.Clamp01(1f - (t - pl.THit) / 0.8f);
                if (pulse <= 0.01f) continue;
                var hc = HighlightColour();
                var edge = new Color(hc.r, hc.g, hc.b, pulse * (pl.Final ? 1f : 0.8f));
                for (int i = 0; i + 2 < pl.Tris.Count; i += 3)
                    if (Proj(pl.Tris[i], out var a) && Proj(pl.Tris[i + 1], out var b) && Proj(pl.Tris[i + 2], out var c))
                        Tri(_fxLayer, a, b, c, new Color(hc.r, hc.g, hc.b, 0.15f * pulse));
                foreach (var (a, b) in pl.Edges) Seg3(a, b, pl.Final ? 3f : 2f, edge);
            }

            // shell, impact flash, spall
            float travelled = Math.Min(t, _tPen) * ShellSpeed;      // constant speed, through every plate up to the last
            Vector3 head = _entry - _dir * Approach + _dir * travelled;
            Vector3 tail = head - _dir * Math.Min(0.5f, travelled + 0.01f);
            if (t < _tPen + 0.1f) Seg3(tail, head, 4f, new Color(0.35f, 1f, 0.35f, 1f));
            if (t >= TIn && t < _tPen + 0.6f && Proj(t < _tPen ? head : _exitPt, out var ep))
            {
                float a = 1f - Math.Max(0f, t - _tPen) / 0.6f, r = 6f + (1f - a) * 22f;
                Quad(_fxLayer, ep + V2(-r, -r), ep + V2(r, -r), ep + V2(r, r), ep + V2(-r, r), new Color(1f, 0.8f, 0.3f, a * 0.9f));
            }
            // (hot loop: plain arithmetic only - Unity struct operators/colour conversions are native calls under IL2CPP)
            Color32 white = default; white.r = white.g = white.b = white.a = 255;
            for (int fi2 = 0; fi2 < _frags.Count; fi2++)
            {
                var f = _frags[fi2];
                float u = (t - f.T0) / (f.T1 - f.T0);
                if (u <= 0f) continue;
                if (u > 1f) u = 1f;
                var tip = V3(f.A.x + (f.B.x - f.A.x) * u, f.A.y + (f.B.y - f.A.y) * u, f.A.z + (f.B.z - f.A.z) * u);
                Seg3(f.A, tip, f.W, f.C32);
                if (u < 1f && Proj(tip, out var hp)) QuadC(_fxLayer, hp.x - 2f, hp.y - 2f, hp.x + 2f, hp.y - 2f, hp.x + 2f, hp.y + 2f, hp.x - 2f, hp.y + 2f, white);
            }

            // explosive filler: expanding fireball ring at the burst point
            if (_rec.Blast)
            {
                float u = (t - (_tPen + 0.25f)) / 0.9f;
                var col = _rec.ShellKind == 2 ? new Color(1f, 0.5f, 0.1f, 0.85f * (1f - u)) : new Color(1f, 0.3f, 0.9f, 0.85f * (1f - u));
                DrawBlast(_rec.BlastPoint, _rec.BlastRadius, u, col);
            }
            // destroyed ammo racks: big red explosion once their damage flashing ends
            if (_ammoBlasts.Count > 0 && t >= TAmmo)
            {
                float u = (t - TAmmo) / (1.2f / 1.4f);
                float r = Mathf.Max(0.5f, Plugin.AmmoBlastRadius.Value);
                foreach (var p in _ammoBlasts) DrawBlast(p, r * 1.5f, u, new Color(1f, 0.1f, 0.05f, 1f - 0.6f * u), 0.9f, false);
            }

            if (t >= TIn)
            {
                // re-title if the kill message arrives after the reveal
                int kill = (_rec.Result == 0 || _rec.Result == 4) ? (Runner.Battle?.KillNear(_rec.Victim, _rec.ReceivedAt) ?? -1) : -1;
                if (!_titled || kill != _titledKill)
                {
                    if (!_titled) _baseTitle = _title.text;
                    _titled = true; _titledKill = kill;
                    string col = kill >= 0 ? "#ff3020" : _rec.Result == 0 ? "#ff5040" : _rec.Result == 2 ? "#a0c8ff" : "#c8c8c8";
                    _title.text = $"<color={col}>{TitleText(kill)}</color>  " + _baseTitle;
                }
            }
            if (t > TIn + TSwing && _list.text.Length == 0) _list.text = _listText;
            _panel.GetComponent<CanvasGroup>().alpha = Mathf.Clamp01((Total - t) / 0.5f) * Mathf.Clamp01(t / 0.2f);

            End(_texLayer); End(_fxLayer);
        }

        /// <summary>Opaque tank: parts drawn far -> near (no per-triangle sort), back faces culled, flat shaded,
        /// with dark outlines on creases and silhouettes so every part reads clearly.</summary>
        /// <summary>Percent text; one decimal when whole numbers would hide the change (e.g. 100% -> 99.6%).</summary>
        static string Pct(float v, float other)
        {
            int a = Mathf.RoundToInt(v * 100), b = Mathf.RoundToInt(other * 100);
            return a != b || Math.Abs(v - other) < 0.0005f ? a.ToString() : (Mathf.Floor(v * 1000f) / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        void DrawBlast(Vector3 at, float radius, float u, Color col, float fillAlpha = 0.55f, bool core = true)
        {
            if (!(u > 0f && u < 1f) || !Proj(at, out var bp)) return;
            float dist = (float)Math.Sqrt((at.x - _cx) * (at.x - _cx) + (at.y - _cy) * (at.y - _cy) + (at.z - _cz) * (at.z - _cz));
            float rpx = radius * u / Math.Max(0.1f, dist) * _f;
            const int seg = 28;
            var fill = new Color(col.r, col.g, col.b, col.a * fillAlpha);
            for (int k = 0; k < seg; k++)
            {
                float a0 = k * Mathf.PI * 2f / seg, a1 = (k + 1) * Mathf.PI * 2f / seg;
                var p0 = V2(bp.x + Mathf.Cos(a0) * rpx, bp.y + Mathf.Sin(a0) * rpx); var p1 = V2(bp.x + Mathf.Cos(a1) * rpx, bp.y + Mathf.Sin(a1) * rpx);
                Tri(_fxLayer, bp, p0, p1, fill);
                Line(p0, p1, fillAlpha > 0.6f ? 4f : 2.5f, col);
            }
            if (!core) return;
            float cr = rpx * 0.35f * (1f - u);
            Quad(_fxLayer, bp + V2(-cr, -cr), bp + V2(cr, -cr), bp + V2(cr, cr), bp + V2(-cr, cr), new Color(1f, 0.9f, 0.5f, 0.6f * (1f - u)));
        }

        string TitleText(int kill)
        {
            string shell = _rec.ShellKind == 1 ? "APHE " : _rec.ShellKind == 2 ? "HE " : _rec.ShellKind == 3 ? "APDS " : "";
            if (kill == 1) return shell + "AMMO RACK";
            if (kill == 0) return shell + "CREW KNOCKED OUT";
            if (_rec.Result != 0) return shell + PenRecord.ResultText(_rec.Result);
            foreach (var d in _rec.Damage) if (d.After <= 0.01f * Math.Max(d.Max, 0.001f)) return shell + "CRITICAL HIT";
            return shell + "PENETRATION HIT";
        }

        void DrawSolid(float alpha)
        {
            _order.Clear();
            for (int i = 0; i < _hull.Count; i++) { var s = _hull[i]; _order.Add((_r20 * (s.Cx - _cx) + _r21 * (s.Cy - _cy) + _r22 * (s.Cz - _cz), i)); }
            _order.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            byte A = (byte)(alpha * 255f);
            foreach (var (_, si) in _order)
            {
                var s = _hull[si];
                var V = s.V; var T = s.T; var Ok = s.Ok;
                int nt = T.Length / 3;
                if (s.Front == null || s.Front.Length < nt) s.Front = new bool[nt];
                var ch = Reserve(_fxLayer, Math.Min(ChunkVerts, nt * 3), IntPtr.Zero, null);
                for (int k = 0; k < nt; k++)
                {
                    int i = k * 3, a = T[i], b = T[i + 1], c = T[i + 2];
                    s.Front[k] = false;
                    if (!Ok[a] || !Ok[b] || !Ok[c]) continue;
                    float ux = V[b].x - V[a].x, uy = V[b].y - V[a].y, uz = V[b].z - V[a].z;
                    float vx = V[c].x - V[a].x, vy = V[c].y - V[a].y, vz = V[c].z - V[a].z;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    if (nx * (_cx - V[a].x) + ny * (_cy - V[a].y) + nz * (_cz - V[a].z) <= 0) continue;
                    s.Front[k] = true;
                    float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-9f; nx /= nl; ny /= nl; nz /= nl;
                    float key = Math.Max(0f, nx * -0.35f + ny * 0.8f + nz * 0.48f);
                    float head = Math.Max(0f, -(nx * _r20 + ny * _r21 + nz * _r22));
                    float shade = 0.45f + 0.3f * key + 0.25f * head; if (shade > 1f) shade = 1f;
                    if (ch.Vn + 3 > ChunkVerts) ch = Reserve(_fxLayer, 3, IntPtr.Zero, null);
                    int bi = ch.Vn; byte g = (byte)(shade * 235f);
                    Color32 c32 = default; c32.r = g; c32.g = g; c32.b = g; c32.a = A;
                    ch.V[bi] = Clip(s.Sx[a], s.Sy[a]); ch.V[bi + 1] = Clip(s.Sx[b], s.Sy[b]); ch.V[bi + 2] = Clip(s.Sx[c], s.Sy[c]);
                    ch.C[bi] = c32; ch.C[bi + 1] = c32; ch.C[bi + 2] = c32; ch.Vn += 3;
                    ch.T.Add(bi); ch.T.Add(bi + 1); ch.T.Add(bi + 2);
                }
                // outlines: crease edges with a visible face, silhouette edges (one face front, one back)
                if (s.Edges == null) BuildEdges(s);
                var ol = new Color(0.05f, 0.05f, 0.06f, alpha);
                foreach (var e in s.Edges)
                {
                    bool f1 = s.Front[e.F1], f2 = e.F2 >= 0 && s.Front[e.F2];
                    if (!f1 && !f2) continue;
                    if (!(e.Crease || e.F2 < 0 || f1 != f2)) continue;
                    if (!Ok[e.A] || !Ok[e.B]) continue;
                    Line(V2(s.Sx[e.A], s.Sy[e.A]), V2(s.Sx[e.B], s.Sy[e.B]), 1.3f, ol);
                }
            }
        }

        // ================================================================== GPU chase view
        // The exterior is baked ONCE into a vehicle-space mesh with flat shading in its vertex colours (fixed key light),
        // plus a line mesh of the hard edges. Each frame a CommandBuffer draws both into a RenderTexture with the built-in
        // unlit "Hidden/Internal-Colored" shader and a real depth buffer: no scene, no lighting pipeline, no CPU sorting.
        RawImage _raw; RenderTexture _rt; Mesh _gpuMesh, _gpuLines;
        static Material _matSolid, _matLine;
        UnityEngine.Rendering.CommandBuffer _cb;

        static Material ColoredMat(bool lines)
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return null;
            var m = new Material(sh); m.hideFlags = HideFlags.HideAndDontSave;
            m.SetInt("_SrcBlend", 1); m.SetInt("_DstBlend", 0);
            m.SetInt("_Cull", 0); m.SetInt("_ZWrite", 1); m.SetInt("_ZTest", 4);
            if (lines) m.SetFloat("_ZBias", -2f);
            return m;
        }

        static void BuildGpu(Geo geo)
        {
            if (geo.Hull.Count == 0) return;
            if (_matSolid == null) _matSolid = ColoredMat(false);
            if (_matLine == null) _matLine = ColoredMat(true);
            if (_matSolid == null) { MPLog.Warn("PenCam: built-in colour shader missing"); return; }
            var vs = new List<Vector3>(); var cs = new List<Color32>(); var ls = new List<Vector3>(); var lc = new List<Color32>();
            foreach (var s in geo.Hull)
            {
                var V = s.V; var T = s.T;
                for (int i = 0; i + 2 < T.Length; i += 3)
                {
                    var n = TriNormal(V, T, i);
                    float key = Math.Abs(n.x * -0.35f + n.y * 0.8f + n.z * 0.48f), fill = Math.Abs(n.x * 0.6f - n.z * 0.3f);
                    float shade = 0.35f + 0.5f * key + 0.15f * fill; if (shade > 1f) shade = 1f;
                    byte g = (byte)(shade * 240f); Color32 c = default; c.r = g; c.g = g; c.b = (byte)Math.Min(255, g + 8); c.a = 255;
                    vs.Add(V[T[i]]); vs.Add(V[T[i + 1]]); vs.Add(V[T[i + 2]]); cs.Add(c); cs.Add(c); cs.Add(c);
                }
                if (s.Edges == null) BuildEdges(s);
                Color32 dark = default; dark.r = 20; dark.g = 22; dark.b = 26; dark.a = 255;
                foreach (var e in s.Edges) if (e.Crease || e.F2 < 0) { ls.Add(V[e.A]); ls.Add(V[e.B]); lc.Add(dark); lc.Add(dark); }
            }
            var m = new Mesh(); m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = vs.ToArray(); m.colors32 = cs.ToArray();
            var idx = new int[vs.Count]; for (int i = 0; i < idx.Length; i++) idx[i] = i;
            m.SetIndices(idx, MeshTopology.Triangles, 0);
            m.bounds = new Bounds(geo.Focus, Vector3.one * 50f);
            UnityEngine.Object.DontDestroyOnLoad(m);
            geo.Gpu = m;
            if (_matLine != null && ls.Count > 0)
            {
                var l = new Mesh(); l.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                l.vertices = ls.ToArray(); l.colors32 = lc.ToArray();
                var li = new int[ls.Count]; for (int i = 0; i < li.Length; i++) li[i] = i;
                l.SetIndices(li, MeshTopology.Lines, 0);
                l.bounds = m.bounds;
                geo.Lines = l;
            }
            MPLog.Info($"PenCam GPU view: {vs.Count / 3} triangles, {ls.Count / 2} edges");
        }

        void RenderGpu()
        {
            if (_rt == null || _geo?.Gpu == null) return;
            // Unity camera convention: view space looks down -Z
            var view = Matrix4x4.TRS(_camPos, _camRot, new Vector3(1, 1, -1)).inverse;
            var proj = Matrix4x4.Perspective(_fov, DW / DH, 0.05f, 200f);
            _cb.Clear();
            _cb.SetRenderTarget(_rt);
            _cb.ClearRenderTarget(true, true, new Color(0.10f, 0.12f, 0.15f, 1f));
            _cb.SetViewProjectionMatrices(view, proj);
            _cb.DrawMesh(_geo.Gpu, Matrix4x4.identity, _matSolid, 0, 0);
            if (_geo.Lines != null) _cb.DrawMesh(_geo.Lines, Matrix4x4.identity, _matLine, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        struct Edge { public int A, B, F1, F2; public bool Crease; }

        static void BuildEdges(Shape s)
        {
            var V = s.V; var T = s.T; int nt = T.Length / 3;
            var fn = new Vector3[nt];
            for (int k = 0; k < nt; k++) fn[k] = TriNormal(V, T, k * 3);
            var map = new Dictionary<(long, long), int>(); var list = new List<Edge>();
            for (int k = 0; k < nt; k++)
                for (int j = 0; j < 3; j++)
                {
                    int a = T[k * 3 + j], b = T[k * 3 + (j + 1) % 3];
                    var key = EdgeKey(V[a], V[b]);
                    if (map.TryGetValue(key, out var ei))
                    {
                        var e = list[ei];
                        if (e.F2 < 0) { e.F2 = k; var n1 = fn[e.F1]; var n2 = fn[k]; e.Crease = n1.x * n2.x + n1.y * n2.y + n1.z * n2.z < 0.82f; list[ei] = e; }
                    }
                    else { map[key] = list.Count; list.Add(new Edge { A = a, B = b, F1 = k, F2 = -1 }); }
                }
            s.Edges = list.ToArray();
        }

        /// <summary>Fresnel-style glow: faces seen edge-on are bright (silhouette), faces seen head-on faint.</summary>
        void DrawGlow(Shape s, Color col, float aFace, float aEdge)
        {
            int n = s.V.Length; if (n > ChunkVerts) return;
            var ch = Reserve(_fxLayer, n, IntPtr.Zero, null);
            int baseV = ch.Vn;
            byte r = (byte)(Mathf.Clamp01(col.r) * 255f), g = (byte)(Mathf.Clamp01(col.g) * 255f), b = (byte)(Mathf.Clamp01(col.b) * 255f);
            var V = s.V; var N = s.N;
            for (int i = 0; i < n; i++)
            {
                ch.V[baseV + i] = s.Ok[i] ? Clip(s.Sx[i], s.Sy[i]) : default;
                float dx = V[i].x - _cx, dy = V[i].y - _cy, dz = V[i].z - _cz;
                float l = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) + 1e-6f;
                float facing = Math.Abs((N[i].x * dx + N[i].y * dy + N[i].z * dz) / l);
                float rim = 1f - facing; rim *= rim;
                Color32 c = default; c.r = r; c.g = g; c.b = b; c.a = (byte)(Math.Min(1f, aFace + (aEdge - aFace) * rim) * 255f);
                ch.C[baseV + i] = c;
            }
            ch.Vn += n;
            var T = s.T; var Ok = s.Ok;
            for (int i = 0; i + 2 < T.Length; i += 3)
            {
                int a = T[i], bb = T[i + 1], c = T[i + 2];
                if (!Ok[a] || !Ok[bb] || !Ok[c]) continue;
                ch.T.Add(baseV + a); ch.T.Add(baseV + bb); ch.T.Add(baseV + c);
            }
        }

        // ------------------------------------------------------------------ chunked uGUI mesh output
        Layer NewLayer(string name)
        {
            var go = new GameObject(name); go.transform.SetParent(_panel.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, -36); rt.sizeDelta = new Vector2(DW, DH);
            return new Layer { Parent = go.transform };
        }

        void Begin(Layer l) { foreach (var c in l.Chunks) { c.Vn = 0; c.T.Clear(); } }

        Chunk Reserve(Layer l, int verts, IntPtr tex, Texture texObj)
        {
            foreach (var c in l.Chunks) if (c.Tex == tex && c.Vn + verts <= ChunkVerts) return c;
            var go = new GameObject("c" + l.Chunks.Count); go.transform.SetParent(l.Parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1); rt.sizeDelta = new Vector2(DW, DH);
            var cr = go.AddComponent<CanvasRenderer>();
            cr.materialCount = 1; cr.SetMaterial(Canvas.GetDefaultCanvasMaterial(), 0);
            cr.SetTexture(texObj != null ? texObj : Texture2D.whiteTexture);
            var ch = new Chunk { CR = cr, M = new Mesh(), Tex = tex };
            l.Chunks.Add(ch);
            return ch;
        }

        void End(Layer l)
        {
            foreach (var ch in l.Chunks)
            {
                ch.M.Clear();
                if (ch.Vn > 0)
                {
                    var v = new Vector3[ch.Vn]; Array.Copy(ch.V, v, ch.Vn);
                    var c = new Color32[ch.Vn]; Array.Copy(ch.C, c, ch.Vn);
                    var uv = new Vector2[ch.Vn]; Array.Copy(ch.UV, uv, ch.Vn);
                    ch.M.vertices = v; ch.M.colors32 = c; ch.M.uv = uv;
                    ch.M.triangles = ch.T.ToArray();
                }
                // clip to the view on the GPU (canvas-space rect), instead of clamping vertices (which distorted shapes)
                try
                {
                    var t = ch.CR.transform;
                    var p0 = _canvas.InverseTransformPoint(t.TransformPoint(V3(-DW * 0.5f, -DH, 0f)));
                    var p1 = _canvas.InverseTransformPoint(t.TransformPoint(V3(DW * 0.5f, 0f, 0f)));
                    ch.CR.EnableRectClipping(Rect.MinMaxRect(Math.Min(p0.x, p1.x), Math.Min(p0.y, p1.y), Math.Max(p0.x, p1.x), Math.Max(p0.y, p1.y)));
                }
                catch { }
                ch.CR.SetMesh(ch.M);
            }
        }

        static Vector3 Clip(float x, float y) => V3(Math.Clamp(x, -20000f, 20000f), Math.Clamp(y, -20000f, 20000f), 0f);   // real clipping is done by the CanvasRenderer rect

        static string _hcSrc; static Color _hc = Color.white;
        static Color HighlightColour()
        {
            var s = Plugin.PenCamPlateColour.Value;
            if (s != _hcSrc) { _hcSrc = s; if (!ColorUtility.TryParseHtmlString(s, out _hc)) _hc = Color.white; }
            return _hc;
        }

        void Tri(Layer l, Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            var ch = Reserve(l, 3, IntPtr.Zero, null);
            int i = ch.Vn; Color32 c32 = To32(col);
            ch.V[i] = Clip(a.x, a.y); ch.V[i + 1] = Clip(b.x, b.y); ch.V[i + 2] = Clip(c.x, c.y);
            ch.C[i] = c32; ch.C[i + 1] = c32; ch.C[i + 2] = c32; ch.Vn += 3;
            ch.T.Add(i); ch.T.Add(i + 1); ch.T.Add(i + 2);
        }

        static Color32 To32(Color c)
        {
            Color32 o = default;
            o.r = (byte)(c.r <= 0f ? 0 : c.r >= 1f ? 255 : c.r * 255f); o.g = (byte)(c.g <= 0f ? 0 : c.g >= 1f ? 255 : c.g * 255f);
            o.b = (byte)(c.b <= 0f ? 0 : c.b >= 1f ? 255 : c.b * 255f); o.a = (byte)(c.a <= 0f ? 0 : c.a >= 1f ? 255 : c.a * 255f);
            return o;
        }

        void QuadC(Layer l, float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy, Color32 c32)
        {
            var ch = Reserve(l, 4, IntPtr.Zero, null);
            int i = ch.Vn;
            ch.V[i] = Clip(ax, ay); ch.V[i + 1] = Clip(bx, by); ch.V[i + 2] = Clip(cx, cy); ch.V[i + 3] = Clip(dx, dy);
            ch.C[i] = c32; ch.C[i + 1] = c32; ch.C[i + 2] = c32; ch.C[i + 3] = c32; ch.Vn += 4;
            ch.T.Add(i); ch.T.Add(i + 1); ch.T.Add(i + 2); ch.T.Add(i); ch.T.Add(i + 2); ch.T.Add(i + 3);
        }

        void Quad(Layer l, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
        {
            var ch = Reserve(l, 4, IntPtr.Zero, null);
            int i = ch.Vn; Color32 c32 = To32(col);
            ch.V[i] = Clip(a.x, a.y); ch.V[i + 1] = Clip(b.x, b.y); ch.V[i + 2] = Clip(c.x, c.y); ch.V[i + 3] = Clip(d.x, d.y);
            ch.C[i] = c32; ch.C[i + 1] = c32; ch.C[i + 2] = c32; ch.C[i + 3] = c32; ch.Vn += 4;
            ch.T.Add(i); ch.T.Add(i + 1); ch.T.Add(i + 2); ch.T.Add(i); ch.T.Add(i + 2); ch.T.Add(i + 3);
        }

        void Line(Vector2 a, Vector2 b, float w, Color col) => LineC(a, b, w, To32(col));

        void LineC(Vector2 a, Vector2 b, float w, Color32 col)
        {
            float dx = b.x - a.x, dy = b.y - a.y, l = (float)Math.Sqrt(dx * dx + dy * dy); if (l < 0.1f) return;
            float nx = -dy / l * w * 0.5f, ny = dx / l * w * 0.5f;
            QuadC(_fxLayer, a.x - nx, a.y - ny, b.x - nx, b.y - ny, b.x + nx, b.y + ny, a.x + nx, a.y + ny, col);
        }

        void Seg3(Vector3 a, Vector3 b, float w, Color col) => Seg3(a, b, w, To32(col));

        void Seg3(Vector3 a, Vector3 b, float w, Color32 col)
        {
            float za = _r20 * (a.x - _cx) + _r21 * (a.y - _cy) + _r22 * (a.z - _cz);
            float zb = _r20 * (b.x - _cx) + _r21 * (b.y - _cy) + _r22 * (b.z - _cz);
            if (za < 0.05f && zb < 0.05f) return;
            if (za < 0.05f) { float k = (0.05f - za) / (zb - za); a = V3(a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k, a.z + (b.z - a.z) * k); }
            else if (zb < 0.05f) { float k = (0.05f - zb) / (za - zb); b = V3(b.x + (a.x - b.x) * k, b.y + (a.y - b.y) * k, b.z + (a.z - b.z) * k); }
            if (Proj(a, out var pa) && Proj(b, out var pb)) LineC(pa, pb, w, col);
        }

        TextMeshProUGUI Text(Transform parent, Vector2 pos, Vector2 size, float fs, TextAlignmentOptions al)
        {
            var go = new GameObject("t"); go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = _font; t.fontSize = fs; t.alignment = al; t.color = Color.white; t.raycastTarget = false; t.richText = true;
            t.enableWordWrapping = true;
            return t;
        }
    }
}

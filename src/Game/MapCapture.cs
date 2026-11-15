using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Top-down overview of each map, for placing team spawns in the lobby. Built once per map the first time you are
    /// in a battle on it (from the terrain heights: shaded relief, coloured by height) and kept in
    /// BepInEx/config/SprocketLAN_maps as a small raw image + its world bounds.
    /// </summary>
    public static class MapCapture
    {
        public const int Res = 512;
        public class MapImage { public Texture2D Tex; public float MinX, MinZ, Size; public Vector3? A, B; }

        static string Dir => Path.Combine(BepInEx.Paths.ConfigPath, "SprocketLAN_maps");
        static string FileOf(string map) => Path.Combine(Dir, DesignShare.SafeName(map) + ".map");

        static readonly Dictionary<string, MapImage> _cache = new Dictionary<string, MapImage>();
        static readonly Dictionary<string, float> _missingUntil = new Dictionary<string, float>();

        /// <summary>The overview of this map, or null if it hasn't been built yet.</summary>
        public static MapImage Get(string map)
        {
            if (string.IsNullOrEmpty(map)) return null;
            if (_cache.TryGetValue(map, out var m) && m.Tex != null && !m.Tex.WasCollected) return m;
            if (_missingUntil.TryGetValue(map, out var t) && Time.realtimeSinceStartup < t) return null;
            try
            {
                var f = FileOf(map);
                if (!File.Exists(f)) { _missingUntil[map] = Time.realtimeSinceStartup + 3f; return null; }
                using var r = new BinaryReader(File.OpenRead(f));
                if (r.ReadInt32() != 0x50414D53) return null;   // "SMAP"
                int res = r.ReadInt32();
                var img = new MapImage { MinX = r.ReadSingle(), MinZ = r.ReadSingle(), Size = r.ReadSingle() };
                if (r.ReadBoolean()) img.A = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (r.ReadBoolean()) img.B = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rgb = r.ReadBytes(res * res * 3);
                var px = new Color32[res * res];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255);
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                tex.SetPixels32(px); tex.Apply(false);
                img.Tex = tex; _cache[map] = img;
                return img;
            }
            catch (Exception e) { MPLog.Warn($"map overview {map}: {e.Message}"); _missingUntil[map] = Time.realtimeSinceStartup + 30f; return null; }
        }

        // ---- building one (spread over frames)
        static string _building; static float[] _h; static int _row; static float _minX, _minZ, _size; static int _epoch = -1;

        public static void Tick(BattleManager bm)
        {
            if (!bm.InBattle) { _building = null; return; }
            if (_building == null)
            {
                if (_epoch == bm.Epoch || Time.time - bm.EpochStartedAt < 3f) return;
                _epoch = bm.Epoch;
                string map = Lobby.CurrentMap();
                if (string.IsNullOrEmpty(map)) return;
                if (File.Exists(FileOf(map))) return;
                if (!Bounds(out _minX, out _minZ, out _size)) return;
                _building = map; _h = new float[Res * Res]; _row = 0;
                MPLog.Info($"Map overview: building '{map}' ({_size:F0} m square from {_minX:F0},{_minZ:F0})");
                return;
            }
            for (int n = 0; n < 16 && _row < Res; n++, _row++)
                for (int i = 0; i < Res; i++)
                {
                    var p = new Vector3(_minX + (i + 0.5f) * _size / Res, 0, _minZ + (_row + 0.5f) * _size / Res);
                    _h[_row * Res + i] = Terrain.GroundHeight(p, out var y) ? y : float.NaN;
                }
            if (_row < Res) return;
            try { Save(_building); } catch (Exception e) { MPLog.Warn("map overview save: " + e.Message); }
            _building = null; _h = null;
        }

        static bool Bounds(out float minX, out float minZ, out float size)
        {
            minX = minZ = float.MaxValue; size = 0; float maxX = float.MinValue, maxZ = float.MinValue;
            try
            {
                var ts = UnityEngine.Terrain.activeTerrains;
                if (ts == null || ts.Length == 0) return false;
                foreach (var t in ts)
                {
                    if (t == null) continue;
                    var o = t.GetPosition(); var s = t.terrainData.size;
                    minX = Mathf.Min(minX, o.x); minZ = Mathf.Min(minZ, o.z); maxX = Mathf.Max(maxX, o.x + s.x); maxZ = Mathf.Max(maxZ, o.z + s.z);
                }
            }
            catch (Exception e) { MPLog.Warn("map bounds: " + e.Message); return false; }
            if (maxX <= minX) return false;
            size = Mathf.Max(maxX - minX, maxZ - minZ);
            return size > 10f;
        }

        static void Save(string map)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in _h) if (!float.IsNaN(v)) { lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
            if (hi < lo) { MPLog.Warn("map overview: no terrain heights"); return; }
            float cell = _size / Res;
            var rgb = new byte[Res * Res * 3];
            var light = new Vector3(-1f, 1.4f, 1f).normalized;   // from the north-west
            for (int j = 0; j < Res; j++)
                for (int i = 0; i < Res; i++)
                {
                    int k = j * Res + i; float h = _h[k];
                    Color c;
                    if (float.IsNaN(h)) c = new Color(0.12f, 0.14f, 0.18f);
                    else
                    {
                        float hx = H(i + 1, j, h) - H(i - 1, j, h), hz = H(i, j + 1, h) - H(i, j - 1, h);
                        var nrm = new Vector3(-hx, 2f * cell, -hz).normalized;
                        float shade = Mathf.Clamp01(0.35f + 0.85f * Vector3.Dot(nrm, light));
                        float u = (h - lo) / Mathf.Max(1f, hi - lo);
                        var lowC = new Color(0.36f, 0.48f, 0.27f); var midC = new Color(0.55f, 0.52f, 0.36f); var highC = new Color(0.78f, 0.76f, 0.72f);
                        c = u < 0.5f ? Color.Lerp(lowC, midC, u * 2f) : Color.Lerp(midC, highC, (u - 0.5f) * 2f);
                        c *= shade;
                        // 25 m grid lines every 250 m for scale
                        float wx = _minX + (i + 0.5f) * cell, wz = _minZ + (j + 0.5f) * cell;
                        if (Mathf.Abs(Mathf.Repeat(wx, 250f)) < cell || Mathf.Abs(Mathf.Repeat(wz, 250f)) < cell) c = Color.Lerp(c, Color.black, 0.18f);
                    }
                    rgb[k * 3] = (byte)(Mathf.Clamp01(c.r) * 255); rgb[k * 3 + 1] = (byte)(Mathf.Clamp01(c.g) * 255); rgb[k * 3 + 2] = (byte)(Mathf.Clamp01(c.b) * 255);
                }
            Directory.CreateDirectory(Dir);
            _natives.TryGetValue(map, out var nat);
            Write(map, rgb, nat.a, nat.b);
            MPLog.Info($"Map overview: '{map}' saved to {FileOf(map)}");
            _cache.Remove(map); _missingUntil.Remove(map);
        }
        static float H(int i, int j, float fallback)
        {
            if (i < 0 || j < 0 || i >= Res || j >= Res) return fallback;
            float v = _h[j * Res + i]; return float.IsNaN(v) ? fallback : v;
        }

        static void Write(string map, byte[] rgb, Vector3? a, Vector3? b)
        {
            var f = FileOf(map);
            using var w = new BinaryWriter(File.Create(f + ".part"));
            w.Write(0x50414D53); w.Write(Res); w.Write(_minX); w.Write(_minZ); w.Write(_size);
            w.Write(a.HasValue); if (a.HasValue) { w.Write(a.Value.x); w.Write(a.Value.y); w.Write(a.Value.z); }
            w.Write(b.HasValue); if (b.HasValue) { w.Write(b.Value.x); w.Write(b.Value.y); w.Write(b.Value.z); }
            w.Write(rgb);
            w.Close();
            if (File.Exists(f)) File.Delete(f);
            File.Move(f + ".part", f);
        }

        /// <summary>Remember where the map's own two spawns are (shown as A / B on the overview).</summary>
        static readonly Dictionary<string, (Vector3? a, Vector3? b)> _natives = new Dictionary<string, (Vector3?, Vector3?)>();
        public static void NoteSpawns(string map, Vector3? a, Vector3? b)
        {
            if (string.IsNullOrEmpty(map)) return;
            _natives[map] = (a, b);
            if (_building == map) return;   // written with the overview
            try
            {
                var f = FileOf(map); if (!File.Exists(f) || (a == null && b == null)) return;
                byte[] rgb; float mx, mz, sz; Vector3? oa = null, ob = null;
                using (var r = new BinaryReader(File.OpenRead(f)))
                {
                    if (r.ReadInt32() != 0x50414D53 || r.ReadInt32() != Res) return;
                    mx = r.ReadSingle(); mz = r.ReadSingle(); sz = r.ReadSingle();
                    if (r.ReadBoolean()) oa = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (r.ReadBoolean()) ob = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    rgb = r.ReadBytes(Res * Res * 3);
                }
                a ??= oa; b ??= ob;
                if (oa.HasValue && ob.HasValue && a.HasValue && b.HasValue && (oa.Value - a.Value).sqrMagnitude < 4f && (ob.Value - b.Value).sqrMagnitude < 4f) return;
                _minX = mx; _minZ = mz; _size = sz;
                Write(map, rgb, a, b);
                _cache.Remove(map); _missingUntil.Remove(map);
                MPLog.Info($"Map overview '{map}': map spawns A {a} / B {b}");
            }
            catch (Exception e) { MPLog.Debug("map spawns: " + e.Message); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>The game's eras (StreamingAssets/Eras/*.json: name + start date). A design belongs to the last era that
    /// started on or before its creation date.</summary>
    public static class Eras
    {
        public sealed class Era { public string Key = "", Title = ""; public DateTime Start; public int MediumKg, HeavyKg; }
        static List<Era> _all;
        /// <summary>Era count without loading them (safe off the main thread; loaded at the first lobby update).</summary>
        public static int Count => _all?.Count ?? 6;
        public static List<Era> All
        {
            get
            {
                if (_all != null) return _all;
                var l = new List<Era>();
                try
                {
                    var dir = Path.Combine(Application.streamingAssetsPath, "Eras");
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.json"))
                        {
                            var txt = File.ReadAllText(f);
                            string name = Str(txt, "name"); if (!TryDate(Str(txt, "start"), out var start) || name == "") continue;
                            l.Add(new Era { Key = name, Title = Pretty(name), Start = start, MediumKg = Int(txt, "mediumVehicleMass"), HeavyKg = Int(txt, "heavyVehicleMass") });
                        }
                }
                catch (Exception e) { MPLog.Warn("Eras: " + e.Message); }
                if (l.Count == 0)   // the game's eras as of 0.2.55
                    foreach (var (k, d, m, h) in new[] { ("WWI", "1914.07.28", 8000, 16000), ("Interwar", "1918.11.12", 10000, 20000), ("Earlywar", "1939.09.02", 12000, 24000),
                                                          ("Midwar", "1942.01.01", 15000, 30000), ("Latewar", "1944.01.01", 18000, 36000), ("Coldwar", "1945.09.03", 18000, 36000) })
                    { TryDate(d, out var s); l.Add(new Era { Key = k, Title = Pretty(k), Start = s, MediumKg = m, HeavyKg = h }); }
                _all = l.OrderBy(e => e.Start).ToList();
                MPLog.Info("Eras: " + string.Join(", ", _all.Select(e => $"{e.Title} from {e.Start:yyyy-MM-dd}")));
                return _all;
            }
        }
        /// <summary>"Earlywar" -> "Early war", "WWI" stays.</summary>
        static string Pretty(string k)
        {
            if (k.Length > 3 && k.EndsWith("war", StringComparison.OrdinalIgnoreCase) && !k.EndsWith(" war", StringComparison.OrdinalIgnoreCase))
                k = k.Substring(0, k.Length - 3) + " war";
            return k.Length > 0 ? char.ToUpperInvariant(k[0]) + k.Substring(1) : k;
        }
        public static int Of(DateTime d) { int i = -1; var a = All; for (int k = 0; k < a.Count; k++) if (a[k].Start <= d) i = k; return i < 0 && a.Count > 0 ? 0 : i; }
        public static string Title(int i) => i >= 0 && i < All.Count ? All[i].Title : "?";
        public static bool TryDate(string s, out DateTime d) => DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy.MM.dd", "yyyy.M.d", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        internal static string Str(string json, string key) { var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : ""; }
        internal static int Int(string json, string key) { var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(\\.[0-9]+)?)"); return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)Math.Round(v) : 0; }
    }

    /// <summary>What a design is, from its blueprint header (mass, cost, creation date): read once per file version.</summary>
    public sealed class DesignInfo
    {
        public bool Ok; public int MassKg, Cost; public DateTime Date; public int Era = -1;
        public string EraTitle => Era >= 0 ? Eras.Title(Era) : "unknown era";
        public string Short => Ok ? $"{EraTitle} · {MassKg / 1000f:0.#} t · {Money(Cost)}" : "can't read this design";
        public static string Money(int c) => c >= 1000000 ? $"{c / 1000000f:0.##}M" : c >= 1000 ? $"{c / 1000f:0.#}k" : c.ToString();

        static readonly Dictionary<string, (DateTime t, long len, DesignInfo i)> _cache = new Dictionary<string, (DateTime, long, DesignInfo)>(StringComparer.OrdinalIgnoreCase);
        public static DesignInfo Of(string path)
        {
            if (string.IsNullOrEmpty(path)) return new DesignInfo();
            try
            {
                var fi = new FileInfo(path); if (!fi.Exists) return new DesignInfo();
                if (_cache.TryGetValue(path, out var c) && c.t == fi.LastWriteTimeUtc && c.len == fi.Length) return c.i;
                var buf = new byte[(int)Math.Min(fi.Length, 16384)];
                using (var fs = File.OpenRead(path)) { int got = 0, n; while (got < buf.Length && (n = fs.Read(buf, got, buf.Length - got)) > 0) got += n; }
                var txt = Encoding.UTF8.GetString(buf);
                // only the header object (it holds no nested objects); later parts have "mass" fields of their own
                int h = txt.IndexOf("\"header\"", StringComparison.Ordinal); int a = h >= 0 ? txt.IndexOf('{', h) : -1; int b = a >= 0 ? txt.IndexOf('}', a) : -1;
                var info = new DesignInfo();
                if (b > a && a > 0)
                {
                    var head = txt.Substring(a, b - a + 1);
                    info.MassKg = Eras.Int(head, "mass"); info.Cost = Eras.Int(head, "cost");
                    if (Eras.TryDate(Eras.Str(head, "creationDate"), out var d)) { info.Date = d; info.Era = Eras.Of(d); }
                    info.Ok = info.MassKg > 0;
                }
                if (_cache.Count > 500) _cache.Clear();
                _cache[path] = (fi.LastWriteTimeUtc, fi.Length, info);
                return info;
            }
            catch (Exception e) { MPLog.Debug("design info: " + e.Message); return new DesignInfo(); }
        }
    }

    /// <summary>Requirements the host sets for the tanks in a lobby. 255 / 0 = no limit.</summary>
    public sealed class LobbyRules
    {
        public byte EraMin = 255, EraMax = 255; public int MaxMassKg, MaxCost;
        public bool Any => EraMin != 255 || EraMax != 255 || MaxMassKg > 0 || MaxCost > 0;

        public void Write(BinaryWriter w) { w.Write(EraMin); w.Write(EraMax); w.Write(MaxMassKg); w.Write(MaxCost); }
        public static LobbyRules Read(BinaryReader r)
        {
            var x = new LobbyRules { EraMin = r.ReadByte(), EraMax = r.ReadByte(), MaxMassKg = r.ReadInt32(), MaxCost = r.ReadInt32() };
            return x.Clean();
        }
        LobbyRules Clean()
        {
            int n = Eras.Count;
            if (EraMin != 255 && EraMin >= n) EraMin = 255;
            if (EraMax != 255 && EraMax >= n) EraMax = 255;
            if (EraMin != 255 && EraMax != 255 && EraMin > EraMax) (EraMin, EraMax) = (EraMax, EraMin);
            MaxMassKg = Mathf.Clamp(MaxMassKg, 0, 100000000); MaxCost = Mathf.Clamp(MaxCost, 0, 1000000000);
            return this;
        }
        public LobbyRules Copy() => new LobbyRules { EraMin = EraMin, EraMax = EraMax, MaxMassKg = MaxMassKg, MaxCost = MaxCost };
        public bool Same(LobbyRules o) => o != null && o.EraMin == EraMin && o.EraMax == EraMax && o.MaxMassKg == MaxMassKg && o.MaxCost == MaxCost;

        /// <summary>Short text code for the config file, the LAN answer and the Steam lobby data: "e1-3;m30000;c0" (eras by
        /// index, 'x' = any).</summary>
        public string Code() => $"e{(EraMin == 255 ? "x" : EraMin.ToString())}-{(EraMax == 255 ? "x" : EraMax.ToString())};m{MaxMassKg};c{MaxCost}";
        public static LobbyRules FromCode(string s)
        {
            var x = new LobbyRules();
            try
            {
                foreach (var part in (s ?? "").Split(';'))
                {
                    if (part.Length < 2) continue;
                    var v = part.Substring(1);
                    switch (part[0])
                    {
                        case 'e': { var ab = v.Split('-'); if (ab.Length == 2) { x.EraMin = byte.TryParse(ab[0], out var a) ? a : (byte)255; x.EraMax = byte.TryParse(ab[1], out var b) ? b : (byte)255; } break; }
                        case 'm': int.TryParse(v, out x.MaxMassKg); break;
                        case 'c': int.TryParse(v, out x.MaxCost); break;
                    }
                }
            }
            catch { }
            return x.Clean();
        }

        public string EraText()
        {
            if (EraMin == 255 && EraMax == 255) return "any era";
            if (EraMin == EraMax) return Eras.Title(EraMin) + " only";
            if (EraMin == 255) return Eras.Title(EraMax) + " or older";
            if (EraMax == 255) return Eras.Title(EraMin) + " or newer";
            return $"{Eras.Title(EraMin)} to {Eras.Title(EraMax)}";
        }
        /// <summary>One line for the lobby and the game browser.</summary>
        public string Summary()
        {
            if (!Any) return "no tank rules";
            var l = new List<string>();
            if (EraMin != 255 || EraMax != 255) l.Add(EraText());
            if (MaxMassKg > 0) l.Add($"max {MaxMassKg / 1000f:0.#} t");
            if (MaxCost > 0) l.Add($"max cost {DesignInfo.Money(MaxCost)}");
            return string.Join(", ", l);
        }

        /// <summary>"" when the design meets the rules, else why not (short).</summary>
        public string Check(DesignInfo d)
        {
            if (!Any) return "";
            if (d == null || !d.Ok) return "can't read the design's era / weight";
            var why = new List<string>();
            if (EraMin != 255 || EraMax != 255)
            {
                if (d.Era < 0) why.Add("unknown era");
                else if ((EraMin != 255 && d.Era < EraMin) || (EraMax != 255 && d.Era > EraMax)) why.Add($"{d.EraTitle} (needs {EraText()})");
            }
            if (MaxMassKg > 0 && d.MassKg > MaxMassKg) why.Add($"{d.MassKg / 1000f:0.#} t (max {MaxMassKg / 1000f:0.#} t)");
            if (MaxCost > 0 && d.Cost > MaxCost) why.Add($"cost {DesignInfo.Money(d.Cost)} (max {DesignInfo.Money(MaxCost)})");
            return string.Join(", ", why);
        }
        public string Check(string path) => Any ? Check(DesignInfo.Of(path)) : "";
    }
}

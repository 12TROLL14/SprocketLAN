using System;
using System.IO;
using System.Collections.Generic;
using BepInEx.Configuration;
using Sprocket;
using Sprocket.DamageModelling;
using SprocketMP.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP.Shells
{
    public enum ShellType : byte { AP = 0, APHE = 1, HE = 2, APDS = 3 }

    /// <summary>Shell settings + in-game selection (keys 1/2/3, switching forces a reload).
    /// AP is the game's own shell. APHE and HE are the same native projectile (so ballistics stay identical on every
    /// machine) tagged with a ShellKind + filler payload; the VICTIM applies their effect when its armour simulation runs:
    ///   APHE: penetration x (1 - APHEPenalty) x (1 - filler% x PenLossPerFillerPercent); after penetrating it detonates
    ///         FuseDistance metres further on: fragments (run through the game's own spall simulation) + blast.
    ///   HE:   penetration x HEPenFraction; detonates on impact. Inside (it penetrated) = full effect; outside = blast only,
    ///         reduced by OutsideBlastFactor for interior modules (overpressure through the armour).</summary>
    public static class ShellConfig
    {
        public static ConfigEntry<float> BouncesPer100mm, FuseSensitivity, APHEFiller, HEFiller, APHEPenalty, PenLossPer100g, HEPenCurve_unused, APPenMultiplier, MaxAPHEFillerFraction, MaxHEFillerFraction, FuseDistance, OutsideBlastFactor, BlastScale;
        public static ConfigEntry<string> KeyAP, KeyAPHE, KeyHE, KeyAPDS;
        public static ConfigEntry<float> APDSPenMultiplier, APDSConeNarrowing, APDSNormalizationReduction, APHENormalizationFactor,
            SpallConeAngle, SpallFragmentsPer10mm, SpallMassFraction, SpallSpeedFraction;
        public static ConfigEntry<int> SpallBounces;
        public static ConfigEntry<float> AmmoRackHealthMultiplier, APHEFuseNormalization, APHESpallReduction, SpallDamageScale, BurstDamageScale, ArmourRhaThreshold;

        public static void Bind(ConfigFile c)
        {
            APHEFiller = c.Bind("Shells", "APHEFillerGrams", 50f, "APHE explosive filler in grams (more filler = bigger burst, less penetration)");
            HEFiller = c.Bind("Shells", "HEFillerGrams", 650f, "HE explosive filler in grams");
            APHEPenalty = c.Bind("Shells", "APHEPenetrationPenalty", 0.15f, "APHE penetrates this fraction less than plain AP (0.15 = 15%) before filler is counted");
            PenLossPer100g = c.Bind("Shells", "APHEPenetrationLossPer100g", 0.05f, "Extra APHE penetration loss per 100 g of filler (0.05 = 5% per 100 g), on top of the base penalty; total is capped so APHE keeps at least 30% of AP penetration");
            APPenMultiplier = c.Bind("Shells", "APPenetrationMultiplier", 1f, "AP penetration multiplier (1 = the game's own value)");
            HEPenCurve = c.Bind("Shells", "HEPenetrationCurve", "0.65:9, 3:30, 8:49", "HE penetration curve, 'filler kg:penetration mm' points (interpolated, extended past the last point with the last slope); never more than AP");
            MaxAPHEFillerFraction = c.Bind("Shells", "MaxAPHEFillerFraction", 0.04f, "Maximum APHE filler as a fraction of the projectile mass (set by calibre/shell size)");
            MaxHEFillerFraction = c.Bind("Shells", "MaxHEFillerFraction", 0.15f, "Maximum HE filler as a fraction of the projectile mass (set by calibre/shell size)");
            BouncesPer100mm = c.Bind("Shells", "FragmentBouncesPer100mmCalibre", 2f, "How many times APHE/HE burst fragments may bounce/step inside the tank per 100 mm of calibre (75 mm -> 3); never more than the game's own limit");
            FuseSensitivity = c.Bind("Shells", "APHEFuseSensitivityMm", 9f, "APHE fuse only arms after going through at least this much armour (mm); thinner plates and the shell passes through without exploding");
            FuseDistance = c.Bind("Shells", "APHEFuseDistance", 1.2f, "APHE detonates this many metres after penetrating");
            OutsideBlastFactor = c.Bind("Shells", "HEOutsideBlastInterior", 0f, "Share of the blast that reaches INTERIOR modules/crew when HE explodes outside the armour without penetrating (0 = none; tracks/wheels outside always take the full blast)");
            BlastScale = c.Bind("Shells", "BlastDamageScale", 1f, "Global multiplier for explosive blast damage");
            KeyAP = c.Bind("Shells", "KeyAP", "Digit1", "Key that loads AP (Unity Input System key name)");
            KeyAPHE = c.Bind("Shells", "KeyAPHE", "Digit2", "Key that loads APHE");
            KeyHE = c.Bind("Shells", "KeyHE", "Digit3", "Key that loads HE");
            KeyAPDS = c.Bind("Shells", "KeyAPDS", "Digit4", "Key that loads APDS");
            APDSPenMultiplier = c.Bind("Shells", "APDSPenetrationMultiplier", 1.1f, "APDS penetration relative to AP (1.1 = +10%)");
            APDSConeNarrowing = c.Bind("Shells", "APDSSpallConeNarrowing", 0.2f, "APDS spall cone is this much narrower than AP's (0.2 = 20% shallower)");
            APDSNormalizationReduction = c.Bind("Shells", "APDSNormalizationReduction", 0.6f, "APDS turns this much less towards the plate normal when penetrating angled armour (0.3 = 30% less)");
            APHENormalizationFactor = c.Bind("Shells", "APHENormalisation", 0.33f, "APHE normalisation (turning towards the plate normal on angled armour) relative to the game's value (0.33 = a third)");
            SpallConeAngle = c.Bind("Shells", "SpallConeHalfAngleDegrees", 15f, "Half-angle (degrees) of the spall cone thrown behind a penetrated plate (AP/APHE; APDS is narrower)");
            SpallFragmentsPer10mm = c.Bind("Shells", "SpallFragmentsPer10mmCal", 1.5f, "Spall fragments per 10 mm of calibre (75 mm -> 11)");
            SpallMassFraction = c.Bind("Shells", "SpallMassFractionOfShell", 0.06f, "Total spall mass as a fraction of the shell mass (lower = lighter, less damaging fragments)");
            SpallSpeedFraction = c.Bind("Shells", "SpallSpeedFractionOfShell", 0.6f, "Spall speed as a fraction of the shell's speed after the plate");
            ArmourRhaThreshold = c.Bind("Shells", "ArmourRhaThreshold", 0.3f, "Damage-model materials with at least this RHA factor count as armour (lower = modules/crew)");
            SpallDamageScale = c.Bind("Shells", "SpallFragmentDamageScale", 0.35f, "Damage each spall-cone fragment does to modules/crew (1 = the game's full per-hit damage)");
            BurstDamageScale = c.Bind("Shells", "BurstFragmentDamageScale", 0.5f, "Damage each APHE/HE burst fragment does to modules/crew (1 = the game's full per-hit damage)");
            APHESpallReduction = c.Bind("Shells", "APHESpallReduction", 0.25f, "APHE throws this much less spall when penetrating (0.25 = 25% fewer fragments)");
            AmmoRackHealthMultiplier = c.Bind("Shells", "AmmoRackHealthMultiplier", 4f, "Ammo racks get this much more health (1 = the game's own value)");
            APHEFuseNormalization = c.Bind("Shells", "APHEBurstNormalisation", 0.33f, "How much the APHE burst point follows the shell turning towards the plate normal after penetrating (0 = straight on, 1 = fully)");
            SpallBounces = c.Bind("Shells", "SpallBounces", 0, "How many times spall-cone fragments may bounce inside the tank after their first straight flight (0 = no bounces; each bounce makes spall much deadlier). The shooter's value is used");
        }

        /// <summary>Filler in grams of the active tank design.</summary>
        public static float Filler(ShellType t) { var d = ShellStore.Active; return t == ShellType.APHE ? Mathf.Clamp(d.ApheFiller, 0f, 2000f) : t == ShellType.HE ? Mathf.Clamp(d.HeFiller, 0f, 20000f) : 0f; }

        /// <summary>Largest filler (grams) a shell of this projectile mass can carry.</summary>
        public static float MaxFiller(ShellType t, float projectileMassKg) =>
            t == ShellType.APHE ? projectileMassKg * 1000f * MaxAPHEFillerFraction.Value : t == ShellType.HE ? projectileMassKg * 1000f * MaxHEFillerFraction.Value : 0f;

        public static float EffectiveFiller(ShellType t, float grams, float projectileMassKg) => Mathf.Clamp(grams, 0f, MaxFiller(t, projectileMassKg));

        /// <summary>Penetration (mm) of this shell type, given the AP penetration (mm) of the same gun.</summary>
        public static float Penetration(ShellType t, float apPenMm, float fillerG) => t switch
        {
            ShellType.APHE => apPenMm * Mathf.Clamp((1f - APHEPenalty.Value) * (1f - fillerG / 100f * PenLossPer100g.Value), 0.3f, 1f),
            ShellType.HE => Mathf.Min(apPenMm, HEPen(fillerG / 1000f)),
            ShellType.APDS => apPenMm * APDSPenMultiplier.Value,
            _ => apPenMm * APPenMultiplier.Value,
        };

        /// <summary>Normalisation (turn towards the plate normal) multiplier.</summary>
        public static float Normalization(ShellType t) => t == ShellType.APHE ? APHENormalizationFactor.Value : t == ShellType.APDS ? 1f - APDSNormalizationReduction.Value : 1f;

        /// <summary>Spall cone half-angle (deg).</summary>
        public static float ConeAngle(ShellType t) => SpallConeAngle.Value * (t == ShellType.APDS ? 1f - APDSConeNarrowing.Value : 1f);

        /// <summary>Penetration multiplier relative to the game's AP value.</summary>
        public static float PenFactor(ShellType t, float apPenMm, float fillerG) => apPenMm > 0.01f ? Mathf.Clamp(Penetration(t, apPenMm, fillerG) / apPenMm, 0.005f, 3f) : 1f;

        public static ConfigEntry<string> HEPenCurve;
        static string _curveSrc; static (float kg, float mm)[] _curve;

        /// <summary>HE penetration (mm) for a filler mass (kg), from the configured curve (starts at 0 kg = 0 mm).</summary>
        public static float HEPen(float kg)
        {
            if (_curve == null || _curveSrc != HEPenCurve.Value)
            {
                _curveSrc = HEPenCurve.Value;
                var pts = new List<(float, float)> { (0f, 0f) };
                foreach (var part in (_curveSrc ?? "").Split(','))
                {
                    var kv = part.Split(':'); if (kv.Length != 2) continue;
                    if (float.TryParse(kv[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k) &&
                        float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && k > 0) pts.Add((k, m));
                }
                pts.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                if (pts.Count < 2) pts.Add((0.65f, 9f));
                _curve = pts.ToArray();
            }
            var c = _curve;
            if (kg <= 0f) return 0f;
            for (int i = 1; i < c.Length; i++)
                if (kg <= c[i].kg) return Mathf.Lerp(c[i - 1].mm, c[i].mm, (kg - c[i - 1].kg) / Mathf.Max(1e-6f, c[i].kg - c[i - 1].kg));
            int n = c.Length - 1;
            float slope = (c[n].mm - c[n - 1].mm) / Mathf.Max(1e-6f, c[n].kg - c[n - 1].kg);
            return c[n].mm + slope * (kg - c[n].kg);
        }

        public static string Name(ShellType t) => t == ShellType.APHE ? "APHE" : t == ShellType.HE ? "HE" : t == ShellType.APDS ? "APDS" : "AP";
    }

    /// <summary>Shell settings saved per tank design (BepInEx/config/SprocketLAN_shells.txt):
    /// design name | APHE filler g | HE filler g | APHE fuse sensitivity mm | APHE fuse distance m</summary>
    public static class ShellStore
    {
        public class Design { public float ApheFiller, HeFiller, FuseSens, FuseDist; }
        static readonly Dictionary<string, Design> _d = new Dictionary<string, Design>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        public static string ActiveName = "";
        static string PathOf => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "SprocketLAN_shells.txt");

        public static Design Defaults() => new Design { ApheFiller = ShellConfig.APHEFiller.Value, HeFiller = ShellConfig.HEFiller.Value, FuseSens = ShellConfig.FuseSensitivity.Value, FuseDist = ShellConfig.FuseDistance.Value };

        /// <summary>Settings of the active design: the tank being edited, or the tank you drive in battle.</summary>
        public static Design Active
        {
            get
            {
                var bm = BattleManager.Instance;
                if (bm != null && bm.InBattle && bm.Local?.GO != null)
                {
                    if (_battleNameOf != bm.Local.GO.Pointer) { _battleNameOf = bm.Local.GO.Pointer; _battleName = DesignNameOf(bm.Local.GO) ?? bm.LocalVehicleName; MPLog.Info($"Shell settings: battle design '{_battleName}'"); }
                    ActiveName = _battleName;
                }
                return Get(ActiveName);
            }
        }

        static IntPtr _battleNameOf; static string _battleName = "";

        /// <summary>The design name the game shows (Vehicle.DesignInfo.Name), the same in the designer and in battle.</summary>
        public static string DesignNameOf(GameObject go)
        {
            try
            {
                var v = go.GetComponentInParent<Sprocket.Vehicles.Vehicle>() ?? go.GetComponentInChildren<Sprocket.Vehicles.Vehicle>(true);
                var n = v?.DesignInfo?.Name;
                return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
            }
            catch (Exception e) { MPLog.Debug("design name: " + e.Message); return null; }
        }

        /// <summary>Design being edited in the vehicle designer.</summary>
        public static string DesignNameInScene()
        {
            try
            {
                foreach (var v in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.Vehicle>())
                {
                    var n = v?.DesignInfo?.Name;
                    if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
                }
            }
            catch (Exception e) { MPLog.Debug("design name: " + e.Message); }
            return "";
        }

        public static Design Get(string name)
        {
            Load();
            if (!string.IsNullOrEmpty(name) && _d.TryGetValue(name, out var d)) return d;
            return Defaults();
        }

        public static void Set(string name, Design d)
        {
            if (string.IsNullOrEmpty(name)) return;
            Load(); _d[name] = d; Save();
        }

        static void Load()
        {
            if (_loaded) return; _loaded = true;
            try
            {
                if (!System.IO.File.Exists(PathOf)) return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in System.IO.File.ReadAllLines(PathOf))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;   // header / comments
                    var p = line.Split('|'); if (p.Length < 5) continue;
                    try { _d[p[0].Trim()] = new Design { ApheFiller = float.Parse(p[1].Trim(), ci), HeFiller = float.Parse(p[2].Trim(), ci), FuseSens = float.Parse(p[3].Trim(), ci), FuseDist = float.Parse(p[4].Trim(), ci) }; }
                    catch { MPLog.Warn("Shell settings: skipped bad line: " + line); }
                }
                MPLog.Info($"Shell settings loaded for {_d.Count} designs");
            }
            catch (Exception e) { MPLog.Warn("Shell settings load: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var lines = new List<string> { "# design | APHE filler g | HE filler g | APHE fuse sensitivity mm | APHE fuse distance m" };
                foreach (var kv in _d) lines.Add(string.Join("|", kv.Key, kv.Value.ApheFiller.ToString(ci), kv.Value.HeFiller.ToString(ci), kv.Value.FuseSens.ToString(ci), kv.Value.FuseDist.ToString(ci)));
                System.IO.File.WriteAllLines(PathOf, lines);
            }
            catch (Exception e) { MPLog.Warn("Shell settings save: " + e.Message); }
        }
    }

    /// <summary>Every shell parameter that affects what a hit does, taken from the SHOOTER's settings/design and sent
    /// with the shot; the tank that gets hit uses these, never its own config.</summary>
    public class ShellParams
    {
        public float Filler, FuseSens, FuseDist, HEPenMm, PenFactor = 1f, Normalization = 1f, ConeDeg = 15f,
            SpallPer10mm = 1.5f, SpallMassFrac = 0.06f, SpallSpeedFrac = 0.6f, SpallBounces = 0f, SpallCountMult = 1f,
            SpallDamage = 0.35f, BurstDamage = 0.5f, BouncesPer100mm = 2f;
        const byte Version = 1;
        const int Count = 15;

        public void Write(BinaryWriter w)
        {
            w.Write((byte)0xA5); w.Write(Version);
            foreach (var v in new[] { Filler, FuseSens, FuseDist, HEPenMm, PenFactor, Normalization, ConeDeg, SpallPer10mm, SpallMassFrac, SpallSpeedFrac, SpallBounces, SpallCountMult, SpallDamage, BurstDamage, BouncesPer100mm }) w.Write(v);
        }

        public byte[] ToBytes() { using var ms = new MemoryStream(); using var w = new BinaryWriter(ms); Write(w); w.Flush(); return ms.ToArray(); }

        public static ShellParams Parse(byte[] p)
        {
            try
            {
                if (p == null || p.Length < 2 + Count * 4 || p[0] != 0xA5) return null;
                var f = new float[Count]; for (int i = 0; i < Count; i++) f[i] = BitConverter.ToSingle(p, 2 + i * 4);
                return new ShellParams { Filler = f[0], FuseSens = f[1], FuseDist = f[2], HEPenMm = f[3], PenFactor = f[4], Normalization = f[5], ConeDeg = f[6], SpallPer10mm = f[7], SpallMassFrac = f[8], SpallSpeedFrac = f[9], SpallBounces = f[10], SpallCountMult = f[11], SpallDamage = f[12], BurstDamage = f[13], BouncesPer100mm = f[14] };
            }
            catch { return null; }
        }

        /// <summary>The local player's shell of this type (their config + their tank design's saved settings).</summary>
        public static ShellParams ForLocal(ShellType t)
        {
            var d = ShellStore.Active;
            var g = ShellSelect.MainGun();
            float filler = ShellConfig.Filler(t);
            if (g.mass > 0f) filler = ShellConfig.EffectiveFiller(t, filler, g.mass);
            return new ShellParams
            {
                Filler = filler, FuseSens = d.FuseSens, FuseDist = d.FuseDist,
                HEPenMm = t == ShellType.HE ? ShellConfig.HEPen(filler / 1000f) : 0f,
                PenFactor = ShellConfig.PenFactor(t, 100f, filler),
                Normalization = ShellConfig.Normalization(t), ConeDeg = ShellConfig.ConeAngle(t),
                SpallPer10mm = ShellConfig.SpallFragmentsPer10mm.Value, SpallMassFrac = ShellConfig.SpallMassFraction.Value, SpallSpeedFrac = ShellConfig.SpallSpeedFraction.Value,
                SpallBounces = ShellConfig.SpallBounces.Value, SpallCountMult = t == ShellType.APHE ? 1f - ShellConfig.APHESpallReduction.Value : 1f,
                SpallDamage = ShellConfig.SpallDamageScale.Value, BurstDamage = ShellConfig.BurstDamageScale.Value, BouncesPer100mm = ShellConfig.BouncesPer100mm.Value,
            };
        }
    }

    public static class ShellSelect
    {
        public static ShellType Current = ShellType.AP;
        static ShellType _loaded = ShellType.AP;
        public static float SwitchedAt = -10f;

        public static void Update(bool typing)
        {
            var bm = BattleManager.Instance;
            if (bm == null || !bm.InBattle || typing) return;
            var kb = Keyboard.current; if (kb == null) return;
            if (Pressed(kb, ShellConfig.KeyAP.Value)) Select(ShellType.AP);
            else if (Pressed(kb, ShellConfig.KeyAPHE.Value)) Select(ShellType.APHE);
            else if (Pressed(kb, ShellConfig.KeyHE.Value)) Select(ShellType.HE);
            else if (Pressed(kb, ShellConfig.KeyAPDS.Value)) Select(ShellType.APDS);
        }

        static bool Pressed(Keyboard kb, string name)
        {
            try { return Enum.TryParse<Key>(name, true, out var k) && k != Key.None && kb[k].wasPressedThisFrame; } catch { return false; }
        }

        static void Select(ShellType t)
        {
            if (t == Current) return;
            Current = t; SwitchedAt = Time.time;
            MPLog.Info($"Shell selected: {ShellConfig.Name(t)}");
            if (t != _loaded) ForceReload();
        }

        /// <summary>Unload the gun(s) so the new shell type has to be loaded.</summary>
        static void ForceReload()
        {
            var local = BattleManager.Instance?.Local; if (local == null) return;
            foreach (var w in local.Weapons)
            {
                try
                {
                    if (w == null) continue;
                    w.NeedsReload = true;
                    w.RequestReload(new Il2CppSystem.EventArgs());
                }
                catch (Exception e) { MPLog.Warn("Shell reload: " + e.Message); }
            }
            _loaded = Current;
        }

        /// <summary>Called when the local gun fires: the round that left the barrel is the one that was loaded.</summary>
        public static ShellType OnFired() { _loaded = Current; return Current; }

        public static string HudText()
        {
            string Item(ShellType t, string key) => t == Current ? $"<b><color=#ffd060>[{KeyLabel(key)}] {ShellConfig.Name(t)}</color></b>" : $"<color=#9aa>[{KeyLabel(key)}] {ShellConfig.Name(t)}</color>";
            string s = "Shell  " + Item(ShellType.AP, ShellConfig.KeyAP.Value) + "  " + Item(ShellType.APHE, ShellConfig.KeyAPHE.Value) + "  " + Item(ShellType.HE, ShellConfig.KeyHE.Value) + "  " + Item(ShellType.APDS, ShellConfig.KeyAPDS.Value);
            if (Time.time - SwitchedAt < 3f) s += "  <color=#fd8>reloading</color>";
            var g = GunInfo();
            if (g.cal > 0)
            {
                float fa = ShellConfig.EffectiveFiller(ShellType.APHE, ShellConfig.Filler(ShellType.APHE), g.mass), fh = ShellConfig.EffectiveFiller(ShellType.HE, ShellConfig.Filler(ShellType.HE), g.mass);
                string P(ShellType t, float f) => g.ap > 0 ? $"{ShellConfig.Penetration(t, g.ap, f):F0} mm" : "?";
                s += $"\n<size=15><color=#bcc>AP {P(ShellType.AP, 0)}  |  APHE {P(ShellType.APHE, fa)}, {fa:F0} g  |  HE frags {P(ShellType.HE, fh)}, {fh:F0} g  |  APDS {P(ShellType.APDS, 0)}</color></size>";
            }
            return s;
        }

        static (int cal, float mass, float ap) _gun; static IntPtr _gunOf;

        /// <summary>Main gun of the local tank (largest calibre): calibre, projectile mass, AP penetration (mm).</summary>
        public static (int cal, float mass, float ap) MainGun() => GunInfo();

        static (int cal, float mass, float ap) GunInfo()
        {
            var local = BattleManager.Instance?.Local;
            if (local?.GO == null) return default;
            if (_gunOf == local.GO.Pointer) return _gun;
            _gunOf = local.GO.Pointer; _gun = default;
            try
            {
                foreach (var c in local.GO.GetComponentsInChildren<Sprocket.Vehicles.Cannons.Cannon>(true))
                {
                    var bp = c?.Blueprint; if (bp == null || bp.Caliber <= _gun.cal) continue;
                    var prof = new Sprocket.Vehicles.ShellProfile(bp.PropellantLength, bp.Caliber);
                    float v = c.muzzleVelocity;
                    _gun = (bp.Caliber, prof.ProjectileMass, v > 1f ? prof.GetPenetration(v, bp.PenetratorConstant) : -1f);
                }
            }
            catch (Exception e) { MPLog.Debug("gun info: " + e.Message); }
            return _gun;
        }

        static string KeyLabel(string k) => k.StartsWith("Digit") ? k.Substring(5) : k;
    }

    /// <summary>Tags replicated shots with the selected shell type and its filler.</summary>
    public class ExplosiveShellHandler : IShellHandler
    {
        readonly ShellType _type;
        public ExplosiveShellHandler(ShellType t) { _type = t; }
        public ushort Kind => (ushort)_type;
        public string Name => ShellConfig.Name(_type);

        public bool ClaimLocalLaunch(FireMsg shot, VehicleRig shooter, Il2CppSystem.Object source, BinaryWriter payload)
        {
            if (ShellSelect.Current != _type) return false;
            ShellParams.ForLocal(_type).Write(payload);      // everything the target needs to resolve the hit, from OUR settings
            return true;
        }

        // the projectile itself is the native one (same ballistics everywhere); the effect is applied by the victim
        public bool HandleRemote(FireMsg shot, VehicleRig puppet) => false;

        public static float ReadFiller(FireMsg f) => ReadF(f, 0, 0f);
        public static float ReadFuseSens(FireMsg f) => ReadF(f, 4, ShellConfig.FuseSensitivity.Value);
        public static float ReadFuseDist(FireMsg f) => ReadF(f, 8, ShellConfig.FuseDistance.Value);
        static float ReadF(FireMsg f, int off, float dflt)
        {
            try { if (f.Payload != null && f.Payload.Length >= off + 4) return BitConverter.ToSingle(f.Payload, off); } catch { }
            return dflt;
        }
    }

    /// <summary>Victim-side detonation: fragments through the game's own penetration/spall simulation, plus blast.</summary>
    public static class Explosives
    {
        public static bool InExplosion;
        public static int Mode;    // 1 = shell burst fragments, 2 = spall cone
        public static ShellParams P = new ShellParams();   // the shooter's parameters of the hit being resolved
        public static int MaxDepth = 99;
        public static Vector3 LastPoint; public static float LastRadius;

        /// <summary>TNT-equivalent mass (kg) of a shell.</summary>
        public static float Tnt(float shellMassKg, float fillerGrams) => Mathf.Clamp(fillerGrams / 1000f, 0f, shellMassKg * 0.6f + 0.001f);
        const float GurneyTnt = 2400f;   // m/s, TNT
        public static float Radius(float tnt) => 0.5f + 1.6f * Mathf.Pow(Mathf.Max(tnt, 0.001f), 1f / 3f);

        public static void Detonate(CompoundStructure cs, VehicleRig local, Vector3 point, Vector3 dir, float shellMass, float filler, ushort penConst, bool inside, bool forwardCone, bool applyDamage = true, float caliberMm = 75f, bool blast = true, float fragPenMm = -1f)
        {
            if (cs == null || local == null) return;
            float tnt = Tnt(shellMass, filler);
            if (tnt <= 0.0005f) return;
            float radius = Radius(tnt);
            LastPoint = point; LastRadius = radius;
            InExplosion = true; Mode = 1;
            int frags = 0, hits = 0;
            // bigger shells throw heavier fragments that keep bouncing around inside: allow more path steps (bounces)
            // (the game's own limit is a compiled constant; instead each burst fragment's simulated path is cut after
            //  this many bounces/child fragments, before its hits are applied - see PenCapture.LimitDepth)
            // HE: by calibre. APHE: small burster charges throw small fragments -> 1 bounce under 200 g, 2 above (max)
            int steps = forwardCone ? (tnt < 0.2f ? 1 : 2)
                                    : Mathf.Clamp(Mathf.RoundToInt(caliberMm / 100f * P.BouncesPer100mm), 1, 32);
            MaxDepth = steps;
            try
            {
                // ---- fragments: the casing breaks up; each fragment is simulated against our armour/modules by the game
                // casing breaks into small fragments; their speed follows the Gurney equation (filler/casing ratio),
                // so a 50 g APHE burster gives slow fragments and a 650 g HE shell fast ones
                float casing = Mathf.Max(0.05f, shellMass - tnt);
                float cm = tnt / casing;
                float speed = Mathf.Clamp(GurneyTnt * Mathf.Sqrt(cm / (1f + 0.5f * cm)), 80f, 1800f);
                int baseN = Mathf.RoundToInt((8 + tnt * 40f) * 1.3f);
                // APHE: 20..100 fragments; HE: 2.5x as many, fewer (and slower, see Gurney above) with less filler
                int n = forwardCone ? Mathf.Clamp(Mathf.RoundToInt(75f * Mathf.Pow(tnt / 0.2f, 0.85f)), 8, 150)   // APHE: 200 g -> 75, 50 g -> ~23 (small fillers throw few fragments)
                    : Mathf.Clamp(Mathf.RoundToInt(baseN * 2.5f), 10, 200);
                float maxFm = forwardCone ? Mathf.Lerp(0.006f, 0.03f, Mathf.Clamp01(tnt / 0.2f)) : 0.03f;   // APHE: a small burster can't throw big chunks (50 g -> ~12 g fragments, 200 g+ -> 30 g)
                float fm = Mathf.Clamp(casing * 0.5f / n, 0.002f, maxFm);               // only part of the casing makes lethal fragments
                float fd = Mathf.Pow(6f * fm / (Mathf.PI * 7850f), 1f / 3f);
                // HE: the shell itself does not penetrate; its fragments carry the shell's penetration value
                if (fragPenMm > 0f) speed = SpeedForPen(fd, fm, penConst, fragPenMm);
                var rnd = new System.Random(unchecked((int)(point.x * 1000) ^ (int)(point.z * 977)));
                for (int i = 0; i < n; i++)
                {
                    Vector3 d = RandomDir(rnd);
                    if (forwardCone) { d = (d + dir * 0.45f).normalized; }                // moving shell: burst somewhat biased forward (wide cone)
                    else if (!inside && Vector3.Dot(d, dir) < -0.2f) continue;           // outside: half the burst flies away from the tank
                    var info = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, point, d * speed);   // born exactly at the burst point (kept in the air)
                    var sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default);
                    frags++;
                    if (sim == null) continue;
                    try { if (sim.Hit) hits++; if (applyDamage) sim.ApplyFragmentHits(); } catch { }
                }

                // ---- blast: overpressure on modules / crew within the radius
                float scale = Mathf.Clamp(tnt * 1.2f, 0.02f, 1.4f) * ShellConfig.BlastScale.Value;
                int blasted = 0;
                for (int i = 0; applyDamage && blast && i < local.Pools.Count && i < local.PoolOwners.Count; i++)
                {
                    var o = local.PoolOwners[i]; var pool = local.Pools[i];
                    if (o == null || pool == null) continue;
                    float dist = DistanceTo(o, point, out bool exterior);
                    if (dist > radius) continue;
                    float extF = (inside || exterior) ? 1f : ShellConfig.OutsideBlastFactor.Value;
                    float fall = 1f - dist / radius; fall *= fall;
                    float max = 1f; try { max = pool.max; } catch { }
                    float dmg = max * scale * fall * extF;
                    if (dmg <= 0.001f) continue;
                    try { pool.HealthDelta(-dmg); blasted++; } catch (Exception e) { MPLog.Debug("blast: " + e.Message); }
                }
                MPLog.Info($"Detonation {(inside ? "inside" : "outside")} ({caliberMm:F0} mm, {steps} fragment bounces, blast {(blast ? "on" : "off")}): {tnt * 1000f:F0} g TNT, radius {radius:F1} m, {frags} fragments of {fm * 1000f:F0} g at {speed:F0} m/s{(fragPenMm > 0 ? $" (~{fragPenMm:F0} mm pen)" : "")} ({hits} hit), {blasted} modules in blast");
            }
            finally
            {
                InExplosion = false;

            }
        }

        /// <summary>Speed (m/s) at which a fragment penetrates penMm of steel (bisection on the game's formula, diameter in mm).</summary>
        public static float SpeedForPen(float diameterM, float massKg, ushort penConst, float penMm)
        {
            float lo = 30f, hi = 5000f;
            try
            {
                for (int i = 0; i < 30; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (PenetrationUtils.ComputePenetration(diameterM * 1000f, massKg, mid, penConst) < penMm) lo = mid; else hi = mid;
                }
            }
            catch { return 800f; }
            return hi;
        }

        /// <summary>Spall thrown behind a penetrated plate (replaces the game's own spall for AP/APHE/APDS): a cone around
        /// the shell's direction after the plate, simulated fragment by fragment against our tank.</summary>
        public static int SpallCone(CompoundStructure cs, Vector3 point, Vector3 dir, float residualSpeed, float shellMass, float caliberMm, ushort penConst, float coneDeg, bool applyDamage, float countMult = 1f)
        {
            if (cs == null || residualSpeed < 50f) return 0;
            point += dir * 0.05f;          // start in the air behind the plate, never inside the steel (fragments born in a plate ignore armour)
            int n = Mathf.Clamp(Mathf.RoundToInt(caliberMm / 10f * P.SpallPer10mm * countMult), 4, 80);
            float fm = Mathf.Clamp(shellMass * P.SpallMassFrac / n, 0.002f, 0.2f);
            float fd = Mathf.Pow(6f * fm / (Mathf.PI * 7850f), 1f / 3f);
            float speed = residualSpeed * P.SpallSpeedFrac;
            var rnd = new System.Random(unchecked((int)(point.x * 733) ^ (int)(point.y * 1013)));
            Vector3 a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized, b = Vector3.Cross(dir, a);
            float tanMax = Mathf.Tan(Mathf.Clamp(coneDeg, 1f, 85f) * Mathf.Deg2Rad);
            InExplosion = true; Mode = 2; MaxDepth = Mathf.Max(0, Mathf.RoundToInt(P.SpallBounces));
            int hits = 0;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    // concentrated around the axis (most spall carries straight on), thinning towards the cone edge
                    float r = tanMax * (float)rnd.NextDouble(), ang = (float)(rnd.NextDouble() * Math.PI * 2);
                    var d = (dir + a * (Mathf.Cos(ang) * r) + b * (Mathf.Sin(ang) * r)).normalized;
                    float sp = speed * (0.9f + 0.2f * (float)rnd.NextDouble());   // consistent spall speed
                    var info = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, point + d * 0.01f, d * sp);
                    var sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default);
                    if (sim == null) continue;
                    try { if (sim.Hit) hits++; if (applyDamage) sim.ApplyFragmentHits(); } catch { }
                }
            }
            finally { InExplosion = false; Mode = 0; }
            float pen = 0f; try { pen = PenetrationUtils.ComputePenetration(fd * 1000f, fm, speed, penConst); } catch { }
            MPLog.Debug($"Spall cone: {n} fragments of {fm * 1000f:F0} g at ~{speed:F0} m/s (~{pen:F0} mm pen), {coneDeg:F0} deg ({hits} hit)");
            return n;
        }

        /// <summary>Distance from the burst to the closest point of a module (its colliders/renderers, not its pivot -
        /// a track's pivot can be metres away from the part that got hit). Exterior parts (tracks, wheels, suspension)
        /// take the full blast even when the shell explodes outside the armour.</summary>
        static float DistanceTo(Sprocket.Vehicles.VehicleComponent o, Vector3 p, out bool exterior)
        {
            exterior = false;
            float best = float.MaxValue;
            try
            {
                var groups = o.models;
                if (groups != null)
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        var g = groups[gi]; if (g == null) continue;
                        if (((int)g.partType & (2 | 8192 | 32768)) != 0) exterior = true;
                        var lods = g.lods;
                        if (lods != null && lods.Length > 0 && lods[0]?.renderer != null) best = Mathf.Min(best, Vector3.Distance(lods[0].renderer.bounds.ClosestPoint(p), p));
                    }
            }
            catch { }
            try
            {
                foreach (var c in o.GetComponentsInChildren<Collider>(true))
                    if (c != null && c.enabled) best = Mathf.Min(best, Vector3.Distance(c.bounds.ClosestPoint(p), p));
            }
            catch { }
            if (best == float.MaxValue) best = Vector3.Distance(o.transform.position, p);
            return best;
        }

        static Vector3 RandomDir(System.Random r)
        {
            float z = (float)(r.NextDouble() * 2 - 1), a = (float)(r.NextDouble() * Math.PI * 2), s = Mathf.Sqrt(1 - z * z);
            return new Vector3(s * Mathf.Cos(a), s * Mathf.Sin(a), z);
        }
    }
}

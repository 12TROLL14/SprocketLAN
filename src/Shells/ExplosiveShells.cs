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
    public enum ShellType : byte { AP = 0, APHE = 1, HE = 2, APDS = 3, APFSDS = 4, HEAT = 5 }

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
        public static ConfigEntry<string> KeyAP, KeyAPHE, KeyHE, KeyAPDS, KeyAPFSDS, KeyHEAT;
        public class Fixed<T> { public T Value; public Fixed(T v) { Value = v; } }   // former settings of the old (game-based) model, now fixed
        public static readonly Fixed<bool> LosArmour = new Fixed<bool>(false), OwnModel = new Fixed<bool>(true);
        public static readonly Fixed<float> HEATMaxGap = new Fixed<float>(0.6f), HEATNormalizationReduction = new Fixed<float>(1f), HEATSpacedLoss = new Fixed<float>(0.4f), HEATGapLossPerMetre = new Fixed<float>(0.6f), HEATAngleBonus = new Fixed<float>(0.15f), APHEFuseNormalization = new Fixed<float>(0.33f);
        public static ConfigEntry<float> ExitNorm60, ExitNorm70, HEATJetLossPerMetre, HEATRed60, HEATRed70, RicoAP, RicoAPHE, RicoAPDS, RicoAPFSDS, RicoHEAT, RicoAPC, RicoAPHEC, RicoPenLoss, SpallModuleKeep, TrackFragmentDamage, HEBurstBack, HEBurstOffSurface;
        static float Ramp(float ang, float v60, float v70) => ang <= 60f ? v60 * Mathf.Max(0f, ang) / 60f : ang >= 70f ? v70 : v60 + (v70 - v60) * (ang - 60f) / 10f;
        /// <summary>Own model: degrees a shell turns toward the plate's normal when it leaves a plate hit at 'ang'.</summary>
        public static float ExitNormalization(float ang) => Ramp(ang, ExitNorm60.Value, ExitNorm70.Value);
        /// <summary>Own model: HEAT's reduction of the line-of-sight thickness (0..1).</summary>
        public static float HeatLosReduction(float ang) => Mathf.Clamp01(Ramp(ang, HEATRed60.Value, HEATRed70.Value));
        public static float RicochetAngle(ShellType t, bool cap) => cap && t == ShellType.AP ? RicoAPC.Value : cap && t == ShellType.APHE ? RicoAPHEC.Value : RicochetAngle(t);
        public static float RicochetAngle(ShellType t) => t == ShellType.APHE ? RicoAPHE.Value : t == ShellType.APDS ? RicoAPDS.Value : t == ShellType.APFSDS ? RicoAPFSDS.Value : t == ShellType.HEAT ? RicoHEAT.Value : RicoAP.Value;
        public static ConfigEntry<string> APHEBounceSteps;
        public static ConfigEntry<bool> SoftWheels; public static ConfigEntry<bool> UseGameArmourModel, OwnFragments; public static ConfigEntry<float> AppliqueExitTurn, AutocannonPenDivisor;
        public static int APHEBounces(float grams) { int n = 0; foreach (var x in (APHEBounceSteps.Value ?? "").Split(',')) if (float.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) && grams >= g) n++; return n; }
        public static ConfigEntry<float> OvermatchRatio, HEATConeNarrowing, APCapNormalizationReduction, APCapPenPenalty;
        public static ConfigEntry<float> APDSVelocityMultiplier, APFSDSVelocityMultiplier, APFSDSPenMultiplier, APFSDSSpallIncrease, APFSDSConeNarrowing, APFSDSNormalizationReduction;
        public static ConfigEntry<float> APDSPenMultiplier, APDSConeNarrowing, APDSNormalizationReduction, APHENormalizationFactor,
            SpallConeAngle, SpallFragmentsPer10mm, SpallMassFraction, SpallSpeedFraction;
        public static ConfigEntry<int> SpallBounces;
        public static ConfigEntry<float> AmmoRackHealthMultiplier, APHESpallReduction, SpallDamageScale, BurstDamageScale, ArmourRhaThreshold;

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
            APHEBounceSteps = c.Bind("Shells", "APHEFragmentBounceThresholdsGrams", "120,180,500", "APHE/APHEC burst fragment bounces by filler: below the first value 0 bounces, below the second 1, below the third 2, and so on");
            BouncesPer100mm = c.Bind("Shells", "FragmentBouncesPer100mmCalibre", 2f, "How many times HE burst fragments may bounce/step inside the tank per 100 mm of calibre (75 mm -> 3); never more than the game's own limit");
            FuseSensitivity = c.Bind("Shells", "APHEFuseSensitivityMm", 9f, "APHE fuse only arms after going through at least this much armour (mm); thinner plates and the shell passes through without exploding");
            FuseDistance = c.Bind("Shells", "APHEFuseDistance", 1.2f, "APHE detonates this many metres after penetrating");
            OutsideBlastFactor = c.Bind("Shells", "HEOutsideBlastInterior", 0f, "Share of the blast that reaches INTERIOR modules/crew when HE explodes outside the armour without penetrating (0 = none; tracks/wheels outside always take the full blast)");
            BlastScale = c.Bind("Shells", "BlastDamageScale", 1f, "Global multiplier for explosive blast damage");
            KeyAP = c.Bind("Shells", "KeyAP", "Digit1", "Key that loads AP (Unity Input System key name)");
            KeyAPHE = c.Bind("Shells", "KeyAPHE", "Digit2", "Key that loads APHE");
            KeyHE = c.Bind("Shells", "KeyHE", "Digit3", "Key that loads HE");
            KeyAPDS = c.Bind("Shells", "KeyAPDS", "Digit4", "Key that loads APDS");
            KeyAPFSDS = c.Bind("Shells", "KeyAPFSDS", "Digit5", "Key that loads APFSDS");
            KeyHEAT = c.Bind("Shells", "KeyHEAT", "Digit6", "Key that loads HEAT");
            ExitNorm60 = c.Bind("Armour", "ExitNormalizationAt60", 11.05f, "Leaving a plate hit at 60 deg, the shell turns this many degrees toward the plate's normal (0 at 0 deg, linear in between). Caps, APDS and APFSDS reduce it; HEAT has none");
            ExitNorm70 = c.Bind("Armour", "ExitNormalizationAt70", 13f, "...and this many degrees for plates hit at 70 deg or more");
            HEATJetLossPerMetre = c.Bind("Armour", "HEATJetLossPerMetre", 1.3f, "HEAT's jet loses this share of its penetration per metre between one plate's impact point and the next (0.5 m = 50%)");
            HEATRed60 = c.Bind("Armour", "HEATLosReductionAt60", 0.15f, "HEAT: line-of-sight thickness counts this much less on a plate hit at 60 deg (0 at 0 deg, linear in between)");
            HEATRed70 = c.Bind("Armour", "HEATLosReductionAt70", 0.20f, "HEAT: ...and this much less at 70 deg or more");
            RicoAP = c.Bind("Armour", "RicochetAngleAP", 68f, "Own model: AP ricochets off plates hit at this angle from the normal or more (unless it overmatches)");
            SpallModuleKeep = c.Bind("Shells", "SpallThroughModuleSpeed", 0.7f, "Spall-cone fragments that hit a crew member or other module carry on through it at this share of their speed (0 = they stop there)");
            HEBurstBack = c.Bind("Armour", "HEBurstBackAlongPath", 0.15f, "HE bursting on impact: metres the burst point is moved back along the shell's path from the first thing it touched");
            HEBurstOffSurface = c.Bind("Armour", "HEBurstOffSurface", 0.10f, "HE bursting on impact: metres the burst point is moved off the struck surface along its normal (keeps it out of thin parts like fenders)");
            AutocannonPenDivisor = c.Bind("Shells", "AutocannonPenetrationDivisor", 1.5f, "Designs set as autocannon (designer Shells panel): their AP penetration (and every shell based on it, HE excepted) is divided by this");
            AppliqueExitTurn = c.Bind("Armour", "AppliqueExitTurnDegrees", 0f, "Degrees a shell turns when it leaves applique / add-on armour with no thickness value (fenders, spare track armour); plates use ExitNormalizationAt60/70");
            OwnFragments = c.Bind("Armour", "OwnFragmentModel", true, "Spall, burst fragments and the shell inside the tank are traced through the game's armour model by the mod (much faster) instead of one game penetration simulation per fragment. Needs UseGameArmourModel");
            UseGameArmourModel = c.Bind("Armour", "UseGameArmourModel", true, "Read plates and their raw thickness from the game's own armour model (what the designer's armour view shows) instead of measuring the 3D models. Faster and more reliable; turn off to go back to the old measurement");
            TrackFragmentDamage = c.Bind("Armour", "TrackFragmentDamage", 0.2f, "Share of a track's health each fragment (HE, APHE burst, spall cone) takes when it hits the track hitbox (0.2 = 5 hits destroy it); a direct shell hit always destroys it");
            SoftWheels = c.Bind("Armour", "WheelsNearlyArmourless", false, "Wheels and sprocket count as almost no armour so fragments pass through them (turn off to test if the game freezes because of it)");
            RicoPenLoss = c.Bind("Armour", "RicochetPenetrationLoss", 0.3f, "A shell that bounces off a plate loses this share of its remaining penetration (0.3 = 30%)");
            RicoAPC = c.Bind("Armour", "RicochetAngleAPC", 70f, "Own model: capped AP (APC) ricochet angle");
            RicoAPHEC = c.Bind("Armour", "RicochetAngleAPHEC", 70f, "Own model: capped APHE (APHEC) ricochet angle");
            RicoAPHE = c.Bind("Armour", "RicochetAngleAPHE", 68f, "Own model: APHE ricochet angle");
            RicoAPDS = c.Bind("Armour", "RicochetAngleAPDS", 75f, "Own model: APDS ricochet angle");
            RicoAPFSDS = c.Bind("Armour", "RicochetAngleAPFSDS", 78f, "Own model: APFSDS ricochet angle");
            RicoHEAT = c.Bind("Armour", "RicochetAngleHEAT", 82f, "Own model: HEAT ricochet (fuse failure) angle");
            OvermatchRatio = c.Bind("Armour", "OvermatchCalibreRatio", 3.5f, "Overmatch: a shell whose calibre is at least this many times the plate's thickness can't ricochet and ignores the slope (only the real thickness counts)");
            HEATConeNarrowing = c.Bind("Shells", "HEATConeNarrowing", 0.3f, "HEAT spall cone is this much narrower than AP's (a jet, 0.3 = 30% shallower)");
            APCapNormalizationReduction = c.Bind("Shells", "APCapNormalizationReduction", 0.1f, "AP / APHE with an armour-piercing cap (designer Shells panel): turns this much less towards the plate normal (0.1 = 10%)");
            APCapPenPenalty = c.Bind("Shells", "APCapPenetrationPenalty", 0.02f, "AP / APHE with an armour-piercing cap lose this much penetration (0.02 = 2%)");
            APDSVelocityMultiplier = c.Bind("Shells", "APDSVelocityMultiplier", 1.3f, "APDS muzzle velocity relative to the gun's normal shell (flatter, faster flight; penetration is set by APDSPenetrationMultiplier only)");
            APFSDSVelocityMultiplier = c.Bind("Shells", "APFSDSVelocityMultiplier", 1.5f, "APFSDS muzzle velocity relative to the gun's normal shell (penetration is set by APFSDSPenetrationMultiplier only)");
            APFSDSPenMultiplier = c.Bind("Shells", "APFSDSPenetrationMultiplier", 1.1f, "APFSDS penetration relative to AP");
            APFSDSSpallIncrease = c.Bind("Shells", "APFSDSSpallIncrease", 0.15f, "APFSDS throws this much more spall in its cone than APDS (0.15 = +15%)");
            APFSDSConeNarrowing = c.Bind("Shells", "APFSDSConeNarrowing", 0.1f, "APFSDS spall cone is this much narrower than APDS's (0.1 = 10% shallower)");
            APFSDSNormalizationReduction = c.Bind("Shells", "APFSDSNormalizationReduction", 0.8f, "APFSDS turns this much less towards the plate normal (like APDSNormalizationReduction)");
            APDSPenMultiplier = c.Bind("Shells", "APDSPenetrationMultiplier", 1.1f, "APDS penetration relative to AP (1.1 = +10%)");
            APDSConeNarrowing = c.Bind("Shells", "APDSSpallConeNarrowing", 0.2f, "APDS spall cone is this much narrower than AP's (0.2 = 20% shallower)");
            APDSNormalizationReduction = c.Bind("Shells", "APDSNormalizationReduction", 0.6f, "APDS turns this much less towards the plate normal when penetrating angled armour (0.3 = 30% less)");
            APHENormalizationFactor = c.Bind("Shells", "APHENormalisation", 1f, "APHE normalisation (turning towards the plate normal on angled armour) relative to the game's value (0.33 = a third)");
            SpallConeAngle = c.Bind("Shells", "SpallConeHalfAngleDegrees", 15f, "Half-angle (degrees) of the spall cone thrown behind a penetrated plate (AP/APHE; APDS is narrower)");
            SpallFragmentsPer10mm = c.Bind("Shells", "SpallFragmentsPer10mmCal", 1.5f, "Spall fragments per 10 mm of calibre (75 mm -> 11)");
            SpallMassFraction = c.Bind("Shells", "SpallMassFractionOfShell", 0.06f, "Total spall mass as a fraction of the shell mass (lower = lighter, less damaging fragments)");
            SpallSpeedFraction = c.Bind("Shells", "SpallSpeedFractionOfShell", 0.6f, "Spall speed as a fraction of the shell's speed after the plate");
            ArmourRhaThreshold = c.Bind("Shells", "ArmourRhaThreshold", 0.3f, "Damage-model materials with at least this RHA factor count as armour (lower = modules/crew)");
            SpallDamageScale = c.Bind("Shells", "SpallFragmentDamageScale", 0.35f, "Damage each spall-cone fragment does to modules/crew (1 = the game's full per-hit damage)");
            BurstDamageScale = c.Bind("Shells", "BurstFragmentDamageScale", 0.5f, "Damage each APHE/HE burst fragment does to modules/crew (1 = the game's full per-hit damage)");
            APHESpallReduction = c.Bind("Shells", "APHESpallReduction", 0.25f, "APHE throws this much less spall when penetrating (0.25 = 25% fewer fragments)");
            AmmoRackHealthMultiplier = c.Bind("Shells", "AmmoRackHealthMultiplier", 4f, "Ammo racks get this much more health (1 = the game's own value)");
            SpallBounces = c.Bind("Shells", "SpallBounces", 0, "How many times spall-cone fragments may bounce inside the tank after their first straight flight (0 = no bounces; each bounce makes spall much deadlier). The shooter's value is used");
        }

        /// <summary>Filler in grams of the active tank design.</summary>
        public static float Filler(ShellType t) { var d = ShellStore.Active; return t == ShellType.APHE ? Mathf.Clamp(d.ApheFiller, 0f, 2000f) : t == ShellType.HE ? Mathf.Clamp(d.HeFiller, 0f, 20000f) : 0f; }

        /// <summary>Largest filler (grams) a shell of this projectile mass can carry.</summary>
        public static float MaxFiller(ShellType t, float projectileMassKg) =>
            t == ShellType.APHE ? projectileMassKg * 1000f * MaxAPHEFillerFraction.Value : t == ShellType.HE ? projectileMassKg * 1000f * MaxHEFillerFraction.Value : 0f;

        public static float EffectiveFiller(ShellType t, float grams, float projectileMassKg) => Mathf.Clamp(grams, 0f, MaxFiller(t, projectileMassKg));

        /// <summary>Penetration (mm) of this shell type, given the AP penetration (mm) of the same gun.</summary>
        public static float Penetration(ShellType t, float apPenMm, float fillerG) => PenetrationRaw(t, apPenMm, fillerG) * (HasCap(t) ? 1f - Mathf.Clamp01(APCapPenPenalty.Value) : 1f);
        static float PenetrationRaw(ShellType t, float apPenMm, float fillerG) => t switch
        {
            ShellType.APHE => apPenMm * Mathf.Clamp((1f - APHEPenalty.Value) * (1f - fillerG / 100f * PenLossPer100g.Value), 0.3f, 1f),
            ShellType.HE => Mathf.Min(apPenMm, HEPen(fillerG / 1000f)),
            ShellType.APDS => apPenMm * APDSPenMultiplier.Value,
            ShellType.APFSDS => apPenMm * APFSDSPenMultiplier.Value,
            ShellType.HEAT => apPenMm * APPenMultiplier.Value,
            _ => apPenMm * APPenMultiplier.Value,
        };

        /// <summary>Normalisation (turn towards the plate normal) multiplier.</summary>
        public static float Normalization(ShellType t) => t == ShellType.APHE ? APHENormalizationFactor.Value : t == ShellType.APDS ? 1f - APDSNormalizationReduction.Value : t == ShellType.APFSDS ? 1f - APFSDSNormalizationReduction.Value : t == ShellType.HEAT ? Mathf.Clamp01(1f - HEATNormalizationReduction.Value) : 1f;
        /// <summary>HEAT: penetration multiplier for a hit at this angle from the plate normal (degrees).</summary>
        public static float HeatAngleFactor(float bonus, float angleDeg)
        {
            if (bonus <= 0f || angleDeg <= 0f) return 1f;
            float c = Mathf.Cos(Mathf.Clamp(angleDeg, 0f, 85f) * Mathf.Deg2Rad);
            float slope = Mathf.Clamp01(1f / Mathf.Max(c, 0.05f) - 1f);       // 0 flat-on .. 1 at 60 deg (thickness doubled)
            return 1f / Mathf.Max(0.2f, 1f - bonus * slope);
        }
        /// <summary>Name as shown to the player: AP / APHE with a cap become APC / APHEC.</summary>
        public static string DisplayName(ShellType t, bool capped) => capped && t == ShellType.AP ? "APC" : capped && t == ShellType.APHE ? "APHEC" : Name(t);
        public static string DisplayName(ShellType t) => DisplayName(t, HasCap(t));
        public static bool HasCap(ShellType t) { var d = ShellStore.Active; return (t == ShellType.AP && d.CapAP) || (t == ShellType.APHE && d.CapAPHE); }
        public static bool IsSabot(ShellType t) => t == ShellType.APDS || t == ShellType.APFSDS;
        /// <summary>Muzzle velocity multiplier (sabot rounds).</summary>
        public static float VelocityMult(ShellType t) => t == ShellType.APDS ? Mathf.Clamp(APDSVelocityMultiplier.Value, 0.2f, 5f) : t == ShellType.APFSDS ? Mathf.Clamp(APFSDSVelocityMultiplier.Value, 0.2f, 5f) : 1f;

        /// <summary>Spall cone half-angle (deg).</summary>
        public static float ConeAngle(ShellType t) => SpallConeAngle.Value * (t == ShellType.APDS ? 1f - APDSConeNarrowing.Value : t == ShellType.APFSDS ? (1f - APDSConeNarrowing.Value) * (1f - APFSDSConeNarrowing.Value) : t == ShellType.HEAT ? 1f - HEATConeNarrowing.Value : 1f);

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

        public static string Name(ShellType t) => t == ShellType.APHE ? "APHE" : t == ShellType.HE ? "HE" : t == ShellType.APDS ? "APDS" : t == ShellType.APFSDS ? "APFSDS" : t == ShellType.HEAT ? "HEAT" : "AP";
    }

    /// <summary>Shell settings saved per tank design (BepInEx/config/SprocketLAN_shells.txt):
    /// design name | APHE filler g | HE filler g | APHE fuse sensitivity mm | APHE fuse distance m</summary>
    public static class ShellStore
    {
        public class Design { public float ApheFiller, HeFiller, FuseSens, FuseDist, FireRate = 120f; public bool CapAP, CapAPHE, AutoCannon; public Design Copy() => (Design)MemberwiseClone(); }
        static readonly Dictionary<string, Design> _d = new Dictionary<string, Design>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        public static string ActiveName = "";
        /// <summary>The gun the settings are for: its ammo "calibre x propellant length" (e.g. "20x200"), as on the ammo rack. Empty = the design's general settings.</summary>
        public static string ActiveSpec = "";
        public static string Key(string name, string spec) => string.IsNullOrEmpty(spec) ? name : name + "#" + spec;
        public static string SpecOf(Sprocket.Vehicles.Cannons.Cannon c) { try { var bp = c?.Blueprint; return bp == null ? "" : $"{bp.Caliber}x{bp.PropellantLength}"; } catch { return ""; } }

        static readonly Dictionary<IntPtr, string> _specOfWeapon = new Dictionary<IntPtr, string>();
        /// <summary>Ammo spec of the gun a weapon belongs to (cached).</summary>
        public static string SpecOfWeapon(GameObject tank, Sprocket.WeaponFramework.WeaponBehaviour w)
        {
            if (w == null || tank == null) return "";
            if (_specOfWeapon.TryGetValue(w.Pointer, out var sp)) return sp;
            sp = "";
            try { foreach (var c in tank.GetComponentsInChildren<Sprocket.Vehicles.Cannons.Cannon>(true)) { var cw = c?.Weapon; if (cw != null && cw.Pointer == w.Pointer) { sp = SpecOf(c); break; } } } catch { }
            if (_specOfWeapon.Count > 256) _specOfWeapon.Clear();
            _specOfWeapon[w.Pointer] = sp;
            MPLog.Info($"Shell settings: gun {(sp == "" ? "(not found)" : sp)} -> settings '{Key(ActiveName, sp)}'");
            return sp;
        }
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
                return Get(Key(ActiveName, ActiveSpec));
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
            int h = name != null ? name.IndexOf('#') : -1;
            if (h > 0 && _d.TryGetValue(name.Substring(0, h), out var gen)) return gen;     // no settings for this gun yet: the design's general ones
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
                    try { _d[p[0].Trim()] = new Design { ApheFiller = float.Parse(p[1].Trim(), ci), HeFiller = float.Parse(p[2].Trim(), ci), FuseSens = float.Parse(p[3].Trim(), ci), FuseDist = float.Parse(p[4].Trim(), ci), CapAP = p.Length > 5 && p[5].Trim() == "1", CapAPHE = p.Length > 6 && p[6].Trim() == "1", AutoCannon = p.Length > 7 && p[7].Trim() == "1", FireRate = p.Length > 8 && float.TryParse(p[8].Trim(), System.Globalization.NumberStyles.Float, ci, out var fr) ? fr : 120f }; }
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
                var lines = new List<string> { "# design | APHE filler g | HE filler g | APHE fuse sensitivity mm | APHE fuse distance m | AP cap | APHE cap | autocannon | fire rate rpm" };
                foreach (var kv in _d) lines.Add(string.Join("|", kv.Key, kv.Value.ApheFiller.ToString(ci), kv.Value.HeFiller.ToString(ci), kv.Value.FuseSens.ToString(ci), kv.Value.FuseDist.ToString(ci), kv.Value.CapAP ? "1" : "0", kv.Value.CapAPHE ? "1" : "0", kv.Value.AutoCannon ? "1" : "0", kv.Value.FireRate.ToString(ci)));
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
            SpallDamage = 0.35f, BurstDamage = 0.5f, BouncesPer100mm = 2f, VelMult = 1f, HeatBonus = 0f, RicoDeg = 0f;
        const byte Version = 4;
        const int Count = 18;

        public void Write(BinaryWriter w)
        {
            w.Write((byte)0xA5); w.Write(Version);
            foreach (var v in new[] { Filler, FuseSens, FuseDist, HEPenMm, PenFactor, Normalization, ConeDeg, SpallPer10mm, SpallMassFrac, SpallSpeedFrac, SpallBounces, SpallCountMult, SpallDamage, BurstDamage, BouncesPer100mm, VelMult, HeatBonus, RicoDeg }) w.Write(v);
        }

        public byte[] ToBytes() { using var ms = new MemoryStream(); using var w = new BinaryWriter(ms); Write(w); w.Flush(); return ms.ToArray(); }

        public static ShellParams Parse(byte[] p)
        {
            try
            {
                if (p == null || p.Length < 2 + 15 * 4 || p[0] != 0xA5) return null;
                int n = Math.Min(Count, (p.Length - 2) / 4);
                var f = new float[Count]; f[15] = 1f; f[16] = 0f; f[17] = 0f;
                for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(p, 2 + i * 4);
                return new ShellParams { Filler = f[0], FuseSens = f[1], FuseDist = f[2], HEPenMm = f[3], PenFactor = f[4], Normalization = f[5], ConeDeg = f[6], SpallPer10mm = f[7], SpallMassFrac = f[8], SpallSpeedFrac = f[9], SpallBounces = f[10], SpallCountMult = f[11], SpallDamage = f[12], BurstDamage = f[13], BouncesPer100mm = f[14], VelMult = f[15], HeatBonus = f[16], RicoDeg = f[17] };
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
            var p = new ShellParams
            {
                Filler = filler, FuseSens = d.FuseSens, FuseDist = d.FuseDist,
                HEPenMm = t == ShellType.HE ? ShellConfig.HEPen(filler / 1000f) : 0f,
                PenFactor = ShellConfig.PenFactor(t, 100f, filler),
                Normalization = ShellConfig.Normalization(t), ConeDeg = ShellConfig.ConeAngle(t),
                SpallPer10mm = ShellConfig.SpallFragmentsPer10mm.Value, SpallMassFrac = ShellConfig.SpallMassFraction.Value, SpallSpeedFrac = ShellConfig.SpallSpeedFraction.Value,
                SpallBounces = ShellConfig.SpallBounces.Value, SpallCountMult = t == ShellType.APHE ? 1f - ShellConfig.APHESpallReduction.Value : t == ShellType.APFSDS ? 1f + ShellConfig.APFSDSSpallIncrease.Value : 1f,
                VelMult = ShellConfig.VelocityMult(t),
                HeatBonus = t == ShellType.HEAT ? ShellConfig.HEATAngleBonus.Value : 0f,
                SpallDamage = ShellConfig.SpallDamageScale.Value, BurstDamage = ShellConfig.BurstDamageScale.Value, BouncesPer100mm = ShellConfig.BouncesPer100mm.Value,
            };
            if (d.AutoCannon && t != ShellType.HE) p.PenFactor /= Mathf.Max(1f, ShellConfig.AutocannonPenDivisor.Value);   // autocannon: lighter rounds, less penetration
            if (ShellConfig.HasCap(t))
            {
                p.RicoDeg = ShellConfig.RicochetAngle(t, true);
                p.Normalization *= 1f - Mathf.Clamp01(ShellConfig.APCapNormalizationReduction.Value);
            }
            return p;
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
            else if (Pressed(kb, ShellConfig.KeyAPFSDS.Value)) Select(ShellType.APFSDS);
            else if (Pressed(kb, ShellConfig.KeyHEAT.Value)) Select(ShellType.HEAT);
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
            int groups = ActiveFireGroups(); bool index = false;
            foreach (var w in local.Weapons) { try { if (w != null && w.FireGroup == 0) index = true; } catch { } }
            int n = 0;
            foreach (var w in local.Weapons)
            {
                try
                {
                    if (w == null) continue;
                    if (groups > 0) { int g = w.FireGroup, bit = index ? 1 << g : g; if ((groups & bit) == 0) continue; }   // only the active fire group's guns
                    n++;
                    w.NeedsReload = true;
                    w.RequestReload(new Il2CppSystem.EventArgs());
                }
                catch (Exception e) { MPLog.Warn("Shell reload: " + e.Message); }
            }
            _loaded = Current;
            var gl = new System.Text.StringBuilder(); foreach (var w in local.Weapons) { try { gl.Append(w != null ? w.FireGroup + " " : "- "); } catch { } }
            MPLog.Info($"Shell switch: {n} of {local.Weapons.Count} guns reloaded (active fire groups {groups}, gun groups {gl})");
        }

        /// <summary>The player's selected fire group(s) (flags: group 1 = 1, group 2 = 2, group 3 = 4...), -1 if unknown.</summary>
        static int ActiveFireGroups()
        {
            try
            {
                foreach (var vc in UnityEngine.Object.FindObjectsOfType<Sprocket.Gameplay.VehicleControl.VehicleController>())
                    if (vc != null && vc.ControlledVehicle != null) return (int)vc.FireGroup;
            }
            catch (Exception e) { MPLog.Debug("fire group: " + e.Message); }
            return -1;
        }

        /// <summary>Called when the local gun fires: the round that left the barrel is the one that was loaded.</summary>
        public static ShellType OnFired() { _loaded = Current; return Current; }

        public static string HudText()
        {
            string Item(ShellType t, string key) => t == Current ? $"<b><color=#ffd060>[{KeyLabel(key)}] {ShellConfig.DisplayName(t)}</color></b>" : $"<color=#9aa>[{KeyLabel(key)}] {ShellConfig.DisplayName(t)}</color>";
            string s = "Shell  " + Item(ShellType.AP, ShellConfig.KeyAP.Value) + "  " + Item(ShellType.APHE, ShellConfig.KeyAPHE.Value) + "  " + Item(ShellType.HE, ShellConfig.KeyHE.Value) + "  " + Item(ShellType.APDS, ShellConfig.KeyAPDS.Value) + "  " + Item(ShellType.APFSDS, ShellConfig.KeyAPFSDS.Value) + "  " + Item(ShellType.HEAT, ShellConfig.KeyHEAT.Value);
            if (Time.time - SwitchedAt < 3f) s += "  <color=#fd8>reloading</color>";
            var g = GunInfo();
            if (g.cal > 0)
            {
                float fa = ShellConfig.EffectiveFiller(ShellType.APHE, ShellConfig.Filler(ShellType.APHE), g.mass), fh = ShellConfig.EffectiveFiller(ShellType.HE, ShellConfig.Filler(ShellType.HE), g.mass);
                string P(ShellType t, float f) => g.ap > 0 ? $"{ShellConfig.Penetration(t, g.ap, f):F0} mm" : "?";
                s += $"\n<size=15><color=#bcc>{ShellConfig.DisplayName(ShellType.AP)} {P(ShellType.AP, 0)}  |  {ShellConfig.DisplayName(ShellType.APHE)} {P(ShellType.APHE, fa)}, {fa:F0} g  |  HE frags {P(ShellType.HE, fh)}, {fh:F0} g  |  APDS {P(ShellType.APDS, 0)}  |  APFSDS {P(ShellType.APFSDS, 0)}  |  HEAT {P(ShellType.HEAT, 0)}</color></size>";
                s += $"\n<size=15><color=#bcc>APHE fuse: {ShellStore.Active.FuseSens:F0} mm sensitivity, {ShellStore.Active.FuseDist:F2} m delay</color></size>";
            }
            return s;
        }

        static (int cal, float mass, float ap) _gun; static IntPtr _gunOf; static string _gunSpec = "";

        /// <summary>Main gun of the local tank (largest calibre): calibre, projectile mass, AP penetration (mm).</summary>
        public static (int cal, float mass, float ap) MainGun() => GunInfo();

        static (int cal, float mass, float ap) GunInfo()
        {
            var local = BattleManager.Instance?.Local;
            if (local?.GO == null) return default;
            if (_gunOf == local.GO.Pointer && _gunSpec == ShellStore.ActiveSpec) return _gun;
            _gunOf = local.GO.Pointer; _gun = default; _gunSpec = ShellStore.ActiveSpec;
            try
            {
                foreach (var c in local.GO.GetComponentsInChildren<Sprocket.Vehicles.Cannons.Cannon>(true))
                {
                    var bp = c?.Blueprint; if (bp == null) continue;
                    if (!string.IsNullOrEmpty(_gunSpec)) { if (ShellStore.SpecOf(c) != _gunSpec) continue; }   // the gun that fires
                    else if (bp.Caliber <= _gun.cal) continue;
                    var prof = new Sprocket.Vehicles.ShellProfile(bp.PropellantLength, bp.Caliber);
                    float v = c.muzzleVelocity;
                    _gun = (bp.Caliber, prof.ProjectileMass, v > 1f ? PenetrationUtils.ComputePenetration(bp.Caliber, prof.ProjectileMass, v, bp.PenetratorConstant) : -1f);   // the formula the damage simulation itself uses (the designer's figure can be higher)
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
        public static bool Rebounding;   // HE fragments bouncing around inside the tank
        public static Vector3 LastPoint; public static float LastRadius;

        /// <summary>TNT-equivalent mass (kg) of a shell.</summary>
        public static float Tnt(float shellMassKg, float fillerGrams) => Mathf.Clamp(fillerGrams / 1000f, 0f, shellMassKg * 0.6f + 0.001f);
        const float GurneyTnt = 2400f;   // m/s, TNT
        public static float Radius(float tnt) => 0.5f + 1.6f * Mathf.Pow(Mathf.Max(tnt, 0.001f), 1f / 3f);

        public static void Detonate(CompoundStructure cs, VehicleRig local, Vector3 point, Vector3 dir, float shellMass, float filler, ushort penConst, bool inside, bool forwardCone, bool applyDamage = true, float caliberMm = 75f, bool blast = true, float fragPenMm = -1f, float shellSpeed = 0f)
        { MPLog.Stage = "detonation";
            if (cs == null || local == null) return;
            float tnt = Tnt(shellMass, filler);
            if (tnt <= 0.0005f) return;
            float radius = Radius(tnt);
            LastPoint = point; LastRadius = radius;
            InExplosion = true; Mode = 1;
            int frags = 0, hits = 0, throughWheels = 0, blockedPast = 0;
            // bigger shells throw heavier fragments that keep bouncing around inside: allow more path steps (bounces)
            // (the game's own limit is a compiled constant; instead each burst fragment's simulated path is cut after
            //  this many bounces/child fragments, before its hits are applied - see PenCapture.LimitDepth)
            // HE: by calibre. APHE: small burster charges throw small fragments -> 1 bounce under 200 g, 2 above (max)
            int steps = forwardCone ? ShellConfig.APHEBounces(tnt * 1000f)
                                    : Mathf.Clamp(Mathf.RoundToInt(caliberMm / 100f * P.BouncesPer100mm), 1, 32) + (inside ? 0 : 1);   // outside: getting through the outer plate is one step, not a bounce
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
                int heBounces = forwardCone ? 0 : Mathf.Clamp(Mathf.RoundToInt(caliberMm / 100f * P.BouncesPer100mm), 0, 4), bounced = 0;
                bool own = SprocketMP.PenCapture.OwnFragmentsAvailable(local);
                if (own) SprocketMP.PenCapture.Stats = default;
                for (int i = 0; own && i < n; i++)
                {
                    Vector3 d = RandomDir(rnd);
                    if (!forwardCone && !inside && Vector3.Dot(d, dir) < -0.2f) continue;
                    float v0 = speed + (forwardCone ? shellSpeed * 0.8f : 0f);
                    int b = forwardCone ? steps : heBounces;
                    SprocketMP.PenCapture.Fire(cs, local, point, d, v0, fm, fd, penConst, b, ShellConfig.SpallModuleKeep.Value, applyDamage, (byte)(64 | (inside ? 2 : 0)), rnd);
                    frags++;
                }
                if (own) { var st = SprocketMP.PenCapture.Stats; hits = st.HitModules; bounced = st.Bounced; }
                for (int i = 0; !own && i < n; i++)
                {
                    Vector3 d = RandomDir(rnd);
                    if (forwardCone) { }                                                   // APHE: full sphere
                    else if (!inside && Vector3.Dot(d, dir) < -0.2f) continue;           // outside: half the burst flies away from the tank
                    var vel = d * (speed + (forwardCone ? shellSpeed * 0.8f : 0f));   // APHE: sphere, fragments as fast as the moving shell's energy allows
                    var info = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, point, vel);   // born at the burst point (kept in the air)
                    var sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default);
                    frags++;
                    if (sim == null) continue;
                    try { if (sim.Hit) hits++; if (applyDamage) sim.ApplyFragmentHits(); } catch { }
                    // a fragment that really stopped in a wheel / the sprocket carries on from the far side of it
                    // (only if that far side is air: born inside anything solid it would ignore all armour)
                    if (!inside && SprocketMP.PenCamView.Instance != null)
                        foreach (var (at, fdir, fv) in SprocketMP.PenCapture.FragEnds(sim))
                        {
                            if (fv < 40f || !SprocketMP.PenCamView.Instance.InWheel(local, at)) continue;
                            var past = SprocketMP.PenCamView.Instance.PastWheels(local, at - fdir * 0.02f, fdir);
                            if ((past - at).sqrMagnitude < 1e-4f) continue;
                            if (SprocketMP.PenCamView.Instance.InsideArmour(local, past, fdir) || SprocketMP.PenCapture.InSteel(cs, penConst, past, fdir)) { blockedPast++; continue; }
                            var info2 = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, past, fdir * fv);
                            var s2 = cs.Penetrate(ref info2, PenetrationSimulationOptions.Default);
                            if (s2 == null) continue;
                            throughWheels++;
                            try { if (applyDamage) s2.ApplyFragmentHits(); } catch { }
                        }
                    // HE: fast fragments that got inside embed instead of ricocheting in the game's model;
                    // bounce them ourselves off whatever stopped them (slower each time)
                    if (!forwardCone && heBounces > 0) Rebound(cs, sim, fm, fd, penConst, applyDamage, rnd, heBounces, ref bounced);
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
                MPLog.Info($"Detonation {(inside ? "inside" : "outside")} ({caliberMm:F0} mm, {steps} fragment bounces, blast {(blast ? "on" : "off")}): {tnt * 1000f:F0} g TNT, radius {radius:F1} m, {frags} fragments{(bounced > 0 ? $" (+{bounced} bounces)" : "")} of {fm * 1000f:F0} g at {speed:F0} m/s{(shellSpeed > 0f && forwardCone ? $" + {shellSpeed * 0.8f:F0} m/s shell speed" : "")}{(fragPenMm > 0 ? $" (~{fragPenMm:F0} mm pen)" : "")} ({hits} hit, {throughWheels} carried on past wheels, {blockedPast} not moved: steel right behind the wheel), {blasted} modules in blast");
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
        public static VehicleRig _victimRig;
        public static int SpallCone(CompoundStructure cs, Vector3 point, Vector3 dir, float residualSpeed, float shellMass, float caliberMm, ushort penConst, float coneDeg, bool applyDamage, float countMult = 1f)
        { MPLog.Stage = "spall cone";
            if (cs == null || residualSpeed < 50f) { MPLog.Info($"Spall cone skipped: {residualSpeed:F0} m/s"); return 0; }
            point += dir * 0.005f;         // the caller already placed it in the air just behind the plate; a bigger step could land inside the next plate
            int n = Mathf.Clamp(Mathf.RoundToInt(caliberMm / 10f * P.SpallPer10mm * countMult), 4, 80);
            float fm = Mathf.Clamp(shellMass * P.SpallMassFrac / n, 0.002f, 0.2f);
            float fd = Mathf.Pow(6f * fm / (Mathf.PI * 7850f), 1f / 3f);
            float speed = residualSpeed * P.SpallSpeedFrac;
            var rnd = new System.Random(unchecked((int)(point.x * 733) ^ (int)(point.y * 1013)));
            Vector3 a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized, b = Vector3.Cross(dir, a);
            float tanMax = Mathf.Tan(Mathf.Clamp(coneDeg, 1f, 85f) * Mathf.Deg2Rad);
            InExplosion = true; Mode = 2; MaxDepth = Mathf.Max(0, Mathf.RoundToInt(P.SpallBounces));
            int hits = 0, passed = 0, probes = 0;
            bool own = _victimRig != null && SprocketMP.PenCapture.OwnFragmentsAvailable(_victimRig);
            try
            {
                if (own)
                {
                    SprocketMP.PenCapture.Stats = default;
                    for (int i = 0; i < n; i++)
                    {
                        float r = tanMax * (float)rnd.NextDouble(), ang = (float)(rnd.NextDouble() * Math.PI * 2);
                        var d = (dir + a * (Mathf.Cos(ang) * r) + b * (Mathf.Sin(ang) * r)).normalized;
                        float sp = speed * (0.9f + 0.2f * (float)rnd.NextDouble());
                        SprocketMP.PenCapture.Fire(cs, _victimRig, point + d * 0.01f, d, sp, fm, fd, penConst, MaxDepth, ShellConfig.SpallModuleKeep.Value, applyDamage, 128, rnd);
                    }
                    var st = SprocketMP.PenCapture.Stats; hits = st.HitModules; passed = st.Penetrated;
                    n = n + 0; goto done;
                }
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
                    // spall that got stuck in a soft module (crew, ammo...) carries on through it, slower
                    for (int pass = 0; pass < 2 && sim != null; pass++)
                    {
                        var stuck = SprocketMP.PenCapture.StuckInModules(sim); sim = null;
                        foreach (var (at, sd, v0, mat) in stuck)
                        {
                            float v = Mathf.Max(v0, sp * 0.5f) * ShellConfig.SpallModuleKeep.Value;
                            if (v < 60f) continue;
                            // through the module's own mesh (cheap); only if that fails, a damage-model probe (slow: max a few per cone)
                            float depth = _victimRig != null && SprocketMP.PenCamView.Instance != null ? SprocketMP.PenCamView.Instance.ModuleExitDistance(_victimRig, at, sd) : -1f;
                            if (depth > 0f) depth += 0.01f;
                            else { if (probes >= 3) continue; probes++; depth = SprocketMP.PenCapture.ModuleDepth(cs, at, sd, mat); }
                            var info2 = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, at + sd * (depth >= 0f ? depth + 0.02f : -depth - 0.005f), sd * v);
                            var s2 = cs.Penetrate(ref info2, PenetrationSimulationOptions.Default);
                            if (s2 == null) continue;
                            passed++;
                            try { if (applyDamage) s2.ApplyFragmentHits(); } catch { }
                            sim = s2;
                        }
                    }
                }
            }
            finally { InExplosion = false; Mode = 0; }
            done:
            float pen = 0f; try { pen = PenetrationUtils.ComputePenetration(fd * 1000f, fm, speed, penConst); } catch { }
            MPLog.Info($"Spall cone: {n} fragments of {fm * 1000f:F0} g at ~{speed:F0} m/s (~{pen:F0} mm pen), {coneDeg:F0} deg at ({point.x:F2},{point.y:F2},{point.z:F2}) ({hits} hit, {passed} carried on through modules)");
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

        static void Rebound(CompoundStructure cs, IPenetrationSimulation sim, float fm, float fd, ushort penConst, bool applyDamage, System.Random rnd, int left, ref int bounced, bool fromInside = false)
        {
            if (left <= 0 || bounced > 250) return;
            foreach (var (end, dir, speed) in SprocketMP.PenCapture.StoppedInside(sim, fromInside))
            {
                float v = Mathf.Max(speed, 0f) * 0.55f;
                if (v < 120f) v = Mathf.Max(120f, v);           // stopped fragments still carry some energy away
                // roughly back off the surface: mirror-ish, scattered
                var nd = (-dir * 0.8f + RandomDir(rnd) * 0.9f).normalized;
                if (Vector3.Dot(nd, dir) > -0.1f) nd = (nd - dir).normalized;
                var info = new PenetratorInfo(fm, fd, 7850f, 0f, penConst, end - dir * 0.03f, nd * v);
                IPenetrationSimulation s2;
                Rebounding = true;
                try { s2 = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { Rebounding = false; }
                bounced++;
                if (s2 == null) continue;
                try { if (applyDamage) s2.ApplyFragmentHits(); } catch { }
                if (bounced > 250) return;
                Rebound(cs, s2, fm, fd, penConst, applyDamage, rnd, left - 1, ref bounced, true);
            }
        }

        static Vector3 RandomDir(System.Random r)
        {
            float z = (float)(r.NextDouble() * 2 - 1), a = (float)(r.NextDouble() * Math.PI * 2), s = Mathf.Sqrt(1 - z * z);
            return new Vector3(s * Mathf.Cos(a), s * Mathf.Sin(a), z);
        }
    }
}

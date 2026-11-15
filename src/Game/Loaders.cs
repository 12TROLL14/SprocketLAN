using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.CrewSystems.Editor;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Extra loaders: crew seats assigned as additional loaders of a gun (in the designer, with a crew seat selected),
    /// which speed its reload up: 1 loader x1, 2 x1.5, 3 x2, 4 x3, 5 x4, 6 x5... (F7 › Loaders).
    ///
    /// The assignments are kept in BepInEx/config/SprocketLAN_loaders.txt (design | gun | seats), not in the design
    /// file, so designs stay loadable by the unmodded game. Guns and seats are identified by their VUID, the id the
    /// blueprint keeps for every part, the same in the designer and in battle.
    ///
    /// In battle, the gun's own loader (the game's) counts as 1, plus every assigned seat whose crew member is alive
    /// right now: a loader killed and not replaced slows the reload down again. The multiplier is applied by scaling
    /// the time step of that gun's load task.
    /// </summary>
    public static class Loaders
    {
        // ------------------------------------------------------------------ store
        static readonly Dictionary<string, Dictionary<int, List<int>>> _d = new Dictionary<string, Dictionary<int, List<int>>>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "SprocketLAN_loaders.txt");

        static void Load()
        {
            if (_loaded) return; _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('|'); if (p.Length < 3 || line.StartsWith("#")) continue;
                    if (!int.TryParse(p[1].Trim(), out int gun)) continue;
                    var seats = p[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim(), out var s) ? s : 0).Where(s => s != 0).Distinct().ToList();
                    if (seats.Count == 0) continue;
                    Of(p[0].Trim())[gun] = seats;
                }
                MPLog.Info($"Extra loaders loaded for {_d.Count} designs");
            }
            catch (Exception e) { MPLog.Warn("Extra loaders file: " + e.Message); }
        }
        static void Save()
        {
            try
            {
                var lines = new List<string> { "# SprocketLAN extra loaders: design | gun VUID | crew seat VUIDs" };
                foreach (var d in _d) foreach (var g in d.Value) if (g.Value.Count > 0) lines.Add($"{d.Key}|{g.Key}|{string.Join(",", g.Value)}");
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e) { MPLog.Warn("Extra loaders save: " + e.Message); }
        }
        static Dictionary<int, List<int>> Of(string design) { if (!_d.TryGetValue(design, out var m)) _d[design] = m = new Dictionary<int, List<int>>(); return m; }

        /// <summary>The gun this seat is an extra loader for (0 = none).</summary>
        public static int GunOfSeat(string design, int seat) { Load(); if (!_d.TryGetValue(design, out var m)) return 0; foreach (var g in m) if (g.Value.Contains(seat)) return g.Key; return 0; }
        public static int ExtraCount(string design, int gun) { Load(); return _d.TryGetValue(design, out var m) && m.TryGetValue(gun, out var l) ? l.Count : 0; }
        public static void Assign(string design, int seat, int gun)
        {
            Load();
            var m = Of(design);
            foreach (var g in m.Values) g.Remove(seat);
            if (gun != 0) { if (!m.TryGetValue(gun, out var l)) m[gun] = l = new List<int>(); if (!l.Contains(seat)) l.Add(seat); }
            foreach (var k in m.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) m.Remove(k);
            Save();
            MPLog.Info($"Extra loaders: '{design}' seat {seat} -> {(gun == 0 ? "none" : "gun " + gun)}");
        }
        /// <summary>Copy of a design's assignments (sent with the design to other players' F8 / lobby later if wanted).</summary>
        public static Dictionary<int, List<int>> Get(string design) { Load(); return _d.TryGetValue(design, out var m) ? m : null; }

        /// <summary>Reload speed for n loaders: from the list in the settings, then +1 per extra loader.</summary>
        public static float Multiplier(int n)
        {
            if (n <= 1) return 1f;
            var list = (Plugin.LoaderMultipliers.Value ?? "").Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).Where(f => !float.IsNaN(f)).ToList();
            if (list.Count == 0) list = new List<float> { 1f, 1.5f, 2f, 3f, 4f, 5f };
            if (n <= list.Count) return Mathf.Max(0.01f, list[n - 1]);
            return Mathf.Max(0.01f, list[list.Count - 1] + (n - list.Count));
        }

        public static int VuidOf(Sprocket.Vehicles.VehiclePart p) { try { return p == null ? 0 : (int)p.VUID; } catch { return 0; } }

        // ------------------------------------------------------------------ designer: the selected crew seat
        static CrewSeatEditor _seatEditor;
        /// <summary>The crew seat whose editor is open (the editor's GUI isn't drawn every frame: it stays until the editor is released).</summary>
        public static CrewSeat SelectedSeat
        {
            get
            {
                if (_seatEditor == null) return null;
                try { if (!_seatEditor.IsValid()) { _seatEditor = null; return null; } var c = _seatEditor.Component; if (c == null || !c.gameObject.activeInHierarchy) return null; return c; }
                catch { _seatEditor = null; return null; }
            }
        }
        static void SeatRelease(CrewSeatEditor __instance) { if (_seatEditor != null && __instance.Pointer == _seatEditor.Pointer) _seatEditor = null; }

        /// <summary>The crew member in this seat has no job (no role / nothing to operate): only those can be extra loaders.</summary>
        public static bool HasNoJob(CrewSeat seat) => HasNoJob(seat?.operatorDefinition);
        public static bool HasNoJob(OperatorSeat op)
        {
            if (op == null) return false;
            try
            {
                var req = op.RequestedOperateTargetIDs; if (req != null && req.Length > 0) return false;
                var asg = op.operateTargets; if (asg != null && asg.Length > 0) return false;
                return true;
            }
            catch { return false; }
        }

        public static void Patch(Harmony h)
        {
            int n = 0;
            try
            {
                foreach (var m in typeof(CrewSeatEditor).GetMethods())
                {
                    if (m.DeclaringType != typeof(CrewSeatEditor)) continue;
                    if (m.Name == "OnGUI" || m.Name == "DrawGizmos") { h.Patch(m, postfix: new HarmonyMethod(typeof(Loaders), nameof(SeatGui))); n++; }
                    else if (m.Name == "VehicleDesignRelease") { h.Patch(m, postfix: new HarmonyMethod(typeof(Loaders), nameof(SeatRelease))); n++; }
                }
            }
            catch (Exception e) { MPLog.Debug("loaders seat hook: " + e.Message); }
            try
            {
                var lt = typeof(Sprocket.Vehicles.Weapons.LoadTask);
                foreach (var m in lt.GetMethods())
                    if (m.Name == "Update" && m.DeclaringType == lt && m.GetParameters().Length == 4) { h.Patch(m, prefix: new HarmonyMethod(typeof(Loaders), nameof(LoadUpdatePrefix))); n++; }
            }
            catch (Exception e) { MPLog.Warn("Extra loaders: reload hook failed: " + e.Message); }
            MPLog.Info($"Extra loaders: {n} hooks");
        }
        static void SeatGui(CrewSeatEditor __instance) { if (_seatEditor == null || _seatEditor.Pointer != __instance.Pointer) _seatEditor = __instance; }

        /// <summary>The designer vehicle's guns: (VUID, label), in a stable order.</summary>
        // ------------------------------------------------------------------ reach
        /// <summary>
        /// Can this crew member load this gun, by the game's own rule? The game checks a crew member's arms against what
        /// they operate (CrewSeat.ValidateOperationDistanceLimits). The seat's arms (free: an extra loader has no job)
        /// are pointed at the gun's breech loading point just long enough for the game to check, then cleared, and the
        /// breech's own assignment is put back as it was. 1 = in reach, 0 = out of reach, -1 = couldn't tell (allowed).
        /// With the game's "unlimited operate distance" setting there is no limit, as for its own crew.
        /// </summary>
        public static int Reach(CrewSeat cs, OperatorSeat op, Cannon gun)
        {
            if (cs == null || op == null || gun == null) return -1;
            try { if (!Sprocket.Vehicles.VehicleRules.OperateDistanceLimitsEnabled) return 1; } catch { }
            VehicleOperable target = null;
            try { var br = gun.GetComponentInChildren<CannonBreech>(true); if (br != null) target = br.loaderOperable ?? br.Operable; } catch { }
            if (target == null) return -1;
            OperatorSeat.Manipulator la = null, ra = null;
            try { la = op.LeftArm?.TryCast<OperatorSeat.Manipulator>(); ra = op.RightArm?.TryCast<OperatorSeat.Manipulator>(); } catch { }
            if (la == null || ra == null) return -1;
            try { if ((la.targets?.Count ?? 0) > 0 || (ra.targets?.Count ?? 0) > 0) return -1; } catch { return -1; }   // arms in use: can't test without disturbing them
            OperatorSeat prevOp = null; byte prevCount = 0, prevRoles = 0;
            try { prevOp = target.AssignedOperator; prevCount = target.AssignCount; prevRoles = target.assignedRoleCount; } catch { }
            try
            {
                la.Assign(target); ra.Assign(target);
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<IInteractor>(2);
                arr[0] = op.LeftArm; arr[1] = op.RightArm;
                var issues = cs.ValidateOperationDistanceLimits(arr);
                const CrewSeatOperateValidationIssues arms = CrewSeatOperateValidationIssues.LeftArmOutOfRange | CrewSeatOperateValidationIssues.RightArmOutOfRange;
                return (issues & arms) != 0 ? 0 : 1;
            }
            catch (Exception e) { if (!_reachWarned) { _reachWarned = true; MPLog.Warn("Extra loaders: reach check: " + e.Message); } return -1; }
            finally
            {
                try { la.ClearAssignments(); ra.ClearAssignments(); } catch { }
                try { target.AssignedOperator = prevOp; target.AssignCount = prevCount; target.assignedRoleCount = prevRoles; } catch { }
            }
        }
        static bool _reachWarned;
        /// <summary>The designer's crew seat and gun with this VUID.</summary>
        static readonly Dictionary<(IntPtr, int), (int r, float at)> _dReach = new Dictionary<(IntPtr, int), (int, float)>();
        public static int DesignerReach(CrewSeat seat, int gunVuid)
        {
            if (seat == null) return -1;
            var key = (seat.Pointer, gunVuid);
            if (_dReach.TryGetValue(key, out var c0) && Time.realtimeSinceStartup - c0.at < 2f) return c0.r;   // (checked every 2 s at most per seat and gun)
            int res = DesignerReachNow(seat, gunVuid);
            if (_dReach.Count > 200) _dReach.Clear();
            _dReach[key] = (res, Time.realtimeSinceStartup);
            return res;
        }
        static int DesignerReachNow(CrewSeat seat, int gunVuid)
        {
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Cannon>()) if (VuidOf(c) == gunVuid) return Reach(seat, seat?.operatorDefinition, c);
            }
            catch { }
            return -1;
        }

        public static List<(int vuid, string label)> DesignerGuns()
        {
            var l = new List<(int, string)>();
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Cannon>())
                {
                    int v = VuidOf(c); if (v == 0) continue;
                    string cal = ""; try { var bp = c.Blueprint; string nm = bp.Name; cal = string.IsNullOrWhiteSpace(nm) ? $"{bp.Caliber:0} mm" : $"{nm.Trim()}, {bp.Caliber:0} mm"; } catch { }
                    l.Add((v, cal));
                }
            }
            catch { }
            l.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            for (int i = 0; i < l.Count; i++) l[i] = (l[i].Item1, $"{i + 1}. " + (l[i].Item2 != "" ? l[i].Item2 : "gun"));
            return l;
        }

        // ------------------------------------------------------------------ battle
        static readonly Dictionary<IntPtr, float> _mult = new Dictionary<IntPtr, float>();   // load target (weapon / cannon / breech) -> multiplier
        static readonly Dictionary<IntPtr, int> _alive = new Dictionary<IntPtr, int>();
        static float _nextCheck; static IntPtr _tank; static string _status = "";
        static bool _argsLogged; static readonly HashSet<IntPtr> _unknown = new HashSet<IntPtr>();
        static readonly List<(Cannon c, int vuid, List<IntPtr> keys)> _guns = new List<(Cannon, int, List<IntPtr>)>();
        static float _nextDiag; static int _diagLines;
        static readonly Dictionary<(int seat, int gun), int> _reach = new Dictionary<(int, int), int>();
        public static string Status => _status;

        /// <summary>Every frame (BattleManager): who of the assigned loaders is alive, twice a second.</summary>
        public static void Tick(VehicleRig local)
        {
            if (local?.GO == null || !Plugin.LoadersEnabled.Value) { if (_mult.Count > 0) { _mult.Clear(); _status = ""; } _tank = IntPtr.Zero; _guns.Clear(); return; }
            if (Time.time < _nextCheck && local.GO.Pointer == _tank) return;
            _nextCheck = Time.time + 0.5f;
            bool first = local.GO.Pointer != _tank; _tank = local.GO.Pointer;
            _mult.Clear();
            string design = Shells.ShellStore.DesignNameOf(local.GO) ?? local.BaseName;
            var map = Get(design);
            if (map == null || map.Count == 0) { _status = ""; return; }
            // seats by VUID -> alive
            var seatAlive = new Dictionary<int, bool>();
            try
            {
                var seats = local.CrewModule?.seats;
                if (seats != null)
                    for (int i = 0; i < seats.Length; i++)
                    {
                        var s = seats[i]; if (s == null) continue;
                        int v = VuidOf(s.Component); if (v == 0) continue;
                        float hf = 0f; try { var b = s.ActiveBehaviour; hf = b != null ? b.HealthFraction : 0f; } catch { }
                        seatAlive[v] = hf > 0.01f && HasNoJob(s);   // a seat given a job since doesn't count
                    }
            }
            catch (Exception e) { MPLog.Debug("loaders seats: " + e.Message); }
            var sb = new System.Text.StringBuilder();
            // the guns and their load targets, found once per tank (searching a huge tank twice a second stuttered)
            if (first || _guns.Count == 0)
            {
                _guns.Clear();
                foreach (var c in local.GO.GetComponentsInChildren<Cannon>(true))
                {
                    int v = VuidOf(c); if (v == 0) continue;
                    var keys = new List<IntPtr> { c.Pointer };
                    try { var w = c.Weapon; if (w != null) keys.Add(w.Pointer); } catch { }
                    try { foreach (var br in c.GetComponentsInChildren<CannonBreech>(true)) keys.Add(br.Pointer); } catch { }
                    _guns.Add((c, v, keys));
                }
            }
            // which assigned seats can reach their gun's breech (the game's arm reach), worked out once per tank
            if (first) _reach.Clear();
            foreach (var (c, v, keys) in _guns)
            {
                if (c == null || !map.TryGetValue(v, out var assigned)) continue;
                foreach (var sv in assigned)
                {
                    if (_reach.ContainsKey((sv, v))) continue;
                    int r = -1;
                    try
                    {
                        var seats = local.CrewModule?.seats;
                        if (seats != null) for (int i = 0; i < seats.Length; i++) { var st = seats[i]; if (st != null && VuidOf(st.Component) == sv) { r = Reach(st.Component?.TryCast<CrewSeat>(), st, c); break; } }
                    }
                    catch { }
                    _reach[(sv, v)] = r;
                    if (r == 0) MPLog.Info($"Extra loaders: seat {sv} is out of reach of gun {v}'s breech: not counted");
                }
            }
            foreach (var (c, v, keys) in _guns)
            {
                if (c == null || !map.TryGetValue(v, out var assigned)) continue;
                int alive = assigned.Count(s => seatAlive.TryGetValue(s, out var a) && a && (!_reach.TryGetValue((s, v), out var rr) || rr != 0));
                float m = Multiplier(1 + alive);
                foreach (var k in keys) { _mult[k] = m; _vuidOfTarget[k] = v; }
                if (!_alive.TryGetValue(c.Pointer, out int was) || was != alive)
                {
                    if (!first && _alive.ContainsKey(c.Pointer)) MPLog.Info($"Extra loaders: gun {v} now has {1 + alive} loaders (x{m:0.##} reload)");
                    _alive[c.Pointer] = alive;
                }
                sb.Append(sb.Length > 0 ? ", " : "").Append($"{1 + alive}/{1 + assigned.Count} loaders x{m:0.##}");
            }
            _status = sb.ToString();
            if (first) MPLog.Info($"Extra loaders for '{design}': {_status}");
        }

        /// <summary>Scales the load task's time step by its gun's multiplier.</summary>
        static void LoadUpdatePrefix(Sprocket.Vehicles.Weapons.LoadTask __instance, ref float __0, ref float __1, ref float __2, ref float __3)
        {
            if (_mult.Count == 0) return;
            IntPtr key = IntPtr.Zero;
            try { var t = __instance.target; key = t != null ? t.Pointer : IntPtr.Zero; } catch { return; }
            if (key == IntPtr.Zero) return;
            if (!_mult.TryGetValue(key, out float m))
            {
                if (_unknown.Add(key) && _unknown.Count < 8)
                {
                    string tn = "?"; try { tn = new Il2CppSystem.Object(key).GetIl2CppType().FullName; } catch { }
                    MPLog.Debug($"Extra loaders: load task for an unknown target ({tn})");
                }
                return;
            }
            // Update(travel speed, manipulation speed, ?, time step). Measured (6.14.7 log): the game's countdown counts
            // the pick-up and put-in steps at one second per second whatever the manipulation speed, and only the
            // carrying by the travel speed. So: the travel speed is raised, and the pick-up / put-in TIMES are shortened
            // (the manipulation speed is left alone): the countdown is then right in every step. The time step is never
            // touched, and nothing happens while no loader contributes yet (all zero, right after spawning).
            var key2 = __instance.Pointer;
            _times.TryGetValue(key2, out var st);
            try
            {
                float pt = __instance.pickupTime, dt0 = __instance.dropoffTime;
                // a time the game (re)set itself (each reload) is the new base
                if (st.m <= 0f || Math.Abs(pt - st.bp / st.m) > 1e-3f) st.bp = pt;
                if (st.m <= 0f || Math.Abs(dt0 - st.bd / st.m) > 1e-3f) st.bd = dt0;
                float mm = Mathf.Approximately(m, 1f) ? 1f : m;
                if (st.m != mm || Math.Abs(pt - st.bp / mm) > 1e-3f) __instance.pickupTime = st.bp / mm;
                if (st.m != mm || Math.Abs(dt0 - st.bd / mm) > 1e-3f) __instance.dropoffTime = st.bd / mm;
                st.m = mm; _times[key2] = st;
            }
            catch (Exception e) { if (!_timesWarned) { _timesWarned = true; MPLog.Warn("Extra loaders: load times: " + e.Message); } }
            if (Plugin.LoadersDiag.Value && _diagLines < 600)
            {   // each gun's reload: every change of step, and every 3 s
                try
                {
                    var t = __instance; string stp = $"{t.state}/{t.action}";
                    _diag.TryGetValue(key2, out var dg);
                    if (dg.step != stp || Time.time >= dg.next)
                    {
                        dg.step = stp; dg.next = Time.time + 3f; _diag[key2] = dg; _diagLines++;
                        _vuidOfTarget.TryGetValue(key, out int gv);
                        MPLog.Info($"Reload gun {gv} x{m:0.##} t={Time.time:0.0}: {stp}, countdown {t.RemainingTime:0.0} s, args ({__0:0.###}, {__1:0.###}, {__2:0.###}), pickup {t.pickupCurrentTime:0.0}/{t.pickupTime:0.0}, travel {t.travelProgress:0.00}/{t.totalDistance:0.00}, dropoff {t.dropoffCurrentTime:0.0}/{t.dropoffTime:0.0}, prepare {t.prepareCurrentTime:0.0}/{t.prepareTime:0.0}, speeds {t.manipulationSpeed:0.###}/{t.travelSpeed:0.###}");
                    }
                }
                catch (Exception e) { MPLog.Debug("reload diag: " + e.Message); }
            }
            if (Mathf.Approximately(m, 1f)) return;
            if (__0 <= 0f && __1 <= 0f && __2 <= 0f) return;
            if (!_argsLogged) { _argsLogged = true; MPLog.Info($"Extra loaders: load task update ({__0:0.####}, {__1:0.####}, {__2:0.####}, dt {__3:0.####}) x{m:0.##}"); }
            __0 *= m;   // carrying
        }
        struct Times { public float bp, bd, m; }
        struct Diag { public string step; public float next; }
        static readonly Dictionary<IntPtr, Times> _times = new Dictionary<IntPtr, Times>();
        static readonly Dictionary<IntPtr, Diag> _diag = new Dictionary<IntPtr, Diag>();
        static readonly Dictionary<IntPtr, int> _vuidOfTarget = new Dictionary<IntPtr, int>();
        static bool _timesWarned;
    }
}

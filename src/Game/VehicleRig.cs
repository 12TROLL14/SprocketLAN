using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Control;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.WeaponFramework;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Everything the mod needs about one spawned vehicle, resolved once:
    /// rigidbody, crew tasks (Driver / GunLayer[] / Gunner[]), weapons and health pools.
    /// Index order of GunLayers/Gunners/Weapons/Pools comes from the blueprint assembly, so the same
    /// blueprint gives the same order on every machine — that's what lets us address turrets/guns by index.
    /// </summary>
    public class VehicleRig
    {
        public VehicleBehaviour VB;
        public IntPtr Ptr;
        public GameObject GO;
        public Rigidbody Body;
        public string RawName = "";
        public string BaseName = "";
        public TeamID Team;
        public ControlType Control;

        public Driver Driver;
        public readonly List<GunLayer> GunLayers = new List<GunLayer>();
        public readonly List<Gunner> Gunners = new List<Gunner>();
        public readonly List<WeaponBehaviour> Weapons = new List<WeaponBehaviour>();
        /// <summary>Shell type each weapon had loaded when first seen (used to re-prime puppet guns; AI-less puppets never reload).</summary>
        public readonly Dictionary<int, Sprocket.DamageModelling.ProjectileTypeID> NativeShellTypes = new Dictionary<int, Sprocket.DamageModelling.ProjectileTypeID>();
        public readonly List<HealthPool> Pools = new List<HealthPool>();
        /// <summary>Component owning each pool (same index as Pools; null for owner-less pools). Used by the pen cam.</summary>
        public readonly List<VehicleComponent> PoolOwners = new List<VehicleComponent>();
        /// <summary>The transform each GunLayer actually rotates (turret ring, or laying-drive trunnions). Same index as GunLayers.</summary>
        public readonly List<Transform> AimXforms = new List<Transform>();
        /// <summary>Pointers of the TurretBehaviour / LayingDriveBehaviour objects (their motors are frozen on puppets).</summary>
        public readonly List<IntPtr> AimBehaviourPtrs = new List<IntPtr>();
        public readonly List<IDurable> CrewPools = new List<IDurable>();
        public readonly List<IDurable> AmmoPools = new List<IDurable>();
        /// <summary>Hash of the canonical pool layout; two machines only exchange health if their hashes match.</summary>
        public int PoolLayoutHash;
        public readonly HashSet<IntPtr> CrewPoolPtrs = new HashSet<IntPtr>();
        public Sprocket.Vehicles.Fires.FireControlVehicleModule FireModule;
        public float NextFireSync;
        public Sprocket.Vehicles.AmmunitionStorage.FlameoutControlVehicleModule FlameoutModule;
        public Sprocket.Vehicles.PartDecoupling.CoupledPartRegister PartRegister;
        public int BindCount;
        public VehicleCrewWorkspaceModule CrewModule;

        /// <summary>Pristine attachment of every detachable part (turrets blown off by ammo detonations), captured while the vehicle is intact.</summary>
        public class PartSnapshot { public Sprocket.Vehicles.PartDecoupling.PartDecoupler Part; public Transform Parent; public Vector3 LocalPos; public Quaternion LocalRot; public string Name; public bool HadBody; public bool BodyKinematic; }
        public readonly List<PartSnapshot> Parts = new List<PartSnapshot>();
        /// <summary>Everything that was active / rendering while the vehicle was intact (decouples and culling switch these off).</summary>
        public readonly List<GameObject> ActiveObjects = new List<GameObject>();
        public readonly List<Renderer> EnabledRenderers = new List<Renderer>();
        public class HatchSnapshot { public Hatch Hatch; public Vector3 LocalPos; public Quaternion LocalRot; }
        public readonly List<HatchSnapshot> Hatches = new List<HatchSnapshot>();

        public void CaptureParts()
        {
            if (Parts.Count > 0) return;
            try
            {
                var arr = GO.GetComponentsInChildren<Sprocket.Vehicles.PartDecoupling.PartDecoupler>(true);
                if (arr == null) return;
                foreach (var d in arr)
                {
                    if (d == null) continue;
                    bool dec = false; try { dec = d.decoupled; } catch { }
                    if (dec) continue;
                    var t = d.transform;
                    bool had = false, kin = true;
                    try { var rb = d.partRigidbody; if (rb != null) { had = true; kin = rb.isKinematic; } } catch { }
                    Parts.Add(new PartSnapshot { Part = d, Parent = t.parent, LocalPos = t.localPosition, LocalRot = t.localRotation, Name = t.name, HadBody = had, BodyKinematic = kin });
                }
                if (Parts.Count > 0) MPLog.Info($"{RawName}: captured {Parts.Count} detachable parts ({string.Join(", ", Parts.ConvertAll(x => x.Name))})");
                var xs = GO.GetComponentsInChildren<Transform>(true);
                foreach (var x in xs) if (x != null && x.gameObject.activeSelf) ActiveObjects.Add(x.gameObject);
                foreach (var h in GO.GetComponentsInChildren<Hatch>(true))
                {
                    if (h == null) continue;
                    var hs = new HatchSnapshot { Hatch = h };
                    try { var m = h.DoorModel; if (m != null) { hs.LocalPos = m.ModelLocalPosition; hs.LocalRot = m.ModelLocalRotation; } } catch { }
                    Hatches.Add(hs);
                }
                var rs = GO.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rs) if (r != null && r.enabled) EnabledRenderers.Add(r);
            }
            catch (Exception e) { MPLog.Warn($"CaptureParts {RawName}: {e.Message}"); }
        }          // how many times a remote player has been bound to this puppet
        public HealthRegister HealthReg;
        public VehicleHealthRegister VehicleHealthReg;
        public bool CrewResolved;

        // puppet bookkeeping
        public Vector3 InitialPos; public Quaternion InitialRot;
        public bool AINeutralized;
        public int NeutralizeAttempts;
        public byte OwnerId = 255;     // remote player controlling this puppet (255 = none)

        /// <summary>"Panther (1)" -> "Panther" (the game appends an instance number to spawned vehicles).</summary>
        public static string StripName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            n = n.Trim();
            if (n.EndsWith(")"))
            {
                int open = n.LastIndexOf('(');
                if (open > 0)
                {
                    var inner = n.Substring(open + 1, n.Length - open - 2);
                    bool digits = inner.Length > 0;
                    foreach (var c in inner) if (!char.IsDigit(c)) digits = false;
                    if (digits) n = n.Substring(0, open).Trim();
                }
            }
            if (n.EndsWith("(Clone)")) n = n.Substring(0, n.Length - 7).Trim();
            return n;
        }

        public VehicleRig(VehicleBehaviour vb)
        {
            VB = vb; Ptr = vb.Pointer; GO = vb.gameObject;
            RawName = GO.name; BaseName = StripName(RawName);
            Refresh();
            InitialPos = Body != null ? Body.position : GO.transform.position;
            InitialRot = Body != null ? Body.rotation : GO.transform.rotation;
        }

        /// <summary>Designer/sandbox tank (no VehicleBehaviour): geometry + modules only, no health pools.</summary>
        public VehicleRig(GameObject root)
        {
            GO = root; Ptr = root.Pointer; RawName = root.name; BaseName = StripName(RawName);
            try
            {
                foreach (var vc in root.GetComponentsInChildren<VehicleComponent>(true))
                {
                    if (vc == null) continue;
                    bool module = vc.TryCast<CrewSeat>() != null;
                    if (!module)
                    {
                        var groups = vc.models;
                        if (groups != null)
                            for (int i = 0; i < groups.Count; i++)
                                if (groups[i] != null && ((int)groups[i].partType & (4 | 16 | 32 | 64 | 128 | 1024 | 2)) != 0) { module = true; break; }
                    }
                    if (module) PoolOwners.Add(vc);
                }
            }
            catch (Exception e) { MPLog.Debug("sandbox rig: " + e.Message); }
        }

        public bool AmmoBoosted;

        /// <summary>Ammo racks are made sturdier (Shells.AmmoRackHealthMultiplier): max and current health scaled once.</summary>
        public void BoostAmmo(float mult)
        {
            if (AmmoBoosted || mult <= 0f || Math.Abs(mult - 1f) < 0.001f || AmmoPools.Count == 0) return;
            AmmoBoosted = true;
            int n = 0;
            var ammo = new HashSet<IntPtr>();
            foreach (var a in AmmoPools) ammo.Add(((Il2CppObjectBase)(object)a).Pointer);
            foreach (var p in Pools)
            {
                if (p == null || !ammo.Contains(p.Pointer)) continue;
                try { float m = p.max * mult; p.SetMaxHealth(m); p.current = m; p.MarkHealthModified(); p.NotifyHealthChanges(); n++; } catch (Exception e) { MPLog.Debug("ammo boost: " + e.Message); }
            }
            MPLog.Info($"Ammo racks x{mult:F1} health on {RawName}: {n} pools");
        }

        public bool Alive => VB != null && !VB.WasCollected && GO != null && !GO.WasCollected;

        public void Refresh()
        {
            try { Body = VB.Rigidbody; } catch { }
            try { Team = VB.ID.TeamID; } catch { }
            try { Control = VB.ControlType; } catch { }
        }

        /// <summary>Find crew tasks, weapons and health pools. Safe to call repeatedly until CrewResolved.</summary>
        public void ResolveCrew()
        {
            if (CrewResolved) return;
            Driver = null; GunLayers.Clear(); Gunners.Clear(); Weapons.Clear(); Pools.Clear();
            AimXforms.Clear(); AimBehaviourPtrs.Clear(); CrewPools.Clear(); AmmoPools.Clear();
            try
            {
                var modules = VB.modules;
                if (modules == null) return;
                for (int i = 0; i < modules.Count; i++)
                {
                    var m = modules[i];
                    if (m == null) continue;
                    var fire = m.TryCast<Sprocket.Vehicles.Fires.FireControlVehicleModule>();
                    if (fire != null) { FireModule = fire; continue; }
                    var flame = m.TryCast<Sprocket.Vehicles.AmmunitionStorage.FlameoutControlVehicleModule>();
                    if (flame != null) { FlameoutModule = flame; continue; }
                    var parts = m.TryCast<Sprocket.Vehicles.PartDecoupling.CoupledPartRegister>();
                    if (parts != null) { PartRegister = parts; continue; }
                    var crew = m.TryCast<VehicleCrewWorkspaceModule>();
                    if (crew == null) continue;
                    CrewModule = crew;
                    var tasks = crew.tasks;
                    if (tasks == null) continue;
                    for (int t = 0; t < tasks.Length; t++)
                    {
                        var it = tasks[t];
                        var task = it?.internalTask;
                        if (task == null) continue;
                        var d = task.TryCast<Driver>(); if (d != null) { if (Driver == null) Driver = d; continue; }
                        var gl = task.TryCast<GunLayer>(); if (gl != null) { GunLayers.Add(gl); continue; }
                        var g = task.TryCast<Gunner>(); if (g != null)
                        {
                            Gunners.Add(g);
                            WeaponBehaviour wb = null;
                            try { wb = g.weapon?.TryCast<WeaponBehaviour>(); } catch { }
                            Weapons.Add(wb);
                            try
                            {
                                if (wb != null && !NativeShellTypes.ContainsKey(Weapons.Count - 1))
                                {
                                    var lt = wb.LoadedProjectileTypeID;
                                    if (lt.Value > 0) NativeShellTypes[Weapons.Count - 1] = lt;
                                }
                            }
                            catch { }
                            continue;
                        }
                    }
                }
            }
            catch (Exception e) { MPLog.Warn($"ResolveCrew {RawName}: {e.Message}"); }

            try
            {
                VehicleHealthReg = VB.healthRegister;
                HealthReg = VehicleHealthReg?.register;
                var list = HealthReg?.register;
                if (list != null) for (int i = 0; i < list.Count; i++) Pools.Add(list[i]);
            }
            catch (Exception e) { MPLog.Warn($"Health pools {RawName}: {e.Message}"); }

            // what each GunLayer physically rotates
            foreach (var gl in GunLayers)
            {
                Transform xf = null; IntPtr bp = IntPtr.Zero;
                try
                {
                    var target = gl.ControlTarget;
                    var tb = target?.TryCast<Sprocket.Vehicles.Weapons.TurretBehaviour>();
                    if (tb != null) { xf = tb.transform; bp = tb.Pointer; }
                    else
                    {
                        var ld = target?.TryCast<Sprocket.Vehicles.Weapons.LayingDriveBehaviour>();
                        if (ld != null) { xf = ld.trunnions; bp = ld.Pointer; }
                    }
                }
                catch (Exception e) { MPLog.Warn($"aimable resolve {RawName}: {e.Message}"); }
                AimXforms.Add(xf); AimBehaviourPtrs.Add(bp);
            }

            // crew seats and ammo racks (destruction rules: no crew left, or an ammo rack destroyed)
            // + canonical pool order: pools are created in an order that is NOT guaranteed to match between two
            //   machines, so health is exchanged in an order keyed on the owning component's hierarchy path.
            CrewPoolPtrs.Clear();
            var keyed = new List<(string key, IntPtr ptr, VehicleComponent owner)>();
            try
            {
                var entries = VehicleHealthReg?.entries;
                var seen = new Dictionary<string, int>();
                if (entries != null)
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        var owner = e.Owner; var hp = e.HealthPool;
                        if (owner == null || hp == null) continue;
                        if (owner.TryCast<CrewSeat>() != null) { CrewPools.Add(hp); CrewPoolPtrs.Add(hp.Pointer); }
                        else if (owner.TryCast<Sprocket.Vehicles.AmmunitionStorage.AmmoRack>() != null) AmmoPools.Add(hp);
                        string path = PathOf(owner.transform) + "|" + TypeName(owner);
                        seen.TryGetValue(path, out int n); seen[path] = n + 1;
                        keyed.Add((path + "#" + n, hp.Pointer, owner));
                    }
            }
            catch (Exception e) { MPLog.Warn($"crew/ammo pools {RawName}: {e.Message}"); }
            try
            {
                keyed.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
                var byPtr = new Dictionary<IntPtr, HealthPool>();
                foreach (var p in Pools) if (p != null) byPtr[p.Pointer] = p;
                var ordered = new List<HealthPool>();
                var owners = new List<VehicleComponent>();
                var used = new HashSet<IntPtr>();
                int hash = 17;
                foreach (var k in keyed)
                    if (byPtr.TryGetValue(k.ptr, out var hp) && used.Add(k.ptr)) { ordered.Add(hp); owners.Add(k.owner); hash = hash * 31 + StableHash(k.key); }
                foreach (var p in Pools) if (p != null && used.Add(p.Pointer)) { ordered.Add(p); owners.Add(null); }   // pools without an owning component, creation order
                hash = hash * 31 + ordered.Count;
                Pools.Clear(); Pools.AddRange(ordered);
                PoolOwners.Clear(); PoolOwners.AddRange(owners);
                PoolLayoutHash = hash;
            }
            catch (Exception e) { MPLog.Warn($"pool ordering {RawName}: {e.Message}"); }

            CrewResolved = Driver != null || GunLayers.Count > 0 || Gunners.Count > 0;
            if (CrewResolved) CaptureParts();
        }

        /// <summary>Find this vehicle's CommanderAI in the global AI register and stop it for good.</summary>
        public bool NeutralizeAI()
        {
            if (AINeutralized) return true;
            NeutralizeAttempts++;
            try
            {
                var reg = AIRegister.register;
                if (reg == null) return false;
                CommanderAI found = null;
                for (int i = 0; i < reg.Count; i++)
                {
                    var ai = reg[i];
                    var cmd = ai?.TryCast<CommanderAI>();
                    if (cmd == null) continue;
                    GameObject go = null;
                    try { go = cmd.controlTarget?.gameObject; } catch { }
                    if (go == null) { try { go = cmd.observerTransform?.gameObject; } catch { } }
                    if (go != null && (go.Pointer == GO.Pointer || go.transform.IsChildOf(GO.transform))) { found = cmd; break; }
                }
                if (found == null) return false;
                found.Enabled = false;
                try { found.driver?.ClearTasks(); } catch { }
                try { var gls = found.gunLayers; if (gls != null) for (int i = 0; i < gls.Length; i++) gls[i]?.ClearTasks(); } catch { }
                try { var gs = found.gunners; if (gs != null) for (int i = 0; i < gs.Length; i++) gs[i]?.ClearTask(); } catch { }
                bool removed = AIRegister.Deregister(found);
                AINeutralized = true;
                MPLog.Info($"AI neutralized on '{RawName}' (deregistered={removed})");
                return true;
            }
            catch (Exception e) { MPLog.Warn($"NeutralizeAI {RawName}: {e.Message}"); return false; }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"'{RawName}' base='{BaseName}' team={Team} control={Control} ptr=0x{Ptr.ToInt64():X} ");
            sb.Append($"driver={(Driver != null)} gunLayers={GunLayers.Count} aimXforms={AimXforms.FindAll(x => x != null).Count} gunners={Gunners.Count} weapons={Weapons.Count} pools={Pools.Count} crew={CrewPools.Count} ammo={AmmoPools.Count} layout={PoolLayoutHash} fire={(FireModule != null)} ");
            if (Body != null) sb.Append($"pos={Body.position} mass={Body.mass}");
            return sb.ToString();
        }

        /// <summary>Crew still able to act: seats whose occupant exists and has health left. -1 if unknown.</summary>
        public int AliveCrewCount(out int seatCount)
        {
            seatCount = 0;
            int alive = 0;
            try
            {
                var seats = CrewModule?.seats;
                if (seats == null) return -1;
                seatCount = seats.Length;
                for (int i = 0; i < seats.Length; i++)
                {
                    var b = seats[i]?.ActiveBehaviour;
                    if (b == null) continue;
                    float hf = 0f; try { hf = b.HealthFraction; } catch { }
                    if (hf > 0.01f) alive++;
                }
            }
            catch { return -1; }
            return alive;
        }

        string PathOf(Transform t)
        {
            var sb = new StringBuilder();
            var root = GO.transform;
            int guard = 0;
            while (t != null && t.Pointer != root.Pointer && guard++ < 64)
            {
                sb.Insert(0, "/" + t.name + ":" + t.GetSiblingIndex());
                t = t.parent;
            }
            return sb.ToString();
        }

        static int StableHash(string s) { unchecked { int h = 23; foreach (var c in s) h = h * 31 + c; return h; } }

        public static string TypeName(Il2CppObjectBase o)
        {
            try { return o == null ? "null" : (o.TryCast<Il2CppSystem.Object>()?.GetIl2CppType().FullName ?? "?"); } catch { return "?"; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DamageModelling;
using Sprocket.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Control;
using Sprocket.WeaponFramework;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Watches the custom battle, finds the local player's tank and the enemy ("AI team") tanks, turns the enemy
    /// tanks into puppets of remote players and keeps them in sync.
    ///
    /// Model:
    ///   * every player puts THEIR tank on the player team and the other players' tanks on the enemy (AI) team
    ///   * locally, each enemy tank gets its CommanderAI removed; it's then driven through the same crew tasks
    ///     the game's own AI/player use: Driver.Solution, GunLayer.AimAtDirection, Weapon.FireInternal
    ///   * position/rotation are softly corrected toward the owner's state (snap when far off)
    ///   * shots are replicated with the exact launch state (origin, direction, velocity, projectile GUID)
    ///   * damage is victim-authoritative: puppets ignore local damage, owners replicate their health pools
    /// </summary>
    public class BattleManager
    {
        public static BattleManager Instance;

        public VehicleRig Local;
        public bool InBattle => Local != null && Local.Alive;
        public int Epoch;
        public string LocalVehicleName => Local?.BaseName ?? "";
        public bool SpawnApplied;
        public string LastEvent = "";

        readonly Dictionary<IntPtr, VehicleRig> _rigs = new Dictionary<IntPtr, VehicleRig>();
        public readonly Dictionary<byte, VehicleRig> PuppetByPlayer = new Dictionary<byte, VehicleRig>();
        readonly Dictionary<byte, VehicleStateMsg> _latest = new Dictionary<byte, VehicleStateMsg>();
        readonly Dictionary<byte, HealthMsg> _pendingHealth = new Dictionary<byte, HealthMsg>();
        readonly HashSet<IntPtr> _parked = new HashSet<IntPtr>();

        float _nextScan, _nextSend, _nextHealth, _lastHealthForce, _nextStatus;
        uint _seq;
        float[] _lastSentHealth;
        bool _autoDumped;
        public int ShotsSent, ShotsReceived, StatesReceived;
        readonly HashSet<IntPtr> _healthMismatchWarned = new HashSet<IntPtr>();

        // ---- destruction / kill feed
        public struct FeedEntry { public string Text; public float Time; }
        public readonly List<FeedEntry> KillFeed = new List<FeedEntry>();
        bool _localDestroyed, _sawCrewAlive;
        float _nextCrewLog;
        public bool LocalDestroyed => _localDestroyed && InBattle;
        public string DeathReason = "", DeathKiller = "";
        float _lastLocalHealthSum = -1f;
        byte _lastAttacker = 255; float _lastAttackerTime = -999f;

        public BattleManager() { Instance = this; }

        Session S => Session.Current;
        bool Networked => S != null && S.Active && (S.IsHost || S.LocalId != 255);

        public IEnumerable<VehicleRig> Rigs => _rigs.Values;

        // =====================================================================================  per-frame
        public void Update()
        {
            System.Threading.Interlocked.Increment(ref MPLog.Beat); MPLog.Stage = "battle update";
            Stabilizer.Tick(InBattle ? Local : null);
            EngineRevive.Tick(InBattle ? Local : null);
            try { AutoCannon.Tick(InBattle ? Local : null); } catch { }
            float now = Time.time;
            if (now >= _nextScan) { _nextScan = now + 0.5f; Scan(); }
            if (!InBattle) { RepairEndsAt = -1f; return; }
            try { UpdateRepair(); } catch (Exception e) { MPLog.Warn("Repair: " + e.Message); }

            if (Networked)
            {
                float interval = 1f / Mathf.Clamp(Plugin.SendRate.Value, 5, 60);
                if (now >= _nextSend) { _nextSend = now + interval; SendLocalState(); }
                if (now >= _nextHealth) { _nextHealth = now + 0.25f; SendLocalHealth(false); CheckLocalDestroyed(); }
                ApplyPuppetInputs();
                if (now >= _nextStatus) { _nextStatus = now + 3f; LogStatus(); }
                PenCapture.Update(this, S);
                MPLog.Stage = "game code (outside the mod, after battle update)";
            }
        }

        void LogStatus()
        {
            var sb = new StringBuilder();
            sb.Append($"status local pos={Local.Body?.position} aims=[");
            foreach (var x in Local.AimXforms) { try { sb.Append(x != null ? x.localEulerAngles.ToString() : "null").Append(' '); } catch { } }
            sb.Append(']');
            foreach (var kv in PuppetByPlayer)
            {
                var r = kv.Value; _latest.TryGetValue(kv.Key, out var s);
                sb.Append($" | puppet[{kv.Key}] pos={r.Body?.position}");
                if (s != null) sb.Append($" net={s.Pos} age={Time.time - s.ReceivedAt:F2}s err={(s.Pos - (r.Body != null ? r.Body.position : s.Pos)).magnitude:F2}m drive=({s.Steer:F2},{s.Throttle:F2})");
                sb.Append(" aims=[");
                foreach (var x in r.AimXforms) { try { sb.Append(x != null ? x.localEulerAngles.ToString() : "null").Append(' '); } catch { } }
                sb.Append($"] parked={_parked.Contains(r.Ptr)} blocked={Patches.BlockedDamageEvents}");
            }
            MPLog.File(sb.ToString());
        }

        public void FixedUpdate()
        {
            if (!InBattle || !Networked) return;
            float dt = Time.fixedDeltaTime;
            foreach (var kv in PuppetByPlayer)
            {
                var rig = kv.Value;
                if (rig == null || !rig.Alive || rig.Body == null) continue;
                if (!_latest.TryGetValue(kv.Key, out var s)) continue;
                try { CorrectPose(rig, s, dt); } catch (Exception e) { MPLog.Warn("CorrectPose: " + e.Message); }
            }
        }

        // =====================================================================================  scanning
        void Scan()
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<VehicleBehaviour> arr;
            try { arr = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>(); }
            catch (Exception e) { MPLog.Warn("FindObjectsOfType: " + e.Message); return; }

            var seen = new HashSet<IntPtr>();
            VehicleRig player = null;
            if (arr != null)
            {
                foreach (var vb in arr)
                {
                    if (vb == null) continue;
                    var ptr = vb.Pointer; seen.Add(ptr);
                    if (!_rigs.TryGetValue(ptr, out var rig))
                    {
                        rig = new VehicleRig(vb);
                        _rigs[ptr] = rig;
                        MPLog.Info("Vehicle seen: " + rig.Describe());
                    }
                    rig.Refresh();
                    if (rig.Control == ControlType.Player) player = rig;
                }
            }
            foreach (var dead in _rigs.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var r = _rigs[dead]; _rigs.Remove(dead);
                Patches.UnregisterPuppet(r);
                foreach (var k in PuppetByPlayer.Where(kv => kv.Value == r).Select(kv => kv.Key).ToList()) PuppetByPlayer.Remove(k);
            }

            // ---- local player vehicle / battle epochs
            if (player != null && (Local == null || Local.Ptr != player.Ptr))
            {
                bool fresh = Local == null || !Local.Alive || !seen.Contains(Local.Ptr);
                Local = player;
                Local.ResolveCrew();
                if (fresh)
                {
                    Epoch++;
                    SpawnApplied = false; _lastSentHealth = null; _autoDumped = false;
                    _localDestroyed = false; _lastLocalHealthSum = -1f; _lastAttacker = 255; _sawCrewAlive = false;
                    PuppetByPlayer.Clear(); _latest.Clear(); _parked.Clear();
                    MPLog.Info($"=== Battle entered (epoch {Epoch}). Local tank: {Local.Describe()}");
                    Local.BoostAmmo(Shells.ShellConfig.AmmoRackHealthMultiplier.Value);
                }
                else MPLog.Info("Local control switched to " + Local.RawName);
                SendBattleState();
            }
            else if (Local != null && !seen.Contains(Local.Ptr))
            {
                MPLog.Info("=== Battle left (local tank gone)");
                Local = null; PuppetByPlayer.Clear(); _latest.Clear(); _parked.Clear();
                Patches.ClearPuppets();
                SendBattleState();
            }

            if (Local == null) return;
            if (!Local.CrewResolved) Local.ResolveCrew();

            if (!_autoDumped && Local.CrewResolved) { _autoDumped = true; DumpDiagnostics("auto"); }

            if (!Networked) return;

            // ---- enemy-team vehicles become puppets
            var enemies = _rigs.Values.Where(r => r.Alive && r.Ptr != Local.Ptr && r.Team != Local.Team).OrderBy(r => r.RawName, StringComparer.Ordinal).ThenBy(r => r.Ptr.ToInt64()).ToList();
            foreach (var e in enemies)
            {
                if (!e.CrewResolved) e.ResolveCrew();
                if (!e.AINeutralized && e.NeutralizeAttempts < 240) e.NeutralizeAI();
                Patches.RegisterPuppet(e);
            }

            AssignPuppets(enemies);

            // park puppets whose owner has not sent a state yet (avoids spawn overlaps / idle targets)
            foreach (var e in enemies)
            {
                bool hasState = e.OwnerId != 255 && _latest.ContainsKey(e.OwnerId);
                if (!hasState && !_parked.Contains(e.Ptr)) Park(e);
            }

            // ---- host: tell clients where to spawn (shared coordinate frame)
            if (S.IsHost && Plugin.AlignSpawns.Value)
            {
                foreach (var p in S.Players.Values)
                {
                    if (!p.InBattle || p.SpawnSentEpoch == p.Epoch) continue;
                    if (!PuppetByPlayer.TryGetValue(p.Id, out var pr)) continue;
                    var pos = pr.InitialPos; var rot = pr.InitialRot; byte id = p.Id; int ep = p.Epoch;
                    if (Plugin.SpawnNearHost.Value && Local.Body != null)
                    {
                        // put joiners in a row in front of the host, facing the host, 14 m apart, on the ground
                        float d = Mathf.Max(10f, Plugin.SpawnNearHostDistance.Value);
                        var fwd = Local.Body.rotation * Vector3.forward; fwd.y = 0; fwd.Normalize();
                        var right = new Vector3(fwd.z, 0, -fwd.x);
                        int slot = p.Id - 1; // 0, 1, 2, ...  ->  0, +14, -14, +28, -28 ...
                        float side = ((slot + 1) / 2) * 14f * (slot % 2 == 1 ? 1f : -1f);
                        pos = Local.Body.position + fwd * d + right * side;
                        if (Terrain.GroundHeight(pos, out float gy)) pos.y = gy + 1.5f; else pos.y += 1.5f;
                        rot = Quaternion.LookRotation(-fwd, Vector3.up);
                    }
                    S.SendTo(p.Id, Wire.Build(Msg.SpawnPose, w => { w.Write(id); w.Write(ep); w.W(pos); w.W(rot); }));
                    p.SpawnSentEpoch = p.Epoch;
                    MPLog.Info($"Sent spawn pose to player {p.Id} ({p.Name}): {pos}");
                }
            }

            // pending health updates that arrived before the puppet existed
            foreach (var kv in _pendingHealth.ToList())
                if (PuppetByPlayer.ContainsKey(kv.Key)) { ApplyHealth(kv.Value); _pendingHealth.Remove(kv.Key); }
        }

        void AssignPuppets(List<VehicleRig> enemies)
        {
            // drop assignments whose player left
            foreach (var id in PuppetByPlayer.Keys.ToList())
                if (!S.Players.ContainsKey(id)) { PuppetByPlayer[id].OwnerId = 255; PuppetByPlayer.Remove(id); }

            var free = enemies.Where(e => e.OwnerId == 255 || !PuppetByPlayer.ContainsKey(e.OwnerId)).ToList();
            foreach (var f in free) f.OwnerId = 255;
            var waiting = S.Players.Values.Where(p => !PuppetByPlayer.ContainsKey(p.Id) && !string.IsNullOrEmpty(p.Vehicle)).OrderBy(p => p.Id).ToList();
            if (waiting.Count == 0) return;

            // pass 1: exact name match
            foreach (var p in waiting.ToList())
            {
                var m = free.FirstOrDefault(e => string.Equals(e.BaseName, p.Vehicle, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                Bind(p, m); free.Remove(m); waiting.Remove(p);
            }
            // pass 2: whatever is left (only once the player is actually in battle, to give names a chance to arrive)
            foreach (var p in waiting)
            {
                if (!p.InBattle || free.Count == 0) continue;
                var m = free[0];
                MPLog.Warn($"No enemy tank named '{p.Vehicle}' for player {p.Id} ({p.Name}); using '{m.RawName}' instead. Put the other players' exact designs on the AI team.");
                Bind(p, m); free.Remove(m);
            }
        }

        void Bind(RemotePlayer p, VehicleRig rig)
        {
            rig.OwnerId = p.Id;
            PuppetByPlayer[p.Id] = rig;
            rig.BindCount++;
            PenCamView.Invalidate(rig);
            PenCamView.ForgetVictim(p.Id);   // fresh tank: forget remembered module damage
            rig.BoostAmmo(Shells.ShellConfig.AmmoRackHealthMultiplier.Value);   // same max health as the owner's copy    // the pen cam's cached model of this tank is rebuilt (turret may have popped)
            if (rig.BindCount > 1)
            {
                // the owner reset/respawned but our scene did not: bring the old (possibly wrecked) puppet back to new
                VehicleRestore.Restore(rig, includeCrew: true);
                MPLog.Info($"Puppet {rig.RawName} revived for player {p.Id}'s respawn");
            }
            LastEvent = $"{p.Name} -> {rig.RawName}";
            MPLog.Info($"Puppet bound: player {p.Id} ({p.Name}, '{p.Vehicle}') -> {rig.Describe()}");
            if (rig.GunLayers.Count == 0 && rig.Gunners.Count == 0) rig.ResolveCrew();
            rig.BoostAmmo(Shells.ShellConfig.AmmoRackHealthMultiplier.Value);
        }

        void Park(VehicleRig e)
        {
            if (e.Body == null) return;
            try
            {
                e.Body.isKinematic = true;
                e.Body.position = e.InitialPos + new Vector3(0, 400f, 0);
                _parked.Add(e.Ptr);
                MPLog.Debug("Parked " + e.RawName);
            }
            catch (Exception ex) { MPLog.Warn("Park: " + ex.Message); }
        }

        float _nextOverlapLog;

        void Unpark(VehicleRig e, VehicleStateMsg s)
        {
            if (!_parked.Contains(e.Ptr) || e.Body == null) return;
            // never drop a puppet on top of our own tank (both tanks start at the same spawn until the host's
            // spawn pose arrives) - the overlap makes the physics blow both up
            if (Local?.Body != null && (Local.Body.position - s.Pos).sqrMagnitude < 9f * 9f)
            {
                if (Time.time >= _nextOverlapLog) { _nextOverlapLog = Time.time + 2f; MPLog.Info($"{e.RawName} kept parked: its owner is inside our tank's spawn spot"); }
                return;
            }
            _parked.Remove(e.Ptr);
            e.Body.isKinematic = false;
            Teleport(e, s.Pos, s.Rot, s.Vel, s.AngVel);
            MPLog.Info($"Unparked {e.RawName} at {s.Pos}");
        }

        // =====================================================================================  local -> network
        void SendBattleState()
        {
            if (!Networked) return;
            bool inB = InBattle; int ep = Epoch; string veh = LocalVehicleName; byte id = S.LocalId;
            S.Broadcast(Wire.Build(Msg.BattleState, w => { w.Write(id); w.Write(inB); w.Write(ep); w.Write(veh); }));
            if (S.IsHost) S.SendRoster();
        }

        public void OnConnected() { SendBattleState(); }
        public void OnPlayerJoined(RemotePlayer p) { SendBattleState(); }

        void SendLocalState()
        {
            var b = Local.Body; if (b == null) { Local.Refresh(); return; }
            var s = new VehicleStateMsg { Id = S.LocalId, Seq = ++_seq };
            s.Pos = b.position; s.Rot = b.rotation; s.Vel = b.linearVelocity; s.AngVel = b.angularVelocity;
            byte flags = 2;
            try { if (Local.FireModule != null && Local.FireModule.OnFire) flags |= 4; } catch { }
            if (Local.Driver != null)
            {
                try
                {
                    var sol = Local.Driver.Solution; s.Steer = sol.Steer; s.Throttle = sol.Throttle;
                    if (Local.Driver.HoldingIgnition) flags |= 1;
                }
                catch { }
            }
            s.Flags = flags;
            var aims = new Quaternion[Local.AimXforms.Count];
            for (int i = 0; i < aims.Length; i++)
            {
                try { var xf = Local.AimXforms[i]; aims[i] = xf != null ? xf.localRotation : Quaternion.identity; } catch { aims[i] = Quaternion.identity; }
            }
            s.AimRots = aims;
            S.Broadcast(Wire.State(s));
        }

        void SendLocalHealth(bool force)
        {
            if (Local.Pools.Count == 0) return;
            var vals = new float[Local.Pools.Count];
            bool changed = _lastSentHealth == null || _lastSentHealth.Length != vals.Length;
            for (int i = 0; i < vals.Length; i++)
            {
                try { vals[i] = Local.Pools[i].Current; } catch { vals[i] = -1; }
                if (!changed && Mathf.Abs(vals[i] - _lastSentHealth[i]) > 0.001f) changed = true;
            }
            if (Time.time - _lastHealthForce > 3f) { force = true; }
            if (!changed && !force) return;
            _lastHealthForce = Time.time;
            _lastSentHealth = vals;
            S.Broadcast(Wire.Health(new HealthMsg { Id = S.LocalId, Layout = Local.PoolLayoutHash, Values = vals }));
        }

        /// <summary>Called from the projectile launch hook when a shell leaves one of OUR guns.</summary>
        public void OnLocalLaunch(Il2CppSystem.Object source, Vector3 pos, Vector3 dir, Vector3 vel, float barrelLength, uint maxPressure, ProjectileTypeID typeId)
        {
            if (!InBattle || !Networked) return;
            int weapon = FindLocalWeaponIndex(source, pos);
            var f = new FireMsg
            {
                Id = S.LocalId, Weapon = (byte)Math.Max(0, weapon), Pos = pos, Dir = dir, Vel = vel,
                BarrelLength = barrelLength, MaxPressure = maxPressure, ProjectileGuid = Projectiles.GuidOf(typeId),
            };
            try { Shells.ShellStore.ActiveSpec = weapon >= 0 && weapon < Local.Weapons.Count ? Shells.ShellStore.SpecOfWeapon(Local.GO, Local.Weapons[weapon]) : ""; } catch { }   // this gun's own shell settings
            Shells.ShellSelect.OnFired();
            LastLocalShotAt = Time.time;
            Shells.ShellRegistry.TagLocalLaunch(f, Local, source);
            if (f.ShellKind == 0) f.Payload = Shells.ShellParams.ForLocal(Shells.ShellType.AP).ToBytes();   // AP: our spall/normalisation settings travel too
            S.Broadcast(Wire.Fire(f));
            ShotsSent++;
            MPLog.Info($"Shot sent: weapon={f.Weapon} type={typeId.Value} guid={f.ProjectileGuid} kind={f.ShellKind} pos={pos} dir={dir}");
        }

        /// <summary>Is this projectile source one of the local player's weapons?</summary>
        public bool IsLocalSource(Il2CppSystem.Object source, Vector3 launchPos)
        {
            if (!InBattle) return false;
            var t = Projectiles.TransformOf(source);
            if (t != null)
            {
                try
                {
                    var vb = t.GetComponentInParent<VehicleBehaviour>();
                    if (vb != null) return vb.Pointer == Local.Ptr;
                }
                catch { }
                if (t.IsChildOf(Local.GO.transform)) return true;
            }
            // fall back to geometry: launched from inside our own tank's bounds
            try
            {
                var bounds = Local.VB.WorldBounds;
                bounds.Expand(4f);
                return bounds.Contains(launchPos);
            }
            catch { return false; }
        }

        int FindLocalWeaponIndex(Il2CppSystem.Object source, Vector3 pos)
        {
            var t = Projectiles.TransformOf(source);
            if (t != null)
                for (int i = 0; i < Local.Weapons.Count; i++)
                {
                    var w = Local.Weapons[i];
                    try { if (w != null && (w.transform.Pointer == t.Pointer || t.IsChildOf(w.transform))) return i; } catch { }
                }
            // nearest fire position
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < Local.Weapons.Count; i++)
            {
                var w = Local.Weapons[i]; if (w == null) continue;
                try { float d = (w.ShellSpawnPosition - pos).sqrMagnitude; if (d < bestD) { bestD = d; best = i; } } catch { }
            }
            return best;
        }

        // =====================================================================================  network -> puppets
        public void OnRemoteState(VehicleStateMsg s)
        {
            s.ReceivedAt = Time.time;
            StatesReceived++;
            if (_latest.TryGetValue(s.Id, out var prev) && unchecked((int)(s.Seq - prev.Seq)) <= 0) return; // stale
            _latest[s.Id] = s;
            if (S.Players.TryGetValue(s.Id, out var p)) p.LastStateAt = s.ReceivedAt;
            if (PuppetByPlayer.TryGetValue(s.Id, out var rig) && _parked.Contains(rig.Ptr)) Unpark(rig, s);
        }

        public void OnRemoteBattleState(RemotePlayer p)
        {
            if (!p.InBattle && PuppetByPlayer.TryGetValue(p.Id, out var rig))
            {
                rig.OwnerId = 255; PuppetByPlayer.Remove(p.Id); _latest.Remove(p.Id);
                if (!_parked.Contains(rig.Ptr)) Park(rig);
            }
        }

        public void OnPlayerLeft(RemotePlayer p)
        {
            if (PuppetByPlayer.TryGetValue(p.Id, out var rig)) { rig.OwnerId = 255; PuppetByPlayer.Remove(p.Id); if (rig.Alive) Park(rig); }
            _latest.Remove(p.Id);
        }

        public void OnSpawnPose(int epoch, Vector3 pos, Quaternion rot)
        {
            if (!Plugin.AlignSpawns.Value) return;
            if (!InBattle || epoch != Epoch) { MPLog.Info($"Ignoring spawn pose for epoch {epoch} (current {Epoch}, inBattle={InBattle})"); return; }
            if (SpawnApplied) return;
            SpawnApplied = true;
            // never put the tank inside the ground (that's what flings it): keep at least our current ground clearance
            float clearance = 1f;
            if (Local.Body != null && Terrain.GroundHeight(Local.Body.position, out float myGround))
                clearance = Mathf.Clamp(Local.Body.position.y - myGround, 0.3f, 4f);
            if (Terrain.GroundHeight(pos, out float g)) pos.y = Mathf.Max(pos.y, g + clearance + 0.3f);
            else pos.y += 0.5f;
            Teleport(Local, pos, rot, Vector3.zero, Vector3.zero);
            LastEvent = "Moved to host-assigned spawn";
            MPLog.Info($"Applied host spawn pose {pos}");
        }

        public PenRecord LatestPenCam;   // shown by UI/PenCamView
        public float LastLocalShotAt = -10f;
        byte _killSoundFor = 255, _pendingKillSound = 255; float _killSoundAt = -10f, _pendingKillSoundAt = -10f;
        public readonly Dictionary<byte, (float time, byte reason)> RecentKills = new Dictionary<byte, (float, byte)>();
        /// <summary>Kill reason (0 crew, 1 ammo) if the victim died within `window` s of `at`, else -1.</summary>
        public int KillNear(byte victim, float at, float window = 4f)
        {
            if (RecentKills.TryGetValue(victim, out var k) && Math.Abs(k.time - at) < window) return k.reason;
            return -1;
        }

        public void OnPenCam(PenRecord p)
        {
            if (S == null || p.Shooter != S.LocalId) return;   // only the shooter sees it
            p.ReceivedAt = Time.time;
            LatestPenCam = p;
            // a kill reported without a killer, and now our pen cam of that tank arrives: it was ours
            if (_pendingKillSound == p.Victim && Time.time - _pendingKillSoundAt < 3f && !(_killSoundFor == p.Victim && Time.time - _killSoundAt < 5f))
            { Sounds.Play(Plugin.KillSoundFile.Value, Plugin.KillSoundVolume.Value); _killSoundFor = p.Victim; _killSoundAt = Time.time; _pendingKillSound = 255; }
            MPLog.Info($"Pen cam received from {NameOf(p.Victim)}: {PenRecord.ResultText(p.Result)}, {p.Frags.Count} fragments, {p.Damage.Count} damaged");
        }

        public void OnRemoteFire(FireMsg f)
        {
            ShotsReceived++;
            PenCapture.RecordRemoteShot(f);
            if (!InBattle) return;
            if (!PuppetByPlayer.TryGetValue(f.Id, out var rig) || !rig.Alive)
            {
                MPLog.Warn($"Shot from player {f.Id} but no puppet bound");
                return;
            }
            try
            {
                if (Shells.ShellRegistry.HandleRemote(f, rig)) return;
                Projectiles.FireRemote(rig, f);
            }
            catch (Exception e) { MPLog.Error("Remote fire: " + e); }
        }

        public void OnRemoteHealth(HealthMsg h)
        {
            if (!PuppetByPlayer.ContainsKey(h.Id)) { _pendingHealth[h.Id] = h; return; }
            ApplyHealth(h);
        }

        public void OnChat(byte id, string text) { MPLog.Info($"[chat] {id}: {text}"); LastEvent = text; }

        void ApplyHealth(HealthMsg h)
        {
            byte id = h.Id; var vals = h.Values;
            var rig = PuppetByPlayer[id];
            if (rig.Pools.Count == 0) rig.ResolveCrew();
            if (rig.Pools.Count != vals.Length || rig.PoolLayoutHash != h.Layout)
            {
                if (_healthMismatchWarned.Add(rig.Ptr))
                    MPLog.Warn($"Health layout mismatch for {rig.RawName}: local {rig.Pools.Count} pools/{rig.PoolLayoutHash} vs remote {vals.Length}/{h.Layout}. The AI-team tank is not the same design as player {id}'s tank - damage won't be mirrored on it.");
                return;
            }
            int n = Math.Min(rig.Pools.Count, vals.Length);
            int applied = 0;
            Patches.AllowDamage = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var pool = rig.Pools[i];
                    if (pool == null || vals[i] < 0) continue;
                    float cur = pool.Current;
                    float delta = vals[i] - cur;
                    if (Mathf.Abs(delta) < 0.001f) continue;
                    pool.HealthDelta(delta);
                    applied++;
                }
            }
            catch (Exception e) { MPLog.Warn("ApplyHealth: " + e.Message); }
            finally { Patches.AllowDamage = false; }
            if (applied > 0) MPLog.Debug($"Health applied to {rig.RawName}: {applied} pools changed");
        }

        void ApplyPuppetInputs()
        {
            float now = Time.time;
            foreach (var kv in PuppetByPlayer)
            {
                var rig = kv.Value;
                if (rig == null || !rig.Alive) continue;
                _latest.TryGetValue(kv.Key, out var s);
                bool fresh = s != null && now - s.ReceivedAt < 1.5f;
                try
                {
                    if (rig.Driver != null)
                    {
                        rig.Driver.Solution = fresh ? new DriveSolution(s.Steer, s.Throttle) : new DriveSolution(0f, 0f);
                        if (s != null) rig.Driver.HoldingIgnition = (s.Flags & 1) != 0;
                    }
                    if (s != null && fresh) SyncFire(rig, (s.Flags & 4) != 0);
                    if (s != null)
                    {
                        // turret ring / trunnions: native motors are frozen on puppets (Patches.Aimable_Prefix),
                        // so we drive the transforms straight from the owner's local rotations
                        float k = 1f - Mathf.Exp(-18f * Time.deltaTime);
                        int n = Math.Min(s.AimRots.Length, rig.AimXforms.Count);
                        for (int i = 0; i < n; i++)
                        {
                            var xf = rig.AimXforms[i]; if (xf == null) continue;
                            var q = s.AimRots[i];
                            if (float.IsNaN(q.x) || (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0)) continue;
                            xf.localRotation = Quaternion.Slerp(xf.localRotation, q, k);
                        }
                    }
                }
                catch (Exception e) { MPLog.Warn($"Puppet inputs {rig.RawName}: {e.Message}"); }
            }
        }

        /// <summary>Fire is its own state (not a health value): mirror the owner's burning / extinguished state.</summary>
        void SyncFire(VehicleRig rig, bool ownerOnFire)
        {
            var fm = rig.FireModule; if (fm == null) return;
            bool on;
            try { on = fm.OnFire; } catch { return; }
            if (on == ownerOnFire) return;
            if (Time.time < rig.NextFireSync) return;
            rig.NextFireSync = Time.time + 0.5f;
            Patches.AllowDamage = true;
            try
            {
                if (ownerOnFire) { fm.StartFire(120f); MPLog.Info($"Puppet {rig.RawName}: fire started (owner is burning)"); }
                else { Extinguish(rig); MPLog.Info($"Puppet {rig.RawName}: fire extinguished (owner put it out)"); }
            }
            catch (Exception e) { MPLog.Warn("SyncFire: " + e.Message); }
            finally { Patches.AllowDamage = false; }
        }

        static void Extinguish(VehicleRig rig)
        {
            try { rig.FireModule?.fireControl?.Extinguish(); } catch (Exception e) { MPLog.Warn("Extinguish: " + e.Message); }
            try { rig.VB.Flags &= ~VehicleFlags.OnFire; } catch { }
        }

        // =====================================================================================  field repair
        public float RepairEndsAt = -1f;      // > 0 while a repair is running
        Vector3 _repairStartPos;
        public bool Repairing => RepairEndsAt > 0f;
        public float RepairRemaining => Repairing ? Mathf.Max(0f, RepairEndsAt - Time.time) : 0f;
        UnityEngine.InputSystem.Key _repairKey = UnityEngine.InputSystem.Key.F6; string _repairKeyName;

        void UpdateRepair()
        {
            if (_repairKeyName != Plugin.RepairKey.Value)
            {
                _repairKeyName = Plugin.RepairKey.Value;
                if (!Enum.TryParse(_repairKeyName, true, out _repairKey)) { MPLog.Warn($"Unknown repair key '{_repairKeyName}', using F6"); _repairKey = UnityEngine.InputSystem.Key.F6; }
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool pressed = false;
            try { pressed = kb != null && kb[_repairKey].wasPressedThisFrame; } catch { }

            if (pressed)
            {
                if (Repairing) { RepairEndsAt = -1f; LastEvent = "Repair cancelled"; Notice("Field repair cancelled"); }
                else if (_localDestroyed) Notice("Your tank is destroyed - it can't be repaired");
                else
                {
                    RepairEndsAt = Time.time + Mathf.Max(1f, Plugin.RepairDuration.Value);
                    _repairStartPos = Local.Body != null ? Local.Body.position : Vector3.zero;
                    Notice($"Field repair started ({Plugin.RepairDuration.Value:F0}s) - hold position");
                }
            }
            if (!Repairing) return;
            if (_localDestroyed) { RepairEndsAt = -1f; return; }
            float maxMove = Plugin.RepairCancelDistance.Value;
            if (maxMove > 0 && Local.Body != null && (Local.Body.position - _repairStartPos).magnitude > maxMove)
            {
                RepairEndsAt = -1f; Notice("Field repair cancelled - you moved"); return;
            }
            if (Time.time < RepairEndsAt) return;
            RepairEndsAt = -1f;
            int n = RepairVehicle(Local);
            Notice($"Field repair complete ({n} modules restored)");
            if (Networked)
            {
                byte id = S.LocalId;
                S.Broadcast(Wire.Build(Msg.Repair, w => w.Write(id)));
                _lastSentHealth = null; SendLocalHealth(true);
            }
        }

        void Notice(string text)
        {
            MPLog.Info(text);
            LastEvent = text;
            KillFeed.Add(new FeedEntry { Text = "<color=#9fd>" + text + "</color>", Time = Time.time });
            if (KillFeed.Count > 6) KillFeed.RemoveAt(0);
        }

        static int RepairVehicle(VehicleRig rig) => VehicleRestore.Restore(rig, includeCrew: false);

        public void OnRemoteRepair(byte id)
        {
            string who = NameOf(id) ?? ("Player " + id);
            if (PuppetByPlayer.TryGetValue(id, out var rig) && rig.Alive) VehicleRestore.Restore(rig, includeCrew: false);
            Notice($"{who} finished a field repair");
        }

        // =====================================================================================  physics correction
        void CorrectPose(VehicleRig rig, VehicleStateMsg s, float dt)
        {
            var b = rig.Body;
            if (b.isKinematic) return;
            float age = Mathf.Clamp(Time.time - s.ReceivedAt, 0f, 0.35f);
            var targetPos = s.Pos + s.Vel * age;
            var targetRot = s.Rot;
            var w = s.AngVel;
            if (w.sqrMagnitude > 1e-6f) targetRot = Quaternion.AngleAxis(w.magnitude * age * Mathf.Rad2Deg, w.normalized) * s.Rot;

            var err = targetPos - b.position;
            var dq = targetRot * Quaternion.Inverse(b.rotation);
            dq.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (float.IsNaN(axis.x)) { ang = 0; axis = Vector3.up; }

            if (err.magnitude > Plugin.SnapDistance.Value || Mathf.Abs(ang) > Plugin.SnapAngle.Value)
            {
                Teleport(rig, targetPos, targetRot, s.Vel, s.AngVel);
                MPLog.Debug($"Snap {rig.RawName}: err={err.magnitude:F1}m ang={ang:F0}");
                return;
            }

            float k = Mathf.Clamp01(Plugin.VelocityBlend.Value);
            var desiredV = s.Vel + err * Plugin.PositionGain.Value;
            b.linearVelocity = Vector3.Lerp(b.linearVelocity, desiredV, k);
            var desiredW = s.AngVel + axis * (ang * Mathf.Deg2Rad * Plugin.RotationGain.Value);
            b.angularVelocity = Vector3.Lerp(b.angularVelocity, desiredW, k);
        }

        static void Teleport(VehicleRig rig, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
        {
            var b = rig.Body;
            if (b == null) { rig.GO.transform.SetPositionAndRotation(pos, rot); return; }
            b.position = pos; b.rotation = rot;
            rig.GO.transform.SetPositionAndRotation(pos, rot);
            if (!b.isKinematic) { b.linearVelocity = vel; b.angularVelocity = angVel; }
        }

        // =====================================================================================  destruction & kill feed
        /// <summary>Destroyed = every crew member knocked out, or an ammunition rack destroyed. Checked by the OWNER only.</summary>
        void CheckLocalDestroyed()
        {
            if (_localDestroyed || Local == null) return;
            if (Local.CrewPools.Count == 0 && Local.AmmoPools.Count == 0) { Local.CrewResolved = false; Local.ResolveCrew(); }

            // who is hurting us? (needed for the kill feed)
            float sum = 0f;
            foreach (var p in Local.Pools) { try { sum += p.Current; } catch { } }
            if (_lastLocalHealthSum >= 0 && sum < _lastLocalHealthSum - 0.001f)
            {
                var a = FindAttacker();
                if (a != 255) { _lastAttacker = a; _lastAttackerTime = Time.time; }
            }
            _lastLocalHealthSum = sum;

            // crew: seat health pools AND the seated operators (a knocked-out crew member can keep some pool health)
            bool poolsGone = Local.CrewPools.Count > 0;
            foreach (var c in Local.CrewPools) { try { var hi = c.HealthInfo; if (hi.Current > 0.01f && hi.Fraction > 0.02f) { poolsGone = false; break; } } catch { poolsGone = false; } }
            int alive = Local.AliveCrewCount(out int seats);
            if (alive > 0) _sawCrewAlive = true;
            bool operatorsGone = _sawCrewAlive && seats > 0 && alive == 0;
            bool abandoned = false;
            try { abandoned = _sawCrewAlive && Local.VB.ControlType == Sprocket.Vehicles.Control.ControlType.None; } catch { }
            bool crewGone = poolsGone || operatorsGone || abandoned;
            if (Time.time >= _nextCrewLog)
            {
                _nextCrewLog = Time.time + 2f;
                var sbc = new StringBuilder("crew check: pools=[");
                foreach (var c in Local.CrewPools) { try { sbc.Append(c.HealthInfo.Fraction.ToString("F2")).Append(' '); } catch { } }
                sbc.Append($"] operatorsAlive={alive}/{seats} control={Local.VB.ControlType} ammo=[");
                foreach (var a in Local.AmmoPools) { try { sbc.Append(a.HealthInfo.Fraction.ToString("F2")).Append(' '); } catch { } }
                MPLog.File(sbc.Append(']').ToString());
            }
            bool ammoGone = false;
            foreach (var a in Local.AmmoPools) { try { if (a.HealthInfo.Current <= 0.01f) { ammoGone = true; break; } } catch { } }
            if (!crewGone && !ammoGone) return;

            _localDestroyed = true;
            byte killer = Time.time - _lastAttackerTime < 60f ? _lastAttacker : (byte)255;
            DeathReason = ammoGone ? "AMMO RACK EXPLOSION" : "CREW KNOCKED OUT";
            DeathKiller = NameOf(killer) ?? "";
            var k = new KillMsg { Victim = S.LocalId, Killer = killer, Epoch = Epoch, Reason = (byte)(ammoGone ? 1 : 0) };
            MPLog.Info($"Local tank destroyed ({KillMsg.ReasonText(k.Reason)}; pools={poolsGone} operators={operatorsGone} abandoned={abandoned} ammo={ammoGone}), killer={killer}");
            S.Broadcast(Wire.Kill(k));
            AddKill(k);
            LocalDestroyedAt = Time.time;
            // kill cam: replay the hit that killed us (if its record is already out; otherwise PenCapture shows it when it is)
            if (PenCapture.LastSent != null && Time.time - PenCapture.LastSentAt < 2f) ShowKillCam(PenCapture.LastSent);
        }

        public float LocalDestroyedAt = -10f;
        PenRecord _killCamShown;
        public void ShowKillCam(PenRecord rec)
        {
            if (!Plugin.KillCamEnabled.Value || rec == null || rec == _killCamShown || rec.Shooter == 255 || (rec.Result != 0 && rec.Result != 4)) return;
            _killCamShown = rec;
            var kc = new PenRecord
            {
                Victim = rec.Victim, Shooter = rec.Shooter, Layout = rec.Layout, ShellKind = rec.ShellKind, ShellGuid = rec.ShellGuid, Entry = rec.Entry, ShellDir = rec.ShellDir,
                ShellSpeed = rec.ShellSpeed, ShellMass = rec.ShellMass, ShellDiameter = rec.ShellDiameter, Result = rec.Result, Frags = rec.Frags, Damage = rec.Damage,
                Blast = rec.Blast, BlastPoint = rec.BlastPoint, BlastRadius = rec.BlastRadius, DeadCrew = rec.DeadCrew, AimRots = rec.AimRots, AimPos = rec.AimPos, Range = rec.Range, Angle = rec.Angle, PlateLos = rec.PlateLos,
                KillCam = true, ReceivedAt = Time.time,
            };
            LatestPenCam = kc;
            MPLog.Info($"Kill cam: replaying the hit from {NameOf(rec.Shooter)}");
        }

        byte FindAttacker()
        {
            float now = Time.time;
            byte best = 255; float bestT = -1;
            try
            {
                var reg = Patches.CapturedFactory?.TryCast<ProjectileRegister>() ?? DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>();
                var hist = reg?.projectileHistory;
                int myId = Local.VB.ID;
                foreach (var kv in Patches.RemoteProjectiles)
                {
                    if (now - kv.Value.time > 20f || kv.Value.time <= bestT) continue;
                    if (hist == null || !hist.ContainsKey(kv.Key)) continue;
                    var hit = hist[kv.Key]?.TryCast<HitDamageModelProjectileEvent>();
                    if (hit == null || hit.DamageModel == null) continue;
                    if ((int)hit.DamageModel.ID != myId) continue;
                    best = kv.Value.shooter; bestT = kv.Value.time;
                }
            }
            catch (Exception e) { MPLog.Debug("FindAttacker: " + e.Message); }
            if (best != 255) return best;
            // fallback: most recent replicated shot in the last 8 s
            foreach (var kv in Patches.RemoteProjectiles)
                if (now - kv.Value.time < 8f && kv.Value.time > bestT) { best = kv.Value.shooter; bestT = kv.Value.time; }
            return best;
        }

        public void OnRemoteKill(KillMsg k)
        {
            MPLog.Info($"Kill received: victim={k.Victim} killer={k.Killer} reason={KillMsg.ReasonText(k.Reason)}");
            RecentKills[k.Victim] = (Time.time, k.Reason);
            if (S != null && k.Victim != S.LocalId)
            {
                // with the kill feed entry. The victim can't always tell who killed it (killer 255): then it is ours if
                // we shot that tank in the last few seconds
                bool ours = k.Killer == S.LocalId || (k.Killer == 255 && Time.time - LastLocalShotAt < 4f);
                if (ours) { Sounds.Play(Plugin.KillSoundFile.Value, Plugin.KillSoundVolume.Value); _killSoundFor = k.Victim; _killSoundAt = Time.time; }
                else if (k.Killer == 255) { _pendingKillSound = k.Victim; _pendingKillSoundAt = Time.time; }
            }
            AddKill(k);
        }

        public string NameOf(byte id)
        {
            if (id == 255) return null;
            if (S != null && id == S.LocalId) return Plugin.EffectiveName;
            if (S != null && S.Players.TryGetValue(id, out var p)) return p.Name;
            return "Player " + id;
        }

        void AddKill(KillMsg k)
        {
            string victim = NameOf(k.Victim) ?? "?";
            string killer = NameOf(k.Killer);
            string text = killer != null && k.Killer != k.Victim
                ? $"<color=#FFD93D><b>{killer}</b></color> destroyed <color=#FF6B5B><b>{victim}</b></color>"
                : $"<color=#FF6B5B><b>{victim}</b></color> was destroyed";
            text += $" <size=80%><color=#aaa>({KillMsg.ReasonText(k.Reason)})</color></size>";
            KillFeed.Add(new FeedEntry { Text = text, Time = Time.time });
            if (KillFeed.Count > 6) KillFeed.RemoveAt(0);
            LastEvent = killer != null ? $"{killer} destroyed {victim}" : $"{victim} was destroyed";
        }

        // =====================================================================================  diagnostics
        public void DumpDiagnostics(string why)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== SprocketMP diagnostics ({why}) epoch={Epoch} inBattle={InBattle} session={S?.Mode} localId={S?.LocalId}");
            sb.AppendLine($"Patches: launch={Patches.LaunchPatched} launchExplicit={Patches.LaunchExplicitPatched} weaponFire={Patches.WeaponFirePatched} health={Patches.HealthPatchCount}");
            foreach (var r in _rigs.Values)
            {
                sb.AppendLine("  VEH " + r.Describe() + (Local != null && r.Ptr == Local.Ptr ? "  <== LOCAL" : "") + (r.OwnerId != 255 ? $"  <== puppet of {r.OwnerId}" : ""));
                try
                {
                    var mods = r.VB.modules;
                    if (mods != null) for (int i = 0; i < mods.Count; i++) sb.AppendLine("     module " + VehicleRig.TypeName(mods[i]));
                }
                catch (Exception e) { sb.AppendLine("     modules err " + e.Message); }
                for (int i = 0; i < r.GunLayers.Count; i++)
                {
                    var gl = r.GunLayers[i];
                    string at = "?", dir = "?";
                    try { at = VehicleRig.TypeName(gl.ControlTarget); } catch { }
                    try { dir = gl.TargetDirection.ToString(); } catch { }
                    sb.AppendLine($"     gunLayer[{i}] aimable={at} targetDir={dir}");
                }
                for (int i = 0; i < r.Gunners.Count; i++)
                {
                    string wt = "?", fg = "?";
                    try { wt = VehicleRig.TypeName(r.Gunners[i].weapon); } catch { }
                    try { fg = r.Gunners[i].FireGroup.ToString(); } catch { }
                    sb.AppendLine($"     gunner[{i}] weapon={wt} fireGroup={fg} weaponBehaviour={(r.Weapons[i] != null)}");
                }
            }
            try
            {
                var reg = AIRegister.register;
                sb.AppendLine($"  AIRegister count={reg?.Count}");
                if (reg != null) for (int i = 0; i < reg.Count; i++)
                    {
                        var ai = reg[i]; var cmd = ai?.TryCast<CommanderAI>();
                        string tgt = "?"; try { tgt = cmd?.controlTarget?.gameObject?.name; } catch { }
                        sb.AppendLine($"     AI {VehicleRig.TypeName(ai)} enabled={ai?.Enabled} target={tgt}");
                    }
            }
            catch (Exception e) { sb.AppendLine("  AIRegister err " + e.Message); }
            try { sb.AppendLine("  DamageModel.ProjectileFactory = " + VehicleRig.TypeName(DamageModel.ProjectileFactory)); } catch { }
            MPLog.File(sb.ToString());
            MPLog.Info($"Diagnostics written to SprocketLAN_{Environment.ProcessId}.log ({_rigs.Count} vehicles)");
        }
    }
}

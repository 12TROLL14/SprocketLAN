using System;
using System.Collections.Generic;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Per-player match statistics for the scoreboard. Everything is counted locally from messages every player
    /// already receives (shots, kill messages, the pen cam records the host relays to everyone), so nothing new
    /// goes over the network.
    /// </summary>
    public static class Stats
    {
        public sealed class Line { public int Kills, Deaths, Shots, Hits, Pens, Bounces, Modules, AiKills; }
        public static readonly Dictionary<byte, Line> ById = new Dictionary<byte, Line>();
        public static float Since = 0f;

        static Line Of(byte id) { if (!ById.TryGetValue(id, out var l)) ById[id] = l = new Line(); return l; }
        public static void Reset() { ById.Clear(); Since = Time.time; }

        static byte Me => BattleManager.Instance?.LocalShooterId ?? 0;

        public static void Shot(byte shooter) => Of(shooter).Shots++;
        public static void Kill(byte killer, byte victim)
        {
            if (victim != 255 && victim != PenCapture.BotVictimId) Of(victim).Deaths++;
            if (killer != 255 && killer != victim && killer != PenCapture.AiShooterId) Of(killer).Kills++;
        }
        public static void AiKill() { var l = Of(Me); l.AiKills++; }

        /// <summary>A pen cam record (ours, received or sent): a hit by its shooter.</summary>
        public static void Hit(PenRecord r)
        {
            if (r == null || r.Shooter == 255 || r.Shooter == PenCapture.AiShooterId || r.Victim == PenCapture.TestVictimId) return;
            var l = Of(r.Shooter);
            l.Hits++;
            if (r.Result == 0 || (r.Result == 4 && r.Damage.Count > 0)) l.Pens++;
            else if (r.Result == 1 || r.Result == 2) l.Bounces++;
            l.Modules += r.Damage.Count;
        }
    }
}

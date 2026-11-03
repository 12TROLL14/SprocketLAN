using System;
using HarmonyLib;
using Sprocket;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Main loop. Driven by Harmony postfixes on SprocketApplication.Update/FixedUpdate — NOT by an injected
    /// MonoBehaviour: on this Unity 6 build Il2CppInterop's ClassInjector ("Class::Init signatures exhausted")
    /// crashes the game (AccessViolation in Class_GetFieldDefaultValue_Hook).
    /// </summary>
    public static class Runner
    {
        public static Session Session;
        public static BattleManager Battle;
        public static UI.MenuUI Menu;
        static bool _started, _autoStarted;
        static float _startTime;

        static void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            Session = new Session(); Session.Current = Session;
            Battle = new BattleManager();
            Session.PlayerLeft += p => Battle.OnPlayerLeft(p);
            Menu = new UI.MenuUI();
            _startTime = Time.realtimeSinceStartup;
            MPLog.Info("Runner started (Harmony-driven loop)");
            DesignShare.StartListener();
            Sounds.Preload(Plugin.KillSoundFile.Value, Plugin.KillSoundVolume.Value);
        }

        public static void Update()
        {
            EnsureStarted();
            Patches.ResetFrame();
            try { DesignShare.ApplyShellImports(); } catch (Exception e) { MPLog.Warn("shell import: " + e.Message); }
            if (!_autoStarted && Time.realtimeSinceStartup - _startTime > 3f)
            {
                _autoStarted = true;
                if (Plugin.CmdHost != null) Session.StartHost(int.TryParse(Plugin.CmdHost, out var p) ? p : Plugin.Port.Value);
                else if (Plugin.CmdJoin != null) Session.StartClient(Plugin.CmdJoin);
            }
            try { Session.Pump(); } catch (Exception e) { MPLog.Error("Pump: " + e); }
            try { Lobby.Update(); } catch (Exception e) { MPLog.Error("Lobby: " + e); }
            try { Net.Discovery.Tick(Session); } catch (Exception e) { MPLog.Error("LAN discovery: " + e); }
            try { SprocketLAN.Api.Lan.Tick(); } catch (Exception e) { MPLog.Error("API: " + e); }
            try { Battle.Update(); } catch (Exception e) { MPLog.Error("Battle.Update: " + e); }
            try { Sounds.Update(); } catch (Exception e) { MPLog.Error("Sounds: " + e); }
            try { Menu.Update(); } catch (Exception e) { MPLog.Error("Menu.Update: " + e); }
        }

        public static void FixedUpdate()
        {
            if (!_started) return;
            try { Battle.FixedUpdate(); } catch (Exception e) { MPLog.Error("Battle.FixedUpdate: " + e); }
        }

        public static void Quit() { try { Session?.Stop(); } catch { } }
    }

    [HarmonyPatch(typeof(SprocketApplication), nameof(SprocketApplication.Update))]
    static class AppUpdatePatch { static void Postfix() { Runner.Update(); } }

    [HarmonyPatch(typeof(SprocketApplication), nameof(SprocketApplication.FixedUpdate))]
    static class AppFixedUpdatePatch { static void Postfix() { Runner.FixedUpdate(); } }
}

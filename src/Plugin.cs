using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketMP
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "SprocketLAN";   // also the config file name: BepInEx/config/SprocketLAN.cfg
        public const string NAME = "SprocketLAN";
        public const string VERSION = "6.6.0";
        public const int PROTOCOL = 18;

        public static Plugin Instance;
        public static Harmony Harmony;

        // ---- config ----
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<string> JoinAddress;
        public static ConfigEntry<int> Port, DiscoveryPort;
        public static ConfigEntry<bool> DiscoveryAnswer;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<float> SnapDistance;
        public static ConfigEntry<float> SnapAngle;
        public static ConfigEntry<float> PositionGain;
        public static ConfigEntry<float> RotationGain;
        public static ConfigEntry<float> VelocityBlend;
        public static ConfigEntry<bool> PuppetsImmuneToLocalDamage;
        public static ConfigEntry<bool> AlignSpawns;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> SpawnNearHost;
        public static ConfigEntry<string> ShellTestKey, ShellTestFireKey, PenCamPlateColour, StabKey;
        public static ConfigEntry<float> StabMultiplier, StabElevationMultiplier;
        public static ConfigEntry<float> PenCamSeconds;
        public static ConfigEntry<float> PenCamScale, PenCamSendDelay;
        public static ConfigEntry<bool> DesignAccept, KillCamEnabled;
        public static ConfigEntry<float> KillCamSpeed;
        public static ConfigEntry<int> DesignPort;
        public static ConfigEntry<string> DesignReceiveFolder, DesignBackupFolder, DesignLastTarget, DesignKey;
        public static ConfigEntry<float> KillSoundVolume, AmmoBlastRadius;
        public static ConfigEntry<bool> KillSoundOnBots;
        public static ConfigEntry<bool> ApheBurstLines;
        public static ConfigEntry<string> KillSoundFile;
        public static ConfigEntry<float> SpawnNearHostDistance;
        public static ConfigEntry<string> RepairKey;
        public static ConfigEntry<float> RepairDuration;
        public static ConfigEntry<float> RepairCancelDistance;
        public static ConfigEntry<string> ConfigKey;
        public static ConfigEntry<string> LobbyDesignFolder, LobbyKey, LobbyLastTank, ModelToolsKey;
        public static ConfigEntry<bool> FeatureShells, FeaturePenCam, FeatureModelTools, FeatureBots, FeatureSandbox;
        // read once at start: what was actually loaded this session (the config may change while the game runs)
        public static bool ShellsOn, PenCamOn, ModelToolsOn, SandboxOn;
        public static ConfigEntry<float> LobbySpawnSpacing;

        // command line overrides
        public static string CmdHost;   // non-null => auto host
        public static string CmdJoin;   // non-null => auto join address
        public static string CmdName;

        public override void Load()
        {
            Instance = this;
            MPLog.Init(Log); MPLog.StartWatchdog();

            PlayerName = Config.Bind("Player", "Name", Environment.UserName, "Name shown to other players");
            JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1:7777", "Last address used to join (ip:port)");
            Port = Config.Bind("Network", "HostPort", 7777, "TCP port used when hosting");
            DiscoveryPort = Config.Bind("Network", "DiscoveryPort", 7778, "UDP port for finding LAN games (FIND GAMES button). Everyone must use the same one");
            DiscoveryAnswer = Config.Bind("Network", "AnswerLanSearches", true, "While hosting, show up in other players' FIND GAMES list");
            SendRate = Config.Bind("Network", "SendRate", 60, "Vehicle state packets per second");
            SnapDistance = Config.Bind("Puppets", "SnapDistance", 6f, "Puppet position error (m) above which it is teleported");
            SnapAngle = Config.Bind("Puppets", "SnapAngle", 40f, "Puppet rotation error (deg) above which it is teleported");
            PositionGain = Config.Bind("Puppets", "PositionGain", 6f, "How hard positional error is corrected (1/s)");
            RotationGain = Config.Bind("Puppets", "RotationGain", 6f, "How hard rotational error is corrected (1/s)");
            VelocityBlend = Config.Bind("Puppets", "VelocityBlend", 0.5f, "Per physics step blend of puppet velocity toward the networked velocity (0..1)");
            PuppetsImmuneToLocalDamage = Config.Bind("Combat", "PuppetsImmuneToLocalDamage", true,
                "Victim-authoritative damage: shells hitting a puppet on YOUR machine do no damage; the owner of the tank computes damage and replicates its health.");
            AlignSpawns = Config.Bind("Battle", "AlignSpawns", true, "Clients teleport their own tank to the spawn the host assigns, so everyone shares one coordinate frame");
            VerboseLogging = Config.Bind("Debug", "Verbose", false, "Extra logging");
            PenCamSeconds = Config.Bind("PenCam", "LingerSeconds", 5f, "How long the side x-ray view stays on screen after the 2 s chase and 2 s swing");
            PenCamPlateColour = Config.Bind("PenCam", "PlateHighlightColour", "#FFFFFF", "Colour of the highlighted (struck) plates, as #RRGGBB");
            PenCamSendDelay = Config.Bind("PenCam", "SendDelaySeconds", 0.1f, "How long the hit tank waits after working out a hit before sending the pen cam (damage that lands a moment later is still included)");
            PenCamScale = Config.Bind("PenCam", "Scale", 1.15f, "Size multiplier of the pen cam panel");
            KillCamEnabled = Config.Bind("PenCam", "KillCam", true, "When you are destroyed, replay the fatal hit in the pen cam");
            KillCamSpeed = Config.Bind("PenCam", "KillCamSpeed", 1.5f, "The kill cam plays this many times faster than the normal pen cam (1.5 = 1.5x shorter)");
            DesignKey = Config.Bind("Designs", "MenuKey", "F8", "Key that opens the design sending panel");
            DesignAccept = Config.Bind("Designs", "AcceptDesigns", true, "Accept designs other players send you");
            DesignPort = Config.Bind("Designs", "Port", 7780, "TCP port for design transfers (sender and receiver must use the same one)");
            DesignReceiveFolder = Config.Bind("Designs", "ReceiveFolder", "", "Folder received designs are saved to. Empty = the Blueprints/Vehicles folder of the faction you saved a design in most recently");
            DesignBackupFolder = Config.Bind("Designs", "BackupFolder", "", "Where the previous version of a design is backed up when a received design replaces it. Empty = BepInEx/config/SprocketLAN_DesignBackups");
            DesignLastTarget = Config.Bind("Designs", "LastTarget", "", "Last IP a design was sent to");
            KillSoundFile = Config.Bind("PenCam", "KillSoundFile", "Sounds/kill.wav", "Sound played when the pen cam shows a kill (WAV file; path relative to the plugin folder, or absolute)");
            KillSoundVolume = Config.Bind("PenCam", "KillSoundVolume", 1.04f, "Kill sound volume (0 = off, 1 = original, up to 3 = louder)");
            KillSoundOnBots = Config.Bind("PenCam", "KillSoundOnBots", true, "Play the kill sound when you destroy an AI tank too");
            ApheBurstLines = Config.Bind("PenCam", "APHEBurstShowFullLines", true, "After APHE burst fragments stop, leave their whole path drawn as a line");
            AmmoBlastRadius = Config.Bind("PenCam", "AmmoRackBlastRadius", 2.2f, "Radius (m) of the red explosion drawn on a destroyed ammo rack");
            RepairKey = Config.Bind("Repair", "Key", "F6", "Key that starts/cancels a field repair (Unity Input System key name, e.g. F6, R, Numpad0)");
            RepairDuration = Config.Bind("Repair", "DurationSeconds", 30f, "How long a field repair takes");
            RepairCancelDistance = Config.Bind("Repair", "CancelIfMovedMetres", 2f, "Repair is cancelled if the tank moves further than this (0 = never cancel)");
            ConfigKey = Config.Bind("Settings", "MenuKey", "F7", "Key that opens the in-game settings panel");
            FeatureShells = Config.Bind("Features", "ShellsAndArmour", true, "RESTART TO APPLY. The mod's shells (APHE, HE, APDS, APFSDS, HEAT, caps, autocannons), its armour / penetration model and fragments. Off: every hit is the game's own (the mod only syncs it). Turn off if another ballistics mod conflicts");
            FeaturePenCam = Config.Bind("Features", "PenCam", true, "RESTART TO APPLY. The pen cam / kill cam replays (and sending them to the shooter)");
            FeatureModelTools = Config.Bind("Features", "ModellingTools", true, "RESTART TO APPLY. The extra modelling tools in the compartment editor (not even hooked when off)");
            FeatureBots = Config.Bind("Features", "ModShellsOnBots", true, "The mod's shells, armour model and pen cam also for AI tanks (your shots at them, theirs at you), offline too");
            FeatureSandbox = Config.Bind("Features", "SandboxArmourInspector", true, "RESTART TO APPLY. The mod's changes to the sandbox armour inspector (firing the mod's shells with caliber / pen / filler / fuse settings). Off: the inspector is the game's own. Needs ShellsAndArmour");
            ShellsOn = FeatureShells.Value; PenCamOn = FeaturePenCam.Value; ModelToolsOn = FeatureModelTools.Value; SandboxOn = ShellsOn && FeatureSandbox.Value;
            ModelToolsKey = Config.Bind("Settings", "ModellingToolsKey", "F4", "Key that shows / hides the modelling tools panel in the compartment editor");
            LobbyKey = Config.Bind("Lobby", "MenuKey", "F9", "Key that opens the multiplayer lobby");
            LobbyDesignFolder = Config.Bind("Lobby", "DesignFolder", "", "Folder the other players' tanks are saved to for lobby battles. Empty = Documents/My Games/Sprocket/SprocketLAN Multiplayer");
            LobbyLastTank = Config.Bind("Lobby", "LastTank", "", "The tank you picked last in the lobby (picked again automatically)");
            LobbySpawnSpacing = Config.Bind("Lobby", "SpawnSpacingMetres", 15f, "Distance between teammates spawning side by side");
            Shells.ShellConfig.Bind(Config);
            ShellTestFireKey = Config.Bind("Shells", "ShellTestFireKey", "F8", "Fires the selected test shell at the mouse cursor (vehicle designer, test armed)");
            StabKey = Config.Bind("Stabiliser", "ToggleKey", "B", "Key that turns the stabiliser on/off (faster turret traverse and gun elevation)");
            StabMultiplier = Config.Bind("Stabiliser", "SpeedMultiplier", 4f, "How many times faster the turret traverses with the stabiliser on");
            StabElevationMultiplier = Config.Bind("Stabiliser", "ElevationMultiplier", 6f, "How many times faster the gun / mantlet elevates with the stabiliser on");
            ShellTestKey = Config.Bind("Shells", "ShellTestKey", "F9", "Shows/hides the shell test row in the vehicle designer (next to the Ammo Rack panel)");
            SpawnNearHost = Config.Bind("Battle", "SpawnClientsNearHost", false, "HOST ONLY: spawn joining players in a row in front of the host instead of at the AI team's spawn (handy for testing)");
            SpawnNearHostDistance = Config.Bind("Battle", "SpawnClientsNearHostDistance", 35f, "HOST ONLY: distance in metres in front of the host for SpawnClientsNearHost");

            ParseCommandLine();

            Harmony = new Harmony(GUID);
            Harmony.PatchAll(typeof(Plugin).Assembly);   // main loop hooks (Runner.cs)
            Patches.Apply(Harmony);                         // gameplay hooks, applied one by one
            if (ShellsOn || PenCamOn) PenCapture.Patch(Harmony);   // the hit hook: the mod's shell model and / or the pen cam capture
            if (ShellsOn)
            {
                if (SandboxOn) UI.SandboxShells.Patch(Harmony);
                Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.APHE));
                Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.HE));
                Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.APDS));
                Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.APFSDS));
                Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.HEAT));
            }
            if (ModelToolsOn) ModelTools.Patch(Harmony);
            MPLog.Info($"Features: shells & armour {(ShellsOn ? "on" : "OFF")}, pen cam {(PenCamOn ? "on" : "OFF")}, modelling tools {(ModelToolsOn ? "on" : "OFF")}, sandbox inspector {(SandboxOn ? "on" : "OFF")}, mod shells on bots {(FeatureBots.Value ? "on" : "off")}");
            MPLog.Info($"{NAME} {VERSION} loaded (protocol {PROTOCOL}). F10 = menu, F11 = diagnostics dump.");
        }

        static void ParseCommandLine()
        {
            try
            {
                foreach (var a in Environment.GetCommandLineArgs())
                {
                    if (a == "--mp-host") CmdHost = "";
                    else if (a.StartsWith("--mp-host=")) CmdHost = a.Substring(10);
                    else if (a.StartsWith("--mp-join=")) CmdJoin = a.Substring(10);
                    else if (a.StartsWith("--mp-name=")) CmdName = a.Substring(10);
                }
                if (CmdName != null) MPLog.Info("Command line name: " + CmdName);
                if (CmdHost != null) MPLog.Info("Command line: auto-host");
                if (CmdJoin != null) MPLog.Info("Command line: auto-join " + CmdJoin);
            }
            catch (Exception e) { MPLog.Warn("cmdline parse: " + e.Message); }
        }

        public static string EffectiveName => string.IsNullOrWhiteSpace(CmdName) ? PlayerName.Value : CmdName;
    }
}

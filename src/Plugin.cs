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
        public const string VERSION = "3.0.4";
        public const int PROTOCOL = 9;

        public static Plugin Instance;
        public static Harmony Harmony;

        // ---- config ----
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<string> JoinAddress;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<float> SnapDistance;
        public static ConfigEntry<float> SnapAngle;
        public static ConfigEntry<float> PositionGain;
        public static ConfigEntry<float> RotationGain;
        public static ConfigEntry<float> VelocityBlend;
        public static ConfigEntry<bool> PuppetsImmuneToLocalDamage;
        public static ConfigEntry<bool> AlignSpawns;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> ForceWindowed;
        public static ConfigEntry<int> WindowWidth, WindowHeight;
        public static ConfigEntry<bool> SpawnNearHost;
        public static ConfigEntry<bool> PenCamEnabled;
        public static ConfigEntry<string> ShellTestKey, ShellTestFireKey, PenCamPlateColour;
        public static ConfigEntry<float> PenCamSeconds;
        public static ConfigEntry<float> PenCamScale;
        public static ConfigEntry<float> KillSoundVolume, AmmoSoundVolume, AmmoBlastRadius;
        public static ConfigEntry<string> KillSoundFile, AmmoSoundFile;
        public static ConfigEntry<float> SpawnNearHostDistance;
        public static ConfigEntry<string> RepairKey;
        public static ConfigEntry<float> RepairDuration;
        public static ConfigEntry<float> RepairCancelDistance;

        // command line overrides
        public static string CmdHost;   // non-null => auto host
        public static string CmdJoin;   // non-null => auto join address
        public static string CmdName;

        public override void Load()
        {
            Instance = this;
            MPLog.Init(Log);

            PlayerName = Config.Bind("Player", "Name", Environment.UserName, "Name shown to other players");
            JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1:7777", "Last address used to join (ip:port)");
            Port = Config.Bind("Network", "HostPort", 7777, "TCP port used when hosting");
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
            PenCamEnabled = Config.Bind("PenCam", "Enabled", true, "Show the penetration camera (upper right) when your shell hits another player");
            PenCamSeconds = Config.Bind("PenCam", "LingerSeconds", 5f, "How long the side x-ray view stays on screen after the 2 s chase and 2 s swing");
            PenCamPlateColour = Config.Bind("PenCam", "PlateHighlightColour", "#FFFFFF", "Colour of the highlighted (struck) plates, as #RRGGBB");
            PenCamScale = Config.Bind("PenCam", "Scale", 1.15f, "Size multiplier of the pen cam panel");
            KillSoundFile = Config.Bind("PenCam", "KillSoundFile", "Sounds/kill.wav", "Sound played when the pen cam shows a kill (WAV file; path relative to the plugin folder, or absolute)");
            KillSoundVolume = Config.Bind("PenCam", "KillSoundVolume", 1.04f, "Kill sound volume (0 = off, 1 = original, up to 3 = louder)");
            AmmoSoundFile = Config.Bind("PenCam", "AmmoRackSoundFile", "Sounds/ammo_explosion.wav", "Sound played when a destroyed ammo rack explodes in the pen cam (WAV file)");
            AmmoSoundVolume = Config.Bind("PenCam", "AmmoRackSoundVolume", 0.53f, "Ammo rack explosion sound volume (0 = off, 1 = original, up to 3 = louder)");
            AmmoBlastRadius = Config.Bind("PenCam", "AmmoRackBlastRadius", 2.2f, "Radius (m) of the red explosion drawn on a destroyed ammo rack");
            RepairKey = Config.Bind("Repair", "Key", "F6", "Key that starts/cancels a field repair (Unity Input System key name, e.g. F6, R, Numpad0)");
            RepairDuration = Config.Bind("Repair", "DurationSeconds", 30f, "How long a field repair takes");
            RepairCancelDistance = Config.Bind("Repair", "CancelIfMovedMetres", 2f, "Repair is cancelled if the tank moves further than this (0 = never cancel)");
            Shells.ShellConfig.Bind(Config);
            ShellTestFireKey = Config.Bind("Shells", "ShellTestFireKey", "F8", "Fires the selected test shell at the mouse cursor (vehicle designer, test armed)");
            ShellTestKey = Config.Bind("Shells", "ShellTestKey", "F9", "Shows/hides the shell test row in the vehicle designer (next to the Ammo Rack panel)");
            ForceWindowed = Config.Bind("Debug", "ForceWindowed", false, "Start the game in a window (handy for running two copies on one PC)");
            WindowWidth = Config.Bind("Debug", "WindowWidth", 1280, "Window width when ForceWindowed is on");
            WindowHeight = Config.Bind("Debug", "WindowHeight", 720, "Window height when ForceWindowed is on");
            SpawnNearHost = Config.Bind("Battle", "SpawnClientsNearHost", false, "HOST ONLY: spawn joining players in a row in front of the host instead of at the AI team's spawn (handy for testing)");
            SpawnNearHostDistance = Config.Bind("Battle", "SpawnClientsNearHostDistance", 35f, "HOST ONLY: distance in metres in front of the host for SpawnClientsNearHost");

            ParseCommandLine();

            Harmony = new Harmony(GUID);
            Harmony.PatchAll(typeof(Plugin).Assembly);   // main loop hooks (Runner.cs)
            Patches.Apply(Harmony);                         // gameplay hooks, applied one by one
            PenCapture.Patch(Harmony);
            Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.APHE));
            Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.HE));
            Shells.ShellRegistry.Register(new Shells.ExplosiveShellHandler(Shells.ShellType.APDS));
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

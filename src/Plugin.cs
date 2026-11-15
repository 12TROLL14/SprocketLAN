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
        public const string VERSION = "6.18.3";
        public const int PROTOCOL = 20;

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
        public static ConfigEntry<string> DesignReceiveFolder, DesignBackupFolder, DesignLastTarget, DesignKey, DesignAutoAccept;
        public static ConfigEntry<bool> DesignAskOthers;
        public static ConfigEntry<float> KillSoundVolume, AmmoBlastRadius, KillSoundBotWindow;
        public static ConfigEntry<bool> KillSoundOnBots;
        public static ConfigEntry<bool> ApheBurstLines;
        public static ConfigEntry<string> KillSoundFile;
        public static ConfigEntry<float> SpawnNearHostDistance;
        public static ConfigEntry<string> RepairKey;
        public static ConfigEntry<float> RepairDuration;
        public static ConfigEntry<float> RepairCancelDistance;
        public static ConfigEntry<string> ConfigKey;
        public static ConfigEntry<string> LobbyDesignFolder, LobbyKey, LobbyLastTank, ModelToolsKey, ShellHudKey; public static ConfigEntry<string> LobbyRulesCode;
        public static ConfigEntry<bool> SteamEnabled;
        public static ConfigEntry<string> HostPassword, HostVisibility;
        public static ConfigEntry<float> SteamTimeout;
        public static ConfigEntry<float> CamMaxZoom;
        public static ConfigEntry<bool> TurretsFollowGroup, KeepOwnPaint; public static ConfigEntry<string> AllTurretsKey;
        public static ConfigEntry<bool> ShellHudShown, PenCamGpu, PenCamGpuRim, PenCamPauseGc, LoadersDiag, HitchLog, WreckStop, RepairRing, RepairRingHideScoped, ScoreEnabled, ScoreHold, ScoreResetEachBattle, SightEnabled, SightScopedOnly, SightLabelHundreds;
        public static ConfigEntry<string> ScoreKey, SightKey, SightColour;
        public static ConfigEntry<float> SightConvergenceOffsetX, SightConvergenceOffsetY, SightMaxRange, SightStepMetres, SightLabelEveryMetres, SightMinRange, SightDrag, SightTickPixels, SightLineWidth, SightFontSize;
        public static ConfigEntry<float> WreckStopDelay, WreckBrake, RepairRingSize;
        public static ConfigEntry<string> RepairRingPosition;
        public static ConfigEntry<int> PenCamMaxMeshVerts, PenCamMaxDmVerts, PenCamMaxDmIndices, PenCamMaxOverlayVerts;
        public static ConfigEntry<bool> FeatureShells, FeaturePenCam, FeatureModelTools, FeatureBots, FeatureSandbox;
        // read once at start: what was actually loaded this session (the config may change while the game runs)
        public static bool ShellsOn, PenCamOn, ModelToolsOn, SandboxOn;
        public static ConfigEntry<float> LobbySpawnSpacing;
        public static ConfigEntry<bool> KillFeedBelowPenCam, CamFollowFireGroup;
        public static ConfigEntry<bool> FreeCamEnabled, KillFeedEnabled, KillFeedShowShell, KillTextShown, DeathTextShown;
        public static ConfigEntry<string> FreeCamKey;
        public static ConfigEntry<float> FreeCamSpeed, FreeCamSensitivity, KillFeedX, KillFeedY, KillFeedSize, KillFeedSeconds, KillTextX, KillTextY, KillTextSize, DeathTextX, DeathTextY, DeathTextSize;
        public static ConfigEntry<int> KillFeedLines, MaxPlayers, EngineMaxCylinders;
        public static ConfigEntry<bool> EngineUnlock, LoadersEnabled, DriveshaftUnlock;
        public static ConfigEntry<float> EngineMaxCylinderLitres, DriveshaftMaxMm;
        public static ConfigEntry<string> LoaderMultipliers;
        public static ConfigEntry<bool> DesignerPerfLog, DesignerCamClip, FastGizmos, OwnSelectionView;
        public static ConfigEntry<int> FastGizmosMinItems; public static ConfigEntry<float> FastGizmosRefresh, SelectionPointPixels;
        public static ConfigEntry<float> DesignerPerfLogAboveMs, DesignerCamMaxDistance, DesignerCamMaxPan;

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
            SteamEnabled = Config.Bind("Steam", "Enabled", true, "RESTART TO APPLY. Play through Steam: when you host, Steam friends can join from an invite (no IP, no ports, no VPN; through Valve's relays when needed). Needs steam_api64.dll next to SprocketLAN.dll and Steam running");
            HostPassword = Config.Bind("Network", "Password", "", "Password: when you host, players must type it to join (empty = none); when you join, it proves you know it without sending it (a one-time challenge)");
            HostVisibility = Config.Bind("Network", "Visibility", "Public", new BepInEx.Configuration.ConfigDescription("Public: your game shows in the lobby browser (Steam) and LAN searches. Private: only by invite or IP", new BepInEx.Configuration.AcceptableValueList<string>("Public", "Private")));
            SteamTimeout = Config.Bind("Steam", "TimeoutSeconds", 20f, "A Steam player who sends nothing for this long is disconnected");
            MaxPlayers = Config.Bind("Network", "MaxPlayers", 16, new BepInEx.Configuration.ConfigDescription("HOST: most players that can join your game (you not counted)", new BepInEx.Configuration.AcceptableValueRange<int>(1, 64)));
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
            DesignAutoAccept = Config.Bind("Designs", "AutoAccept", "Friends", new BepInEx.Configuration.ConfigDescription("Designs and settings saved without asking: from Steam friends only, from everyone, or never (the lobby's tanks for a battle are always received)", new BepInEx.Configuration.AcceptableValueList<string>("Friends", "Always", "Never")));
            DesignAskOthers = Config.Bind("Designs", "AskForOthers", true, "For designs that aren't auto-accepted: show an ACCEPT / DENY prompt. Off: refuse them silently");
            DesignPort = Config.Bind("Designs", "Port", 7780, "TCP port for design transfers (sender and receiver must use the same one)");
            DesignReceiveFolder = Config.Bind("Designs", "ReceiveFolder", "", "Folder received designs are saved to. Empty = the Blueprints/Vehicles folder of the faction you saved a design in most recently");
            DesignBackupFolder = Config.Bind("Designs", "BackupFolder", "", "Where the previous version of a design is backed up when a received design replaces it. Empty = BepInEx/config/SprocketLAN_DesignBackups");
            DesignLastTarget = Config.Bind("Designs", "LastTarget", "", "Last IP a design was sent to");
            KillSoundFile = Config.Bind("PenCam", "KillSoundFile", "Sounds/kill.wav", "Sound played when the pen cam shows a kill (WAV file; path relative to the plugin folder, or absolute)");
            KillSoundVolume = Config.Bind("PenCam", "KillSoundVolume", 1.04f, "Kill sound volume (0 = off, 1 = original, up to 3 = louder)");
            PenCamGpu = Config.Bind("PenCam", "GpuRendering", true, "Draw the pen cam with the GPU (any polygon count, no CPU projection). Off: the old CPU drawing, only if something looks wrong");
            PenCamGpuRim = Config.Bind("PenCam", "GpuSeeThrough", true, "Work the see-through (x-ray) colours out on the GPU: a small texture per frame instead of every vertex on the CPU (big tanks). Off: the CPU way");
            PenCamPauseGc = Config.Bind("PenCam", "PauseGarbageCollection", true, "Pause the game's garbage collector (a ~100 ms stop every few seconds) while a hit is worked out and its pen cam plays (15 s at most); it runs once afterwards");
            HitchLog = Config.Bind("Debug", "LogHitches", true, "Log battle frames over 60 ms (what the mod was doing, whether the game's garbage collector ran)");
            PenCamMaxMeshVerts = Config.Bind("PenCam", "MaxMeshVertices", 2000000, "Render meshes with more vertices than this are left out of the pen cam");
            PenCamMaxDmVerts = Config.Bind("PenCam", "MaxDamageModelVertices", 500000, "Damage-model meshes (armour model) with more vertices than this are left out");
            PenCamMaxDmIndices = Config.Bind("PenCam", "MaxDamageModelIndices", 1500000, "Damage-model meshes with more triangle indices than this are left out");
            PenCamMaxOverlayVerts = Config.Bind("PenCam", "MaxOverlayChunkVertices", 500000, "Largest 2D overlay chunk (vertices); also the most a module can have on the CPU fallback path");
            KillSoundBotWindow = Config.Bind("PenCam", "KillSoundBotWindowSeconds", 10f, "An AI tank counts as your kill if it is knocked out (crew or ammo rack) up to this long after your last hit on it");
            KillSoundOnBots = Config.Bind("PenCam", "KillSoundOnBots", true, "Play the kill sound when you destroy an AI tank too");
            ApheBurstLines = Config.Bind("PenCam", "APHEBurstShowFullLines", true, "After APHE burst fragments stop, leave their whole path drawn as a line");
            AmmoBlastRadius = Config.Bind("PenCam", "AmmoRackBlastRadius", 2.2f, "Radius (m) of the red explosion drawn on a destroyed ammo rack");
            RepairKey = Config.Bind("Repair", "Key", "F6", "Key that starts/cancels a field repair (Unity Input System key name, e.g. F6, R, Numpad0)");
            RepairDuration = Config.Bind("Repair", "DurationSeconds", 30f, "How long a field repair takes");
            ScoreEnabled = Config.Bind("Scoreboard", "Enabled", true, "Show the scoreboard with its key");
            ScoreKey = Config.Bind("Scoreboard", "Key", "Tab", "Key for the scoreboard (Unity Input System key name)");
            ScoreHold = Config.Bind("Scoreboard", "HoldToShow", true, "On: shown while the key is held. Off: the key toggles it");
            ScoreResetEachBattle = Config.Bind("Scoreboard", "ResetEachLobbyBattle", true, "Start from zero when the host starts a new lobby battle (it always resets when you host or join)");
            SightEnabled = Config.Bind("Sight", "RangeMarks", true, "Range-marked trajectory curve in the sight (shows drop and gun / sight parallax for the selected fire group and shell)");
            SightKey = Config.Bind("Sight", "ToggleKey", "None", "Key that turns the range marks on / off (None = no key)");
            SightScopedOnly = Config.Bind("Sight", "OnlyWhenScoped", true, "Only draw the range marks while looking through a sight");
            SightMaxRange = Config.Bind("Sight", "MaxRangeMetres", 3000f, "Furthest range mark");
            SightMinRange = Config.Bind("Sight", "MinRangeMetres", 100f, "Closest range mark");
            SightStepMetres = Config.Bind("Sight", "StepMetres", 100f, "Distance between small marks");
            SightLabelEveryMetres = Config.Bind("Sight", "LabelEveryMetres", 500f, "Distance between labelled (long) marks");
            SightLabelHundreds = Config.Bind("Sight", "LabelInHundreds", true, "Label marks in hundreds of metres (5 = 500 m), like real sights");
            SightDrag = Config.Bind("Sight", "DragPerMetre", 0f, "Air drag used for the curve (0 = gravity only). Raise it if long-range shots land below the marks");
            SightColour = Config.Bind("Sight", "Colour", "#FFD93DE6", "Colour of the marks (#RRGGBB or #RRGGBBAA)");
            SightTickPixels = Config.Bind("Sight", "TickPixels", 28f, "Width of the labelled marks in pixels (small marks are half)");
            SightLineWidth = Config.Bind("Sight", "LineWidth", 2f, "Thickness of the marks in pixels");
            SightConvergenceOffsetX = Config.Bind("Sight", "ConvergenceTextOffsetX", 140f, "Moves the game's gun convergence readout (e.g. '300m') sideways, in pixels (0 = where the game puts it)");
            SightConvergenceOffsetY = Config.Bind("Sight", "ConvergenceTextOffsetY", 0f, "Moves the game's gun convergence readout up (+) / down (-), in pixels");
            SightFontSize = Config.Bind("Sight", "FontSize", 16f, "Size of the range labels");
            RepairRing = Config.Bind("Repair", "ShowProgressRing", true, "Show a progress ring while a field repair runs");
            RepairRingPosition = Config.Bind("Repair", "ProgressRingPosition", "Center", new BepInEx.Configuration.ConfigDescription("Where the repair ring is drawn", new BepInEx.Configuration.AcceptableValueList<string>("Center", "BelowCenter", "BottomCenter", "TopRight")));
            RepairRingHideScoped = Config.Bind("Repair", "ProgressRingThirdPersonOnly", true, "Hide the repair ring while looking through a sight (only shown in the third-person view)");
            RepairRingSize = Config.Bind("Repair", "ProgressRingSize", 96f, "Diameter of the repair ring in pixels");
            WreckStop = Config.Bind("Battle", "StopKnockedOutAiTanks", true, "Knocked-out AI tanks (crew or ammo rack) shut their engine down and brake, instead of driving on");
            WreckStopDelay = Config.Bind("Battle", "KnockedOutAiStopDelaySeconds", 1f, "Seconds after being knocked out before an AI tank stops");
            WreckBrake = Config.Bind("Battle", "KnockedOutAiBrake", 3f, "How hard a knocked-out AI tank brakes (m/s per second; 0 = just engine off, it rolls to a stop)");
            RepairCancelDistance = Config.Bind("Repair", "CancelIfMovedMetres", 2f, "Repair is cancelled if the tank moves further than this (0 = never cancel)");
            ConfigKey = Config.Bind("Settings", "MenuKey", "F7", "Key that opens the in-game settings panel");
            FeatureShells = Config.Bind("Features", "ShellsAndArmour", true, "RESTART TO APPLY. The mod's shells (APHE, HE, APDS, APFSDS, HEAT, caps, autocannons), its armour / penetration model and fragments. Off: every hit is the game's own (the mod only syncs it). Turn off if another ballistics mod conflicts");
            FeaturePenCam = Config.Bind("Features", "PenCam", true, "RESTART TO APPLY. The pen cam / kill cam replays (and sending them to the shooter)");
            FeatureModelTools = Config.Bind("Features", "ModellingTools", true, "RESTART TO APPLY. The extra modelling tools in the compartment editor (not even hooked when off)");
            FeatureBots = Config.Bind("Features", "ModShellsOnBots", true, "The mod's shells, armour model and pen cam also for AI tanks (your shots at them, theirs at you), offline too");
            FeatureSandbox = Config.Bind("Features", "SandboxArmourInspector", true, "RESTART TO APPLY. The mod's changes to the sandbox armour inspector (firing the mod's shells with caliber / pen / filler / fuse settings). Off: the inspector is the game's own. Needs ShellsAndArmour");
            ShellsOn = FeatureShells.Value; PenCamOn = FeaturePenCam.Value; ModelToolsOn = FeatureModelTools.Value; SandboxOn = ShellsOn && FeatureSandbox.Value;
            ShellHudKey = Config.Bind("Settings", "ShellHudKey", "Home", "Key that shows / hides the shell selector text at the top right in battle (None = no key). Not F1-F5: the game uses those for the fire groups");
            if (ShellHudKey.Value == "F3") { ShellHudKey.Value = "Home"; MPLog.Info("Shell selector key moved from F3 (fire group 3) to Home"); }
            ShellHudShown = Config.Bind("Settings", "ShellHudShown", true, "Show the shell selector text at the top right in battle (the key above toggles it, and it's remembered)");
            ModelToolsKey = Config.Bind("Settings", "ModellingToolsKey", "F4", "Key that shows / hides the modelling tools panel in the compartment editor");
            LobbyKey = Config.Bind("Lobby", "MenuKey", "F9", "Key that opens the multiplayer lobby");
            LobbyDesignFolder = Config.Bind("Lobby", "DesignFolder", "", "Folder the other players' tanks are saved to for lobby battles. Empty = Documents/My Games/Sprocket/SprocketLAN Multiplayer");
            LobbyRulesCode = Config.Bind("Lobby", "TankRules", "", "Tank rules you set as a lobby host (era range, max weight, max cost), kept for the next time you host. Set in the lobby [F9]");
            LobbyLastTank = Config.Bind("Lobby", "LastTank", "", "The tank you picked last in the lobby (picked again automatically)");
            LobbySpawnSpacing = Config.Bind("Lobby", "SpawnSpacingMetres", 15f, "Distance between teammates spawning side by side");
            Shells.ShellConfig.Bind(Config);
            ShellTestFireKey = Config.Bind("Shells", "ShellTestFireKey", "F8", "Fires the selected test shell at the mouse cursor (vehicle designer, test armed)");
            StabKey = Config.Bind("Stabiliser", "ToggleKey", "B", "Key that turns the stabiliser on/off (faster turret traverse and gun elevation)");
            StabMultiplier = Config.Bind("Stabiliser", "SpeedMultiplier", 4f, "How many times faster the turret traverses with the stabiliser on");
            StabElevationMultiplier = Config.Bind("Stabiliser", "ElevationMultiplier", 6f, "How many times faster the gun / mantlet elevates with the stabiliser on");
            ShellTestKey = Config.Bind("Shells", "ShellTestKey", "F9", "Shows/hides the shell test row in the vehicle designer (next to the Ammo Rack panel)");
            {
                BepInEx.Configuration.ConfigDescription D(string d, float lo, float hi) => new BepInEx.Configuration.ConfigDescription(d, new BepInEx.Configuration.AcceptableValueRange<float>(lo, hi));
                EngineUnlock = Config.Bind("Engine", "UnlockLimits", true, "Engine designer: allow more cylinders and more displacement than the game's limits (24 cylinders, 5 l per cylinder). Designs keep working without the mod, but the unmodded game may bring them back to its limits");
                DriveshaftUnlock = Config.Bind("Engine", "UnlockDriveshaftThickness", true, "Powertrain designer: use MaxDriveshaftMm instead of the game's 400 mm driveshaft thickness limit (big unlocked engines need thicker shafts)");
                DriveshaftMaxMm = Config.Bind("Engine", "MaxDriveshaftMm", 800f, D("Thickest driveshaft allowed with UnlockDriveshaftThickness (the game's limit is 400 mm)", 100f, 5000f));
                EngineMaxCylinders = Config.Bind("Engine", "MaxCylinders", 64, new BepInEx.Configuration.ConfigDescription("Most cylinders with UnlockLimits (the design file stores up to 255)", new BepInEx.Configuration.AcceptableValueRange<int>(1, 255)));
                EngineMaxCylinderLitres = Config.Bind("Engine", "MaxDisplacementLitres", 25f, D("Largest displacement (per cylinder, the designer's Displacement slider) with UnlockLimits", 1f, 200f));
                LoadersEnabled = Config.Bind("Loaders", "ExtraLoaders", true, "Crew seats can be made extra loaders of a gun (select a crew seat in the designer): faster reloads. Kept in BepInEx/config/SprocketLAN_loaders.txt, not in the design");
                LoadersDiag = Config.Bind("Loaders", "ReloadLog", true, "Log what a sped-up reload is doing once a second (to check the countdown); turn off once it's right");
                LoaderMultipliers = Config.Bind("Loaders", "ReloadMultipliers", "1,1.5,2,3,4,5", "Reload speed for 1, 2, 3... loaders (the gun's own loader counts as 1). Past the end of the list: +1 per extra loader");
                DesignerCamClip = Config.Bind("Designer", "CameraClipPrevention", false, "The designer camera pushes itself out of the vehicle's armour (the game's behaviour). Off: it never jumps around big vehicles, and can go inside them");
                DesignerCamMaxDistance = Config.Bind("Designer", "CameraMaxZoomOutMetres", 120f, D("How far the designer camera can zoom out (the game's own limit is used if it's bigger)", 5f, 1000f));
                DesignerCamMaxPan = Config.Bind("Designer", "CameraMaxPanMetres", 60f, D("How far the designer camera can pan away from the vehicle (the game's own limit is used if it's bigger)", 5f, 1000f));
                FastGizmos = Config.Bind("Designer", "FastSelectionDisplay", true, "Big structures: draw the selected structure's edges and points as a few meshes instead of one object each (much faster; edges are 1 pixel thin). Off: the game's own way");
                OwnSelectionView = Config.Bind("Designer", "OwnSelectionDisplay", true, "Points / Edges / Faces modes on big structures: the mod draws the points, edges and selected faces itself from the mesh (like 3D programs do: updated only when something changes) and the game's slow redraw is skipped. Off: the game's own display (or the fast display above)");
                SelectionPointPixels = Config.Bind("Designer", "OwnSelectionPointPixels", 3f, D("Size of the points of the own selection display, in pixels (at the structure's distance). Also in the modelling tools panel", 1f, 30f));
                if (UnityEngine.Mathf.Approximately(SelectionPointPixels.Value, 7f)) SelectionPointPixels.Value = 3f;   // the old (too big) default
                FastGizmosMinItems = Config.Bind("Designer", "FastSelectionMinItems", 1500, new BepInEx.Configuration.ConfigDescription("The fast display is used for structures with at least this many edges + points", new BepInEx.Configuration.AcceptableValueRange<int>(100, 100000)));
                FastGizmosRefresh = Config.Bind("Designer", "FastSelectionHoverRefreshSeconds", 0.25f, D("While the mouse moves, the selection display is redrawn this often (for the hover highlight). Changes to the selection or the mesh redraw it at once", 0.05f, 2f));
                DesignerPerfLog = Config.Bind("Designer", "PerformanceLog", true, "RESTART TO APPLY. When the designer runs slowly, write to the log where the frame time goes (for bug reports). Off: not even hooked");
                DesignerPerfLogAboveMs = Config.Bind("Designer", "PerformanceLogAboveMs", 25f, D("Only log when frames take longer than this on average (ms)", 5f, 1000f));
                FreeCamEnabled = Config.Bind("FreeCam", "Enabled", true, "Allow the free camera in battle (alive or destroyed)");
                FreeCamKey = Config.Bind("FreeCam", "Key", "F5", "Key that turns the free camera on / off (Unity Input System key name). While it is on, your tank gets no input");
                FreeCamSpeed = Config.Bind("FreeCam", "SpeedMetresPerSecond", 15f, D("Free camera speed (Shift x4, Alt x0.25, the mouse wheel changes it while flying)", 1f, 200f));
                FreeCamSensitivity = Config.Bind("FreeCam", "MouseSensitivity", 1f, D("Free camera mouse look sensitivity", 0.1f, 5f));
                KillFeedEnabled = Config.Bind("KillFeed", "Enabled", true, "Kill feed: a list of every kill (players and AI tanks) with the shell and the cause");
                CamFollowFireGroup = Config.Bind("Battle", "CameraFollowsFireGroup", true, "Multi-turret tanks: the third-person camera centres on the turret of the selected fire group's gun (instead of the game's choice)");
            CamMaxZoom = Config.Bind("Battle", "CameraMaxZoomOut", 40f, "How far (m) the third-person camera can zoom out in battle (the game's limit is ~15 m, tight for huge tanks). 0 = the game's");
            TurretsFollowGroup = Config.Bind("Battle", "TurretsFollowFireGroup", true, "Only the turrets carrying a gun of the selected fire group follow your aim (the others hold their direction). AllTurretsKey makes them all follow again until you pick another fire group");
            KeepOwnPaint = Config.Bind("Battle", "TanksKeepOwnPaint", true, "In multiplayer battles every tank wears its own paint job and decals (the battle setup's team paint scheme is dropped)");
            AllTurretsKey = Config.Bind("Battle", "AllTurretsKey", "Backquote", "Key that makes every turret follow your aim (again to go back to the fire group's). Backquote = the key left of 1 (² on AZERTY). None = no key");
                KillFeedBelowPenCam = Config.Bind("KillFeed", "BelowPenCam", true, "Put the kill feed on the right, just under the pen cam (shown or not). Off: PositionX / PositionY below");
                KillFeedX = Config.Bind("KillFeed", "PositionX", 1f, D("Horizontal position, % of the screen width (0 = left edge, 100 = right edge). Near an edge the lines line up on that edge", 0f, 100f));
                KillFeedY = Config.Bind("KillFeed", "PositionY", 97f, D("Vertical position, % of the screen height (0 = bottom, 100 = top)", 0f, 100f));
                KillFeedSize = Config.Bind("KillFeed", "SizePercent", 100f, D("Size of the kill feed (100 = normal)", 30f, 300f));
                KillFeedSeconds = Config.Bind("KillFeed", "LineSeconds", 30f, D("How long a kill stays in the feed", 3f, 600f));
                KillFeedLines = Config.Bind("KillFeed", "MaxLines", 6, new BepInEx.Configuration.ConfigDescription("Most kills shown at once", new BepInEx.Configuration.AcceptableValueRange<int>(1, 20)));
                KillFeedShowShell = Config.Bind("KillFeed", "ShowShell", true, "Show the shell type the killer last fired (AP, APHE...)");
                KillTextShown = Config.Bind("HUD", "KillTextShown", true, "The big \"X destroyed Y\" text at the top of the screen (also shows repair notices)");
                KillTextX = Config.Bind("HUD", "KillTextPositionX", 50f, D("Kill text horizontal position, % of the screen width", 0f, 100f));
                KillTextY = Config.Bind("HUD", "KillTextPositionY", 95.4f, D("Kill text vertical position, % of the screen height (0 = bottom)", 0f, 100f));
                KillTextSize = Config.Bind("HUD", "KillTextSizePercent", 100f, D("Kill text size (100 = normal)", 30f, 300f));
                DeathTextShown = Config.Bind("HUD", "DestroyedTextShown", true, "The \"CREW KNOCKED OUT / AMMO RACK EXPLOSION\" banner when your tank is destroyed");
                DeathTextX = Config.Bind("HUD", "DestroyedTextPositionX", 50f, D("Destroyed banner horizontal position, % of the screen width", 0f, 100f));
                DeathTextY = Config.Bind("HUD", "DestroyedTextPositionY", 61f, D("Destroyed banner vertical position, % of the screen height (0 = bottom)", 0f, 100f));
                DeathTextSize = Config.Bind("HUD", "DestroyedTextSizePercent", 100f, D("Destroyed banner size (100 = normal)", 30f, 300f));
            }
            SpawnNearHost = Config.Bind("Battle", "SpawnClientsNearHost", false, "HOST ONLY: spawn joining players in a row in front of the host instead of at the AI team's spawn (handy for testing)");
            SpawnNearHostDistance = Config.Bind("Battle", "SpawnClientsNearHostDistance", 35f, "HOST ONLY: distance in metres in front of the host for SpawnClientsNearHost");

            ParseCommandLine();

            Harmony = new Harmony(GUID);
            Harmony.PatchAll(typeof(Plugin).Assembly);   // main loop hooks (Runner.cs)
            Patches.Apply(Harmony);                         // gameplay hooks, applied one by one
            FreeCam.Patch(Harmony);
            if (DesignerPerfLog.Value) DesignerTweaks.Patch(Harmony);
            SprocketMP.FastGizmos.Patch(Harmony);
            SelectionView.Patch(Harmony);
            EngineLimits.Patch(Harmony);
            DriveshaftLimits.Patch(Harmony);
            try { PuppetPaint.Patch(Harmony); } catch (Exception e) { MPLog.Warn("paint: " + e.Message); }
            try { TurretSelect.Patch(Harmony); } catch (Exception e) { MPLog.Warn("turret select: " + e.Message); }
            Loaders.Patch(Harmony);
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

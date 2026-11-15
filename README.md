# SprocketLAN — multiplayer for Sprocket (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.780+3be4532, .NET 6)

Version **6.18.3** (protocol 20). Every player needs the same protocol: a different one is refused when joining
(everyone needs 6.10.0 or newer).

> **OPEN BETA.** Expect bugs: the newest parts have had little testing in real games. They are the F8 / F9 / F10
> menus, the lobby's tank rules, decal and paint sharing, and the Steam DLL finder.
> **Reporting a bug:** say what you did and what happened, and attach `BepInEx/LogOutput.log` and
> `BepInEx/SprocketLAN_<pid>.log` (the newest one) from **every player involved**. For a hit problem that means the
> shooter's and the hit tank's. Read **Network safety** below before playing with people you don't know.

## Install
1. Install **BepInEx 6  (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.780+3be4532, .NET 6)** in the Sprocket folder and start the game once.                                                         https://builds.bepinex.dev/projects/bepinex_be<img width="1355" height="885" alt="build" src="https://github.com/user-attachments/assets/6f280e02-fdd4-4872-91a5-7e1aa3e89b6b" />

2. Copy `BepInEx.cfg` from the release to `BepInEx\config\` (the default config WILL result in a crash ! use the config from release.)
3. Launch the game once until it reaches the main menu then close it. 
4. Copy the `SprocketLAN.dll` from this release into `BepInEx/plugins/`, so you get
   `BepInEx/plugins/SprocketLAN/SprocketLAN.dll` and `BepInEx\config\BepInEx.cfg`. (create `SprocketLAN` folder if you don't have it in plugins)
   Or Copy `SprocketLAN_v6.18.3.zip` from the release to the game folder and extract it there.
5. Start the game: the **SPROCKET LAN** panel appears (F10 hides / shows it).

**Linux (Proton):** BepInEx loads through its `winhttp.dll`, so Wine has to be told to use it. Either set Steam's
launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`, or set it once in the game's Proton prefix:
`protontricks -c 'wine reg add "HKCU\Software\Wine\AppDefaults\Sprocket.exe\DllOverrides" /v winhttp /d native,builtin /f' 1674170`.
The second way also works when the game isn't started from Steam.

## Play
1. One player: F10 → **HOST GAME** (TCP 7777 by default; forward it for internet play, LAN works as is).
   Others: F10 → **FIND GAMES** and click the game, or type `host-ip:port` → **JOIN**.
2. Everyone opens the **MULTIPLAYER LOBBY** (F9): pick your tank and team, click **READY**.
   The host picks the map and team spawns and clicks **START BATTLE**: every player's battle starts by itself.
3. Use the same armour / shell settings as everyone else: F8 → **SEND MY ARMOUR / SHELL SETTINGS**.

Manual way (no lobby): everyone opens **Custom Battle** with their own tank on their team and every other
player's tank (the exact same design file) on the AI team.

### Getting steam_api64.dll (needed for Steam play only)
The DLL belongs to Valve and isn't included in this release. Put a **64-bit `steam_api64.dll` from Steamworks SDK
1.58 or newer** (1.61 and older work through an older friends interface; newer is better) next to `SprocketLAN.dll` (`BepInEx/plugins/SprocketLAN/`). Two ways to get one:
- **Steamworks SDK:** download it for free at <https://partner.steamgames.com/downloads/list> (any Steam
  account, after accepting the licence) and copy `sdk/redistributable_bin/win64/steam_api64.dll`.
- **From another Steam game you own** that was updated recently: look for `steam_api64.dll` in its folder
  (Steam › right-click the game › Manage › Browse local files). Older games ship an older version that won't work.

**Easiest: F10 › STEAM › FIX IT.** When Steam is off because the DLL is missing or unusable, F10 shows a **FIX IT**
button next to the reason. It asks first, then:
- looks through the games in all your Steam libraries;
- checks each `steam_api64.dll` it finds (64-bit, and it must have every Steam function the mod uses);
- copies the newest one that fits next to `SprocketLAN.dll`. Nothing else is changed.

Only **Valve's own, unmodified** `steam_api64.dll` is installed: its digital signature must verify, and the signer
must be Valve Corp. A DLL that matches but can't be verified is never installed automatically. The status line
says which game it's in (the full path is in the log), and you can copy it yourself if you trust it.

If the DLL was missing, Steam starts right away. If an unusable one had been loaded, it's renamed to
`steam_api64.dll.old` and you restart the game.

The mod checks the file when the game starts. F10 shows **Steam: ... relays ready** in green when it works, or in
red what's wrong, for example "too old, Steamworks SDK 1.58 or newer is needed". Without the DLL, everything except
Steam play works as usual.

### Playing through Steam (no IP, no ports, no VPN)
1. The host opens F10 and clicks **INVITE STEAM FRIENDS**, which opens Steam's invite window, or **INVITE FRIENDS
   IN SPROCKET**, which invites every friend currently in the game. Either one starts hosting if needed.
2. Friends accept the invite in Steam, or click **Join Game** on the host in their Steam friends list. Steam's
   own **Invite to game** (right-click a friend) works too. If
   Sprocket isn't running, Steam starts it, and they join automatically.

Traffic goes direct when the routers allow it, otherwise through Valve's relay servers. The host accepts Steam
players and LAN / Radmin players at the same time. This needs `steam_api64.dll` (see above) and Steam running. `[Steam] Enabled` turns it off. F8 sends to players in the game through the
game connection, so it works through Steam too.

### Lobby browser, public / private, password
- **FIND GAMES** (F10) lists public Steam games worldwide and SprocketLAN games on your network. Click one to
  join. Games marked [PASSWORD] need the password typed in the **Password** field first. The **Search** box
  filters by host name, and **< >** turns the pages.
- **PUBLIC / PRIVATE** (the button next to Password) decides whether your game shows in the browser and in LAN
  searches. Private games are reachable only by Steam invite or IP.
- **Password:** when you host, players must type it to join. When you join, the same field is used.

### Finding games
Press **FIND GAMES** in the F10 menu to list the hosts on your local network (and public Steam games), then click one to
join. This uses UDP port 7778 (setting `DiscoveryPort`), and it only works on the same network: LAN or the same
Wi-Fi, not over the internet. Over a VPN such as Radmin or ZeroTier it may or may not work. If nothing shows up,
allow Sprocket through the Windows firewall on the host, or join by IP.

## Keys (all configurable)
| Key | Action |
|---|---|
| F10 | Multiplayer menu (host / join / disconnect, stabiliser, settings) |
| F9 | Multiplayer lobby |
| F7 | In-game settings panel |
| F8 | Send designs / settings to other players |
| F3 | Show / hide the shell selector text (top right, battle) |
| Tab | Scoreboard (hold) |
| F4 | Modelling tools panel (compartment editor) |
| F6 | Field repair (hold position; cancelled if you move) |
| F5 | Free camera (WASD, Q/E, Shift, mouse wheel = speed; F5 to leave) |
| B | Stabiliser on/off |
| 1-6 | Load AP / APHE / HE / APDS / APFSDS / HEAT (reloads the guns of the active fire group) |
| F11 | Diagnostics dump to the log |

## Features
### Armour and penetration (on the game's own armour model)
Every hit is worked out by the mod on the **game's own armour model** — the one the designer's armour
view shows — instead of the game's penetration simulation:
- Plates are solids with their raw thickness (designer value); the shell needs raw / cos(angle).
  Applique and add-on armour (fenders, spare tracks) count by material along the path. Wheels,
  sprockets and idlers are not armour.
- Ricochet angles per shell (AP/APHE 68°, APC/APHEC 70°, APDS 75°, APFSDS 78°, HEAT 82°), overmatch
  (calibre ≥ 3.5× thickness), 30% penetration lost per bounce. **Shot traps**: a shell bouncing off the
  outside flies on and can hit another plate.
- Leaving a plate the shell turns away from the normal (`ExitNormalizationAt60/70`, reduced for
  APDS / APFSDS / caps; applique: `AppliqueExitTurnDegrees`, 0 by default).
- Spall cone behind every penetrated plate; spall, APHE and HE fragments are traced through the same
  model (they stop in armour, bounce, damage the modules they enter; crew/modules let spall through
  at `SpallThroughModuleSpeed`; outside, wheels and external parts count by their material).
- APHE: fuse arming thickness + delay, spherical burst; bounces by filler. HE bursts on the first thing
  it touches (armour, wheel, track), out of any steel, and gets in only if its penetration beats the plate.
  HEAT: jet loses penetration per metre between plates.
- Tracks have a hitbox band around the wheels (a shell breaks the track, fragments damage it).
  Ammo racks are tougher (×4). A repaired engine restarts.

### Per-gun shell settings and autocannons
In the designer the **Shells** panel opens next to the **Ammo Rack** panel: APHE / HE filler (capped to
what the gun's shell holds), APHE fuse, AP/APHE caps, and **Autocannon** with a rate of fire
(rounds/min; penetration ÷ `AutocannonPenetrationDivisor`, 1.5 by default). Settings are saved per
design **and per gun** (by the rack's calibre, e.g. 20x200mm) in `BepInEx/config/SprocketLAN_shells.txt`.

### Modelling tools (compartment editor)
A panel in the compartment editor ([F4] shows / hides it) adds: **inset face** and **polygon hole** (by a distance),
**round hole** / **round inset** (number of edges, radius - shrunk to fit the face; convex faces) and **connect 2
vertices** (knife). Select, then click; Ctrl+Z undoes like any other edit.
**Delete structure** deletes the structure being edited, for parts that can't be dragged off the vehicle. Click it
twice to confirm; Ctrl+Z brings the structure back.

**Designer camera** (F7 › Designer): the game's camera pushes itself out of the vehicle's armour, which makes it
jump around very big vehicles. That's off by default (`CameraClipPrevention`), and the zoom-out and pan limits
are raised.

**Own selection display** (F7 › Designer): the game redraws every point and edge of the selected structure as a
separate object, which brings big structures down to a few fps. For structures with more than 1500 points + edges,
in the Points, Edges and Faces modes, the mod draws them itself, the way 3D programs do. It reads the mesh from the
editor, keeps it in a few static meshes (edges as lines, points as small cubes, selected faces filled), and
rebuilds only what changed. The game's own redraw is skipped. Point size: "point px" in the modelling tools panel. Corners mode uses `FastSelectionDisplay` (the game's
drawing batched). Turn `OwnSelectionDisplay` off for the game's own display.

### Sandbox shell testing
In the vehicle designer's armour inspector (**Sample** mode) a panel offers the mod's shells: type (AP, APC, APHE,
APHEC, HE, APDS, APFSDS, HEAT), calibre, AP penetration, filler and APHE fuse (sensitivity, delay). Clicking the
tank (hull or turret) fires that shell with the mod's rules from 3 m outside the armour; its path, spall and
bursts are drawn on the tank. **MOD SHELLS: OFF** gives the game's own simulation back.

### Pen cam
Replay of each of your hits (and a kill cam when you die), drawn from the game's armour model coloured
by thickness: the shell's real path (bounces included), every struck plate highlighted, spall cones,
APHE / HE bursts, damaged modules, ammo rack explosions. Fragments that hit nothing are not drawn.
The pen cam is drawn entirely by the GPU, so high-poly hulls and turrets are fine and it doesn't stutter;
mesh size limits are the `[PenCam] Max...` settings (`GpuRendering` off = the old CPU drawing).
A kill sound plays when your hit destroys a tank (`[PenCam] KillSoundFile` / `KillSoundVolume`).
Impact marks (holes, dents, ricochet scars) show on the tank that was hit, your own included.

### AI tanks
Shooting AI tanks (offline or in multiplayer) uses the mod's shells and armour model too, with a pen cam
titled "AI tank" and the kill sound (`[PenCam] KillSoundOnBots`). AI tanks firing at you fire AP through the
mod's armour model. `[Features] ModShellsOnBots` turns all of this off. A bot counts as your kill if it's
knocked out up to `[PenCam] KillSoundBotWindowSeconds` (10 s) after your last hit.
Knocked-out AI tanks shut their engine down and brake instead of driving on (`[Battle] StopKnockedOutAiTanks`,
`KnockedOutAiStopDelaySeconds`, `KnockedOutAiBrake`).

### Kill feed and HUD
The kill feed (on the right, under the pen cam) lists the latest kills: killer, the shell they last fired, victim and cause. F7 ›
KillFeed sets whether it's shown, where it is (X/Y in % of the screen), its size and how long lines stay. The big
kill text at the top and the "destroyed" banner can be hidden, moved and resized in F7 › HUD.

### Free camera (F5)
Fly the camera around the battle, alive or destroyed. The game's controls are paused while it's on (your tank
gets no input), and F5 returns to your tank (Esc still opens the pause menu).

### Engine limits (F7 › Engine)
The engine designer allows up to 64 cylinders (MaxCylinders, up to 255) and 25 l per cylinder
(MaxDisplacementLitres) instead of the game's 24 and 5 l. Turn `UnlockLimits` off for the game's limits. Designs
open without the mod, but the unmodded game may bring them back to its own limits. The driveshaft thickness limit is
800 mm instead of 400 (`UnlockDriveshaftThickness`, `MaxDriveshaftMm`).

### Extra loaders (F7 › Loaders)
Select a crew seat with no job in the designer: the EXTRA LOADER panel (bottom right) makes that crew member an
extra loader of a gun (< > to choose the gun, or none). Reload speed with the gun's own loader counted as 1: 1 loader x1, 2 x1.5,
3 x2, 4 x3, 5 x4, 6 x5, then +1 per loader (ReloadMultipliers). The assignments are saved in
`BepInEx/config/SprocketLAN_loaders.txt`, not in the design file, so designs stay compatible. In battle only the
extra loaders that are alive count: a killed loader that isn't replaced slows the reload again. The shell HUD shows
the loaders and the multiplier.

### Field repair (F6)
Hold position for `[Repair] DurationSeconds` (30 s); moving more than `CancelIfMovedMetres` cancels it. A progress
ring shows the time left in the third-person view (`ShowProgressRing`, `ProgressRingPosition`: Center /
BelowCenter / BottomCenter / TopRight, `ProgressRingSize`, `ProgressRingThirdPersonOnly`).

### Multiplayer lobby (F9)
- **Tank rules** (host, TANK RULES in the lobby): era range, max weight (t), max cost. A tank's era comes from its
  creation date, its weight and cost from the design. Players can't READY with a tank that breaks them, the host
  checks every tank itself before START, and the rules show in the game browser. Kept for the next time you host
  (`[Lobby] TankRules`).
- Players: pick your tank (any of your designs) and team, then READY. Your tank is sent to the others
  over the game connection into `Documents/My Games/Sprocket/SprocketLAN Multiplayer` (`[Lobby] DesignFolder`),
  with its shell settings; a design saved again is re-sent.
- Host: map, 2-4 teams, each team's spawn: the map's own spawn **A** or **B**, or **PLACE** it on the map
  overview. The overview is drawn from the terrain the first time you are in a battle on that map
  (kept in `BepInEx/config/SprocketLAN_maps`). Teammates spawn side by side (`SpawnSpacingMetres`) facing
  the other teams.
- START (when everyone is ready, has every tank and is in the menus): each game writes its own Custom
  Battle setup (own tank on the player team, all others on the AI team), opens Custom Battle and confirms
  it. If the game can't open the screen itself, open **Custom Battle**: it is already filled in.
- There is no friendly-fire protection: teammates are hit like anyone else.

### Scoreboard (Tab)
Kills, deaths, K/D, shots, hits, penetrations, accuracy and ping for every player (hits come from the pen cam
records, so the pen cam must be on). `[Scoreboard]`: `Enabled`, `Key`, `HoldToShow`, `ResetEachLobbyBattle`.

### Range-marked sight
While scoped in, a curve of range marks shows where the selected fire group's shell will be at each distance:
the drop and the parallax between the gun and the sight together, for the selected shell's velocity. It calibrates
from your first shot with each shell. `[Sight]`: `RangeMarks`, `ToggleKey`, `OnlyWhenScoped`, `MinRangeMetres`,
`MaxRangeMetres`, `StepMetres`, `LabelEveryMetres`, `LabelInHundreds`, `DragPerMetre`, `Colour`, `TickPixels`,
`LineWidth`, `FontSize`, `ConvergenceTextOffsetX/Y`.
The white "300m" is the game's own gun convergence (zeroing) distance; the marks include it.

### Stabiliser
B or the STAB button: turret traverse ×4 and gun elevation ×6 (separate multipliers).

### Turning features off (F7 › Features, restart needed)
- `ShellsAndArmour`, `PenCam` and `ModellingTools` can each be switched off, and are then not loaded at all.
- `SandboxArmourInspector` turns off the mod's changes to the sandbox armour inspector.
- `ModShellsOnBots` controls whether the mod's shells and pen cam are used against AI tanks.

### In-game settings (F7)
Every setting with sliders / toggles / text boxes and reset buttons; a live diagram for the geometric
ones (spall cones, exit turn, ricochet angles, overmatch, HE burst offsets, APHE fuse, HEAT jet).
Changes apply immediately and are saved.

### Sharing (F8)
- Click a player under **Players** to send through the game connection (works over Steam). Typing an IP sends
  directly instead.
- **Auto-accept from:** STEAM FRIENDS / EVERYONE / NOBODY. For the rest, **OTHERS: ASK ME** gives ACCEPT / DENY
  buttons in F8, and **REFUSE SILENTLY** drops them without a notification.
- Designs (blueprint + picture) to any player or IP; a replaced design is backed up.
- Gameplay settings (Armour, Shells, Combat, Repair, Stabiliser; not keys, names, folders, network,
  pen cam): the receiver sees the differences and chooses APPLY or DISCARD (old config backed up).
- Hits are worked out on the tank that is hit with that player's settings: everyone should use the same.

## Network safety
What the mod protects against, and what it can't:
- **Joining:** a host accepts at most `[Network] MaxPlayers` players (16 by default). A connection that doesn't
  join within 10 s is closed, and at most 16 can wait at once. After 5 wrong passwords from the same address or
  Steam account, that address is locked out for 30 s, then 60 s, and so on.
- **Password:** never sent, not even as a hash: the host sends a one-time challenge and your game answers with
  proof that it knows the password. Someone watching the network can't reuse it, and a host you join doesn't
  learn it.
- **What other players send is checked:** lengths and sizes are bounded, compressed data can't unpack to more
  than a set size, and positions, speeds, health and shell values are kept in range. Names and chat are shown
  as plain text (no formatting tags). The host limits how many messages of each kind a player may send a second
  (extra ones aren't passed on to the others); a player who keeps flooding is disconnected. Shots that start far
  from the shooter's own tank are ignored. Pen cam records are capped and their numbers kept in range.
- **Files:** received designs are only written under their sanitised name, in the receive folder (F8) or the
  lobby folder (in the lobby, only the design that player picked). A battle only starts on a map you have. Nothing else on your PC can be written or read. Shell settings that come with a design only
  change that design's settings. In the lobby, they never replace the settings of a design of your own with
  the same name.
- **F8 transfers:** "Steam friends" auto-accept uses the Steam id of the actual Steam connection (as the host
  sees it), not what the sender claims. Settings are never applied without you clicking APPLY.
  - By default, designs from your **Steam friends are saved without asking** (a design you already have with the
    same name is backed up first). Set F8 › Receiving to NOBODY if you'd rather accept each one yourself.
  - A transfer is capped at one design's size (about 26 MB).
- **Lobby images:** custom decals and paint textures (PNG / JPG) come with the other players' designs and are saved
  in your `My Games/Sprocket/Decals` and `Paint` folders. Only images the design actually uses are saved, never
  over an image of yours with the same name, and at most 64 MB per player each time you run the game.
- **Limits:**
  - LAN/Radmin play over TCP is not encrypted. The password travels as a hash, so someone who can read your
    network traffic could reuse it. Steam play is encrypted by Steam.
  - The host relays everything, so players have to trust the host. A dishonest host could, for example, make a
    stranger look like your Steam friend to your game, so their designs would be saved without asking.
  - **Your IP address:** when you join by IP or on LAN, the host and every other player in the game can see your
    IP address (it's how F8 sends designs straight to a player). Players who join through Steam don't share it.
  - Like any peer-to-peer mod, a player with a modified client can cheat (for example by sending stronger
    shells). Play with people you trust, or use a password.
- **Ports:** hosting listens on TCP 7777, design sharing on TCP 7780, LAN search on UDP 7778. Don't
  port-forward them unless you want people from the internet to reach you. Steam play needs no open ports.
  - **The design port (TCP 7780) is open whenever the game runs,** even when you're not playing multiplayer,
    on every network you're on.
  - Anyone who can reach it can send you a design offer. It's still only saved after your OK, unless they're a
    Steam friend (see F8).
  - On networks you don't trust (public Wi-Fi, school or university networks), turn it off with
    `[Designs] AcceptDesigns = false`. Sending designs through the game connection keeps working.

## How it works
- Enemy (AI-team) tanks become puppets driven through the game's own crew tasks, corrected to the
  owner's position; turrets / guns copied per aimable.
- Shots: launch state + projectile type + shell type and the shooter's shell parameters; the puppet's
  own cannon fires it.
- Damage is victim-authoritative: owners replicate health, fire, repairs and kills, and send the pen cam
  record (path, fragments, struck plates, damage) back to the shooter.
- Destroyed = all crew knocked out or an ammo rack destroyed → kill feed + banner. Native end screens
  are suppressed while connected.
- Runs from Harmony postfixes on `SprocketApplication.Update/FixedUpdate`; UI is uGUI/TMP.

## Config (`BepInEx/config/SprocketLAN.cfg`)
Easiest through F7. Sections: `[Armour]`, `[Shells]`, `[Stabiliser]`, `[PenCam]`, `[Repair]`, `[Designs]`,
`[Battle]`, `[Lobby]`, `[Combat]`, `[Puppets]`, `[Network]`, `[Player]`, `[Settings]`, `[Features]`, `[Debug]`.
Edit the file only while the game is closed (the game rewrites it when it exits).
Fallbacks: `UseGameArmourModel` / `OwnFragmentModel` (Armour) switch back to the older 3D-model
measurement / the game's fragment simulation. `[Debug] Verbose` adds detailed hit logs.

## Troubleshooting
- Logs: `BepInEx/LogOutput.log` and `BepInEx/SprocketLAN_<pid>.log` (send both from the shooter and the
  tank that was hit when reporting a hit problem; turn on `[Debug] Verbose` first).
- "Protocol mismatch" when joining: someone has another version.
- A host doesn't show up in FIND GAMES: allow Sprocket through the Windows firewall (private networks) on the
  host, make sure you're on the same network, or join by IP.
- A clash with another mod: turn off the matching part in F7 › Features and restart.
- Two game copies on one PC: the second listens for designs on port 7781 (send to `ip:7781`).

## Build
`SPROCKET=/path/to/Sprocket ./build.sh` compiles with Roslyn against the game's own `dotnet/` runtime +
BepInEx core + interop DLLs (no NuGet).

## For other mods (API)
See **API.md**. `SprocketLAN.Api.Lan` provides:
- channels to send your own messages between players;
- `LocalShot` / `RemoteShot`, so data attached to a shot arrives with the round fired from the shooter's puppet;
- `RegisterShell` for shell types that replace the round completely;
- player and tank lookups.

[![made with universal-modder](https://raw.githubusercontent.com/rehan-remade/universal-modder/main/docs/media/made-with-dark.svg)](https://github.com/rehan-remade/universal-modder)

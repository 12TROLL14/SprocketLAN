# SprocketLAN — multiplayer for Sprocket (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.780+3be4532, .NET 6)

Version **6.6.0** (protocol 17). Every player needs the same version: a different one is refused when joining.

## Install
1. Install **BepInEx 6  (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.780+3be4532, .NET 6)** in the Sprocket folder and start the game once.                                                         https://builds.bepinex.dev/projects/bepinex_be<img width="1355" height="885" alt="build" src="https://github.com/user-attachments/assets/6f280e02-fdd4-4872-91a5-7e1aa3e89b6b" />

2. Copy `BepInEx.cfg` from the release to `BepInEx\config\` (the one default config WILL result in a crash ! use the config from release.)
3. Copy the `SprocketLAN.dll` from this release into `BepInEx/plugins/`, so you get
   `BepInEx/plugins/SprocketLAN/SprocketLAN.dll` and `BepInEx\config\BepInEx.cfg`.
4. Start the game: the **SPROCKET LAN** panel appears (F10 hides / shows it).

## Play
1. One player: F10 → **HOST GAME** (TCP 7777 by default; forward it for internet play, LAN works as is).
   Others: type `host-ip:port` → **JOIN**.
2. Everyone opens the **MULTIPLAYER LOBBY** (F9): pick your tank and team, click **READY**.
   The host picks the map and team spawns and clicks **START BATTLE**: every player's battle starts by itself.
3. Use the same armour / shell settings as everyone else: F8 → **SEND MY ARMOUR / SHELL SETTINGS**.

Manual way (no lobby): everyone opens **Custom Battle** with their own tank on their team and every other
player's tank (the exact same design file) on the AI team.

### Finding games
Press **FIND GAMES ON MY NETWORK** in the F10 menu to list the hosts on your local network, then click one to
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
| F6 | Field repair (hold position; cancelled if you move) |
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

### Sandbox shell testing
In the vehicle designer's armour inspector (**Sample** mode) a panel offers the mod's shells: type (AP, APC, APHE,
APHEC, HE, APDS, APFSDS, HEAT), calibre, AP penetration and filler. Clicking the tank fires that shell with the
mod's rules: the pen cam shows the result, and its path / spall / bursts are drawn on the tank.

### Pen cam
Replay of each of your hits (and a kill cam when you die), drawn from the game's armour model coloured
by thickness: the shell's real path (bounces included), every struck plate highlighted, spall cones,
APHE / HE bursts, damaged modules, ammo rack explosions. Fragments that hit nothing are not drawn.

### Multiplayer lobby (F9)
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
- Designs (blueprint + picture) to any player or IP; a replaced design is backed up.
- Gameplay settings (Armour, Shells, Combat, Repair, Stabiliser; not keys, names, folders, network,
  pen cam): the receiver sees the differences and chooses APPLY or DISCARD (old config backed up).
- Hits are worked out on the tank that is hit with that player's settings: everyone should use the same.

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
`[Battle]`, `[Lobby]`, `[Combat]`, `[Puppets]`, `[Network]`, `[Player]`, `[Settings]`, `[Debug]`.
Fallbacks: `UseGameArmourModel` / `OwnFragmentModel` (Armour) switch back to the older 3D-model
measurement / the game's fragment simulation. `[Debug] Verbose` adds detailed hit logs.

## Troubleshooting
- Logs: `BepInEx/LogOutput.log` and `BepInEx/SprocketLAN_<pid>.log` (send both from the shooter and the
  tank that was hit when reporting a hit problem; turn on `[Debug] Verbose` first).
- "Protocol mismatch" when joining: someone has another version.
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



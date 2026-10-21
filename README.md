# SprocketLAN — Sprocket multiplayer via Custom Battles (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.780+3be4532, .NET 6)
## Help --> send the README.md to Chatgpt it'll gladly help you
## Play
1. Install: `BepInEx/plugins/SprocketLAN/SprocketLAN.dll` (+ `Sounds/kill.wav`) (every player, same version).
   there is kill sound support, but you are expected to provide your own kill.wav
2. Copy `BepInEx.cfg` from the release to `BepInEx\config\`
3. One player: F10 menu -> HOST GAME (TCP port 7777 by default, forward it for internet play).
   Others: type `host-ip:port` -> JOIN.
4. Everyone opens **Custom Battle**: your own tank on your team, every other player's tank
   (the exact same design file) on the AI team. Start in any order.
5. Keys: F10 menu, F11 diagnostics dump, F6 field repair (configurable).

## How it works
- Enemy (AI-team) tanks become puppets: their CommanderAI is deregistered and they are driven
  through the same crew tasks the game uses (Driver.Solution), with soft physics correction to the
  owner's position. Turret ring / gun trunnion rotations are copied per aimable (multi-turret ready).
- Shots: exact launch state + projectile-type GUID are replicated; the puppet's own cannon fires it.
- Damage is victim-authoritative: puppets ignore local damage; owners replicate health pools
  (keyed by component hierarchy path), fire state, repairs and kills.
- Destroyed = all crew knocked out or an ammo rack destroyed -> kill feed + centre banner.
  Native victory/defeat screens are suppressed while connected.
- Main loop runs from Harmony postfixes on `SprocketApplication.Update/FixedUpdate`
  (no ClassInjector: it crashes this Unity 6 build). UI is uGUI/TMP, no IMGUI.

## Config (`BepInEx/config/SprocketLAN.cfg`)
- `[Battle] SpawnClientsNearHost` (host only) — spawn joiners in a row in front of the host.
- `[Repair] Key / DurationSeconds / CancelIfMovedMetres`
- `[PenCam]` pen camera and sounds, `[Shells]` APHE/HE/APDS tuning (keys 1-4), per-design shell settings in `BepInEx/config/SprocketLAN_shells.txt`.
- `[Puppets]` correction tuning, `[Debug] ForceWindowed/Verbose`.

## Adding shell types
Implement `Shells.IShellHandler` (unique `Kind` >= 1) and `ShellRegistry.Register(...)` from a plugin's
`Load()`. Shots carry `ShellKind` + an opaque payload; kind 0 = native projectile.

## Build
`SPROCKET=/path/to/Sprocket ./build.sh` compiles with Roslyn against the game's own `dotnet/` runtime + BepInEx core + interop DLLs
(no NuGet). Logs: `BepInEx/SprocketLAN_<pid>.log`.

## Known
- Track link visuals stay scattered after a repair (functional repair works).

[![made with universal-modder](https://raw.githubusercontent.com/rehan-remade/universal-modder/main/docs/media/made-with-dark.svg)](https://github.com/rehan-remade/universal-modder)

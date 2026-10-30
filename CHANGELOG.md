# Changelog

## 6.6.0 (same protocol)
- F10 › FIND GAMES ON MY NETWORK: lists the SprocketLAN hosts on your local network (name, address, players,
  in battle or not, version); click one to join. Hosts answer on UDP port 7778 (Network › DiscoveryPort;
  Network › AnswerLanSearches to stay hidden). Joining by IP still works as before.

## 6.5.2 (same protocol)
- F7 › Features › SandboxArmourInspector: turns off the mod's changes to the sandbox armour inspector (the game's
  own inspector is left untouched). Restart to apply.

## 6.5.1 (same protocol as 6.5.0)
- Kill sound when you destroy an AI tank (crew knocked out or ammo rack gone within 6 s of your hit);
  setting PenCam › KillSoundOnBots to turn it off.
- Faster: the armour-to-AI-tank lookup on every hit is cached; the API's player check runs twice a second.

## 6.5.0 (protocol 18 - everyone must update)
- F7 › Features: switch off the mod's shells and armour model, the pen cam, or the modelling tools (they are then
  not loaded at all, in case they clash with other mods). Takes effect after restarting the game.
- AI tanks: our shells, armour model and pen cam now also work when you shoot bots (they used the game's
  ammo and had no pen cam). Bots firing at you use the mod's armour model too (setting ModShellsOnBots).
- Public API for other mods (`SprocketLAN.Api.Lan`, see API.md): messages between players, data attached to
  shots so custom shells behave the same on puppets, player / tank lookups, shell handler registration.

## 6.4.0
- Modelling tools panel while editing a compartment ([F4] shows / hides it): inset face, polygon hole,
  round hole / round inset (number of edges, radius), knife (connect 2 vertices). Each runs as a normal
  editor operation (undo / redo work), mirror mode is respected; faces are kept at 3-4 corners.

## 6.3.6
- Impact marks (holes, dents, ricochet scars) now also show on your own tank when you are hit (the mod works the
  hit out, so the game didn't draw them); a puppet's marks are cleared when its owner respawns.

## 6.3.5
- APHE: the fuse delay now runs from the plate that armed the fuse, not from the first plate hit (battles,
  pen cam and sandbox).

## 6.3.4
- Sandbox shells: APDS / APFSDS were fired without their extra muzzle velocity, which the hit code then took
  back out - they penetrated far less than AP. Fixed (only the sandbox was affected).

## 6.3.3
- Sandbox shells: APHE fuse sensitivity (mm) and delay (m) fields.

## 6.3.2
- Sandbox shells: no pen cam; the shot is shown on the tank only.

## 6.3.1
- Sandbox shells: every click on the tank fires (turret included), using the inspector's own damage model;
  the shell always starts 3 m outside the armour; no more T-posed copies of the crew.

## 6.3.0
- Sandbox armour inspector (Sample mode): a panel next to it fires the mod's shells instead of the game's
  simulation - AP, APC, APHE, APHEC, HE, APDS, APFSDS, HEAT, with calibre, AP penetration and filler. Click the
  tank: the pen cam shows the hit and the shell's path, spall and bursts are drawn on the tank in place of
  the game's line. MOD SHELLS: OFF gives the game's own simulation back.

## 6.2.7
- Pen cam timing: plates along a bent shell path are timed by distance (many path pieces piled up time and
  pushed the APHE shell / burst to the end); the shell's path inside plays in at most 1.2 s; the pen cam
  clock ignores game slow-downs. Each pen cam logs its timeline.

## 6.2.6
- New setting `[Shells] HEFragmentCurve` ('filler g:fragments') for the number of HE burst fragments
  (default = the previous built-in values), drawn in the F7 panel.

## 6.2.5
- New setting `[Shells] APHEFragmentCurve` ('filler g:fragments' points) for the number of APHE burst
  fragments (default = the previous built-in values); the F7 panel draws it, and the HE penetration curve.
- Pen cam: when an ammo rack cooks off, its own fragments were taken for the shell, pushing the shell / APHE
  burst animation to the end; they are now left out. The ammo rack explosion plays after its damage.

## 6.2.4
- Search box on the lobby's tank list and the F8 design list (name or faction, every word typed).
- Pen cam: damaged modules light up when the shell / APHE burst gets there, not as the x-ray view opens;
  crew killed by the hit stay visible until their damage has played; long APHE paths keep the pen cam open
  until the burst is over.

## 6.2.3
- Fixed: fragments counted a thick plate as a few mm when its face got paired with a touching plate's face
  (mostly at steep angles): a plate with a known thickness now always counts at least that thickness.
- Pen cam: the struck plate is highlighted exactly (its own solid in the armour model), not spilling onto
  neighbouring plates of the same thickness.

## 6.2.2
- Fixed: HE fragments (and shells) went through plates whose back face wasn't matched in the armour model
  (often next to road wheels, sprockets and applique): such a face was taken for the far side of a plate
  already passed. Now the model checks whether there is steel just past the face and counts it.

## 6.2.0
- **Multiplayer lobby** (F9 or the LOBBY button): each player picks only their own tank and team and clicks
  READY; the host picks the map, the number of teams (2-4) and each team's spawn, then STARTs. Every
  player's battle is set up (own tank on the player team, everyone else's on the AI team) and started
  automatically.
- Lobby designs travel over the game connection (no second port) into their own folder
  (`Documents/My Games/Sprocket/SprocketLAN Multiplayer`), with their shell settings.
- Team spawns: the map's own spawns A / B, or placed by the host on a map overview (drawn from the
  terrain the first time you play a map). Teammates spawn side by side.
- Fixed: HE on applique sitting on a hull plate burst inside the tank; the battle HUD didn't show the
  autocannon penetration penalty. Shell settings now travel with designs sent from F8.
- Protocol 17: every player needs 6.2.0.

## 6.0.0
- Hits, pen cam, fragments and burst points all use the game's own armour model (the designer's armour
  view): exact raw thicknesses, plates whose thickness sits on either face, applique / add-on armour,
  much faster hits and pen cam.
- Own fragment tracer (spall cones, APHE / HE bursts, the shell inside the tank) with the game's
  damage formula; running gear and external parts count by material.
- HE on the armour model: bursts on first contact (armour, wheel, track), out of any steel.
- Shot traps: shells bouncing off the outside fly on and can hit other plates.
- Pen cam: every struck plate highlighted, thickness colours, barrels, real module shapes, fragments
  that hit nothing hidden, faster path animation.
- Per-gun shell settings (by ammo rack calibre), fillers capped to the gun, autocannon option
  (rate of fire, penetration divisor).
- Shell switching reloads only the active fire group.
- In-game settings panel (F7) with live diagrams; sending settings to other players (F8);
  F8 lists every player for every client.
- Stabiliser with separate traverse / elevation multipliers; repaired engines restart.
- Protocol 15: every player needs 6.0.0.

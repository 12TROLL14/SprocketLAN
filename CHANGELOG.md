# Changelog

## 6.18.3 (same protocol)
- **Pen cam: crew drawn dead and modules drawn destroyed on an undamaged tank** (seen on the sandbox test-drive dummy).
  A health pool with no maximum (0 / 0) passed the "at 1% or less of its maximum" test, so it counted as dead. Such
  pools now count as "no health information", not dead, in the recording and in the view.
- Diagnostics, once per tank:
  - "PenCam crew of '<tank>' before this hit: [...]" gives each seat's health, operator, behaviour and pool, and
    which rule called it dead;
  - "PenCam: '<tank>' module N (<type>) was already destroyed" names any module drawn red from earlier damage.
  These say why, if crew still show as dead.

## 6.18.2 (same protocol)
- **FIX IT only installs Valve's own steam_api64.dll** (security). Before, any DLL with the right function names
  passed, so a fake or modified one in a game's folder (a cracked game's Steam emulator, or worse) would have been
  installed and then run inside Sprocket every time.
  - Now its digital signature must check out with Windows' (or Wine's) own WinVerifyTrust: the file is unchanged
    since it was signed, and it chains to a trusted certificate authority. The check runs offline.
  - The signer must be Valve Corp.
  - The file is checked as it is in the plugin folder, under a temporary name. Only then is that exact file renamed
    into place, so nothing can be swapped in between.
  - A DLL that matches but can't be verified is never installed automatically: the line says which game it's in
    (the full path is in the log).
  - Game names in the status line are shown as plain text.
- Still only started by your own click (FIX IT, then YES). Nothing from the network can start it.

## 6.18.1 (same protocol)
- **FIX IT found too little:**
  - Unreal Engine games keep `steam_api64.dll` 7 folders deep
    (`Engine/Binaries/ThirdParty/Steamworks/Steamv161/Win64`), and the search stopped at 4. It now goes 8 deep
    (skipping asset folders).
  - DLLs from Steamworks 1.61 and older were refused for lacking `SteamFriends_v018`. The mod now falls back to
    their `SteamFriends_v017`: the friends functions it uses (names, rich presence, invites, friend list) are the
    same in both. DLLs with v018 are still preferred.

## 6.18.0 (same protocol) - open beta
- **F10 › STEAM › FIX IT** when Steam is off because `steam_api64.dll` is missing or unusable.
  - It asks first, then searches all your Steam libraries (read from Steam's `libraryfolders.vdf`; Linux paths
    through Wine's Z: drive under Proton).
  - Each `steam_api64.dll` found is checked from its export table, without loading it: it must be 64-bit and have
    all 53 Steam functions the mod uses.
  - The newest one that fits is copied next to SprocketLAN.dll, and Steam starts right away. An unusable DLL that
    was already loaded is renamed to `.old`, and the game needs a restart.
  - The log lists the libraries searched and the DLL used.
- README: open-beta notice and what to send with a bug report, Linux / Proton install, and what the mod exposes
  (IP sharing, the always-open design port, friends' designs saved without asking, lobby images).

## 6.17.1 (same protocol)
- **New main menu [F10]**, in the lobby's style: a title bar showing your state (offline / hosting / connected and
  ping), then sections:
  - You, Host a game, Join a game (address, FIND GAMES, results), Steam, Tools (lobby, send designs, settings,
    stabiliser, diagnostics) and Status;
  - once connected, Join a game becomes **This game**: whether it's public / private / has a password, the lobby's
    map, teams and tank rules, and every player with their ping and battle state;
  - when there are no Steam invite buttons, a line says why.
- **New design panel [F8]**, in three sections:
  - your designs: era, weight and cost on each row, and the selected one in the header;
  - send to: player buttons, with the picked player highlighted, or an IP, and SEND;
  - receiving: what's saved without asking, and cards for transfers waiting for your OK.

## 6.17.0 (same protocol)
- **Tank rules for lobbies.** The host sets them in the lobby [F9]: era range (WWI, Interwar, Early war, Mid war,
  Late war, Cold war, from the game's own era list), max weight (t) and max cost.
  - A player can't READY with a tank that breaks them. Changing the rules (or the tank) takes a player's READY back.
  - The host checks every tank itself, from the design file it received, and START waits until every tank fits.
  - A design's era comes from its creation date; its weight and cost from its header (what the designer saved).
  - The host's rules are kept for next time (config: Lobby › TankRules).
- **The rules and the map show in the game browser** (F10 › FIND GAMES), for Steam and LAN games. A host on an older
  version shows "tank rules: not shown by this version".
- **New lobby panel [F9]**, in three columns:
  - players: one row each, with team colour, tank and state (READY / not ready / receiving / breaks rules /
    in battle), your team, a card for your tank (era, weight, cost, meets the rules or why not), READY and the rules;
  - your tanks: era, weight and cost on every row; tanks that break the rules marked with the reason; a filter for
    tanks that fit only;
  - battle: map, teams, spawns, the map overview and START, which says what it's waiting for.

## 6.16.1 (same protocol)
Security fixes, from a review of everything the mod receives from other players:
- **Design port (TCP 7780): anyone on your network could crash the game.** The port is open whenever the game runs.
  A stranger could make it hold over a gigabyte in memory with one fake transfer, without your OK. A transfer is now
  capped at one design's size (~26 MB), and names, settings and shell lines at 4 KB each.
- **Lobby images (6.16.0) are only saved if the received design uses them**, and at most 64 MB per player per
  game run: a player can't fill your Decals / Paint folders with other files.
- **Shell settings received with a design can't change another design's settings any more** (a line break hidden in
  a setting's name could add a line for another design to the settings file).
- Settings offers: the values shown in the menu can't carry formatting (text that changes the menu's look).

## 6.16.0 (same protocol)
- **Other players' custom decals and paint textures show on their tanks.** A design only pointed to its images
  (`Sprocket/Decals/...`, `Sprocket/Paint/...` in the player's My Games folder) and the lobby sent the blueprint
  alone, so other players didn't have the files.
  - The images now travel with the design: PNG/JPG only, at most 8 MB each and 12 MB per design.
  - They're saved into the same place in your own My Games/Sprocket folder.
  - An image you already have with the same name is never replaced (the log says so if it differs).
  - Built-in decals and web images (https://...) already worked.
- Designs you received before this version get their images too: if a design uses images you don't have, it's
  asked for once more.
- Both players need 6.16.0 for images to be sent. Older versions still play together: they just don't send or
  receive images.
- The log says what was sent ("sends N decal/paint images", and which ones weren't on the sender's PC) and what
  arrived ("images from ...: N saved").

## 6.15.3 (same protocol)
- **Launch error removed:** the red HarmonyX error "Failed to patch ... PaintBlueprintAssemblyStage::set_paintjobOverride"
  and the "Paint override hooks: IL Compile Error" warning. That setter is a field accessor in the interop assembly
  and can't be hooked; the hook is gone.
- The yellow "Failed to init IL2CPP patch backend ... ::.ctor" warnings (EngineProperties, PaintBlueprintAssemblyStage)
  are still there and don't break anything: constructors are hooked the normal (managed) way. For EngineProperties the
  other hooks do the work. For the paint stage it probably means the hook never sees the game's own calls, so
  TanksKeepOwnPaint likely does nothing; the line "Paint: a vehicle was given a team paint scheme" in the log would
  prove otherwise.

## 6.15.2 (same protocol)
- **Extra loaders must be able to reach the breech**, by the game's own rule (the crew member's arms against the
  gun's loading point, as for its own loaders; none with the game's "unlimited operate distance" setting). In the
  designer the < > buttons skip guns out of that crew member's reach and the panel says so; in battle a seat out of
  reach doesn't count (the log says which).
- **Other players' tanks keep their paint and decals.** Multiplayer battles start from a custom battle setup, where
  each team can get a paint scheme that replaces the vehicles' own; it's dropped in multiplayer battles
  (F7 › Battle › TanksKeepOwnPaint). The log says when a scheme was dropped.
- **Designs reach the other players faster in the lobby:**
  - Steam's networking sent at most ~256 KB/s with a 512 KB buffer: now 1-16 MB/s (Steam still measures the
    connection) with a 16 MB buffer.
  - a design that arrived before the lobby update saying it was picked was dropped and asked for again 8 s later;
    it's now kept (20 s at most) and saved as soon as the pick shows. Missing designs are asked for every 4 s, and
    re-sent at most every 2 s (was 5).
- **Field repair of tracks: the tank pulling to one side.** Only broken tracks are touched now (the intact one's belt
  was reset too), and the repaired track's brake force is put back to what it was at spawn if the break changed
  it. Both tracks are logged before the repair and while driving off ("After repair, track ..."), in case it still
  pulls.

## 6.15.1 (same protocol)
- **Sandbox armour inspector: test shells hit every time.** The shell's path is traced through the mod's own model
  of the tank, rebuilt for each test shot (the design may have changed). Since 6.14.5 that build was limited to
  120 ms a frame (to stop battle freezes), so on a big tank it wasn't finished and the shot found "no armour".
  HE wasn't affected (its burst doesn't need the model first). Outside battles the model is built in full again.
- **Only the selected fire group's turrets follow your aim** (F7 › Battle › TurretsFollowFireGroup, on by default):
  the turrets and gun mounts that carry a gun of that group turn (and a turret carrying such a turret); the others
  hold the direction they were last given. **AllTurretsKey** (` by default, the key left of 1, ² on AZERTY) makes all
  of them follow again, until you pick another fire group. The shell selector shows which ("Turrets: fire group").
  Your own tank only.

## 6.15.0 (protocol 20: everyone needs this version)
Second network security review.
- **The password never travels any more.** It used to be sent as a fixed hash: anyone sniffing the LAN could replay
  it to join, and any host you joined learned it. Now a host with a password answers with a one-time random
  challenge and the client proves it knows the password (HMAC of the challenge); a sniffed proof is useless for
  the next join. Wrong-password lockouts as before.
- **Flood limits on the host, per player and kind of message** (states, shots, pen cams, chat, designs, mod data...):
  extra messages are neither relayed to the other players nor used; a player who keeps flooding is disconnected.
  Real play is far under the limits.
- **Pen cam records from the network are checked:** counts capped, every number made finite and within sane
  ranges, fragment parents checked (a forged record could make the pen cam allocate or draw without bound).
- **Shots that start far from the shooter's tank (80 m+) are ignored** (a forged shot fired from inside your tank).
- **Lobby:** only the design a player picked is saved to disk (a player could make everyone write files of any
  name); a START for a map you don't have is refused (the name goes into the battle setup file).
- Vehicle names in battle state messages are cleaned like player names (no rich text in the scoreboard).

## 6.14.13 (same protocol)
- **Modelling: the move / rotate / scale handles are back with the mod's own selection display.** The mod skipped
  the structure editor's whole redraw to draw the points / edges / faces itself, and the handles are drawn in that
  redraw. Now the redraw runs; only the points and edges it would draw one by one are dropped (and the mesh
  editor's own drawing skipped), so the handles show again without the lag coming back.

## 6.14.12 (same protocol)
- **Engines over 24 cylinders are right as soon as the vehicle loads** (not only once the engine is selected): the
  vehicle's engine applies the game's tech limits to its blueprint while the vehicle is built; the values from
  before that are kept now, so the engine model, its power and test driving are right straight away.

## 6.14.11 (same protocol)
- **Engines over 24 cylinders: the engine's size follows when loaded.** 6.14.10 put the count back in the blueprint
  but not the way an edit does, so the engine model kept its 24-cylinder size. The value now goes through the
  editor's own property (like typing it), the blueprint is marked as changed and the dependent values are worked
  out again, so the model is rebuilt.

## 6.14.10 (same protocol)
- **Engines over 24 cylinders / 5 l (second fix):** the 6.14.9 log showed the engine is saved and loaded with its 40
  cylinders; it's the engine editor that clamps it to 24 when it opens it, before the mod has raised its limits.
  The values the file had are put back right after (only when the editor clamped them to exactly the game's limit,
  so lowering them yourself still works). The log says what the editor got.

## 6.14.9 (same protocol)
- **Engines over 24 cylinders / 5 l per cylinder are kept when loaded.** The game clamps them to its own limits when
  it loads an engine (a 40-cylinder engine came back with 24). The values written in the file are now read again
  after the game loads it and kept (up to F7 › Engine › MaxCylinders / MaxDisplacementLitres). The log says what
  was saved and loaded.
- **Camera zoom out:** the game's battle camera stops at ~15 m, tight for a huge tank (the very first view is wider
  because the game starts further out). F7 › Battle › CameraMaxZoomOut (40 m by default, 0 = the game's), applied
  every frame since the game puts its limit back.

## 6.14.8 (same protocol)
- **Extra loaders: the countdown is right now.** The 6.14.7 reload log showed that the game's countdown counts the
  pick-up and put-in steps at one second per second whatever the loaders' handling speed, and only the carrying by
  their speed. The mod raised the handling speed, so those two steps really took 2.8 s but the countdown showed
  13.8 s each (the "last 8 seconds take 3"). Now the carrying speed is raised and the pick-up / put-in times are
  shortened instead: same total (90 s -> ~19 s with 6 loaders), and the countdown matches.
  - The reload log is now per gun, on every change of step (and every 3 s), to catch a reload that doesn't start.
- **Camera zoom range:** telling the camera to follow a turret makes the game work its zoom range out from the
  turret (much smaller than the tank). The game's range for the whole tank is kept now.

## 6.14.7 (same protocol)
- **Fewer stutters in battle when not shooting:**
  - the pen cam's background running-gear retries rebuilt a whole tank's geometry; on a huge tank each step was a
    ~100 ms frame (the P.1000 and your own copy of it, three tries each). Huge tanks (1 000+ modules) aren't retried.
  - extra loaders searched the whole tank for its guns twice a second; the guns are now found once per tank.
  - the camera follow no longer searches the scene for the camera every 3 s once it has it.
  - battle frames over 60 ms are logged ("Hitch: ..."), with whether a garbage collection ran (F7 › Debug › LogHitches).
- **Reload countdown with extra loaders:** reloads now log what the game's load task is doing once a second
  ("Reload x5 ...", F7 › Loaders › ReloadLog) to find why the countdown runs slow in its last seconds.

## 6.14.6 (same protocol)
- **Garbage collection paused during hits and pen cams.** The game's garbage collector stops everything for ~100 ms
  every few seconds (longer with huge tanks loaded); the remaining pen cam hitches were those, on every tank. It is
  now paused while a hit is worked out and while its pen cam plays (15 s at most) and runs once afterwards.
  F7 › PenCam › PauseGarbageCollection.
- **Hit on a P.1000: faster again** (~400 ms in 6.14.5): a second level of boxes (1 024 armour triangles each), so a
  fragment path only looks at the parts of the tank it actually crosses.
- **Camera zoom range:** zoomed out, the camera keeps the game's height above its target (2 m on the P.1000) on the
  selected turret; 6.14.5 put the zoomed-out point right on the turret, so the camera's clipping against the tank
  kept it from zooming out as far.

## 6.14.5 (same protocol)
- **Hitting a P.1000: the hit itself took 1.5 s.** Every fragment path (hundreds for an APHE burst) was tested
  against every armour triangle of the tank (46 000 on a P.1000), and the turret parts were re-posed through the
  game for each of them. Now the triangles are grouped in boxes of 32 that a path skips when it doesn't cross them,
  the turret is posed once per frame, and module bounds are kept instead of being worked out again for each path.
- **No more multi-second freeze when a tank's pen cam geometry isn't built yet** (a huge tank just spawned, or
  rebuilt): a hit or the pen cam used to finish building it on the spot (3 s+ on a P.1000, "FREEZE ... geometry:
  exterior meshes" in the log). Now at most 120 ms a frame is spent; the hit uses the game's armour meanwhile and the
  pen cam opens once the geometry is ready (a slice each frame). The log says why a geometry is (re)built.
- **Camera follows the selected fire group:** the camera orbits its *far* target when zoomed out and its *close*
  target when zoomed in; only the close one was moved before, so zoomed out it stayed on the game's choice. Both are
  the selected gun's turret now. The log lists the tank's turrets and where the camera's target is.

## 6.14.4 (same protocol)
- **Pen cam freeze on the P.1000 (the real cause):** the per-frame loop over the tank's 5 300 modules worked their
  colours out with the game's own colour functions (Color.Lerp, colour comparisons, Mathf.Sin, the game's damage
  colour). Each of those is a call into the game that hands back a freshly allocated value, so the pen cam made
  tens of thousands of small allocations on the game's heap every frame, and the game's garbage collector stopped
  everything for ~110 ms every couple of seconds. The loop now uses plain arithmetic, and the damage colours are
  asked of the game once per shade and kept.
- The pen cam's GPU buffers for big tanks are made in the background before any hit (they were made at the first
  pen cam frame: a 230 ms frame).
- **Camera follows the selected fire group (third fix):** the turret is now found from what the turrets actually
  rotate (the game's turret drives), from the selected gun itself; 6.14.2 picked a turret ring part that could be
  the same for every gun. The log says, for each gun you select, which turret the camera follows.

## 6.14.3 (same protocol)
- **Pen cam see-through colours on the GPU.** How opaque each point of an x-ray part looks depends on how its
  surface faces the camera. That was worked out on the CPU for every vertex (300 000 on a P.1000) whenever the
  camera moved, i.e. every frame. Now each vertex points, by its normal's direction, into a small texture (one
  64x64 tile per kind of look: hull, module, destroyed module, crew, outline...), and only that texture
  (about 30 000 texels) is redone when the camera moves; the GPU does the rest. The vertices are only touched when a
  part's colour changes (damage). Uses the game's built-in UI shader; if it isn't there, or with
  F7 › PenCam › GpuSeeThrough off, the CPU way is used as before.
  - Small difference: the rim is worked out for the camera's viewing direction rather than per point (it's the
    same in the middle of the view, a little different at the edges).

## 6.14.2 (same protocol)
- **Camera follows the selected fire group (second fix):** the camera is now taken from the battle's player state
  (6.14.1 didn't find it), and the target is the turret ring the selected gun sits on. If a gun has no turret ring
  above it, the log says what it found instead ("Camera: no turret ring above the selected gun").
- **Shell selector key moved from F3 to Home:** F1-F5 are the game's fire group keys, so switching to fire group 3
  also hid the shell selector. An old F3 setting is moved to Home automatically (F7 › Settings › ShellHudKey).
- **Pen cam on big tanks (P.1000): less work per frame.**
  - The see-through colours depend on where the camera is, and it orbits during the whole pen cam, so all ~300 000
    vertices were redone every frame (~95 ms a frame on the P.1000). Now a quarter of them is redone each frame
    (the camera moves less than a degree in that time); colour changes (damage) still show at once.
  - The colours go to the GPU through a kept buffer copied in one block, instead of a new 1.2 MB array copied
    element by element every frame.
  - "PenCam frame took" in the log now says which part of the frame took the time.

## 6.14.1 (same protocol)
- **Camera follows the selected fire group (fixed):** 6.14.0 read the camera's target through a property that
  fails through the game's interop, so nothing happened. The vehicle's own tracking target and the camera's are
  now both set to the turret of the selected fire group's gun.
- **Pen cam on big tanks: no more hiccups or multi-second freeze on a hit.**
  - When a tank's running gear couldn't be found (the P.1000), its whole pen cam geometry was thrown away and
    rebuilt every few seconds (up to 7 times), about a second of work each time. A hit during one of those
    rebuilds had to finish it on the spot (the long freeze).
  - Now the retries (3 at most, further apart) are built in the background while the existing geometry stays in
    use, and are only swapped in if they did find the running gear.
  - The merged module meshes are built in the background before any hit, not when the pen cam opens, and are
    kept with the tank's geometry.
  - Slow pen cam steps are logged ("PenCam prewarm step took", "PenCam opening took", "PenCam frame took").

## 6.14.0 (same protocol)
- **Pen cam on big tanks no longer lags** (a P.1000 being hit: about 5 300 module meshes).
  - Modules were drawn one at a time (two draws each, ~10 000 a frame), and every vertex's colour was worked out
    again on every frame.
  - With 200+ modules, they're now merged into two meshes (bodies and outlines), built once per tank, so each is
    one draw.
  - Colours are worked out again only for the modules whose colour changed, or when the camera moved.
  - The hull x-ray and the other meshes skip their colour pass when nothing changed (the camera stands still while
    the result is shown).
- **Kill feed under the pen cam:** on the right, just below the pen cam panel (shown or not), newest at the top.
  F7 › KillFeed › BelowPenCam (on by default). Off: PositionX / PositionY as before.
- **Camera follows the selected fire group** on multi-turret tanks. The third-person camera's close target (the
  one it orbits when zoomed in) becomes the turret of the selected fire group's gun. A hull gun gives the game's
  choice back. F7 › Battle › CameraFollowsFireGroup.
- **Shell loaded at spawn:** a new "Loaded at spawn" button in the shell panel (designer, Ammo Rack) sets which
  shell each gun has loaded when the tank spawns, instead of always AP (AP, APHE, HE, APDS, APFSDS, HEAT). It's
  kept with the gun's shell settings (a 10th field in SprocketLAN_shells.txt; older files read as AP).

## 6.13.6 (same protocol)
- **Extra loaders reworked: speed instead of time.** 6.13.x sped up the reload's time step, which left the game's
  countdown stuck at 0,0. It also ran even right after spawning, when no loader contributes yet (all zero), which
  could leave the gun's load state, and the selector, wrong until the fire group was changed.
  - Now the loaders' contribution is scaled: handling and carrying speed x the multiplier, preparing time / the
    multiplier.
  - The time step is untouched, so the countdown stays right.
  - Nothing is changed while no loader contributes.
  - The log shows the game's values the first time ("Extra loaders: load task update (...)").
- **Driveshaft limit doesn't flicker any more:** the game marks shafts again whenever it rebuilds the powertrain.
  The correction now runs every frame in the designer, on the powertrains found every 2 s, instead of twice a
  second.

## 6.13.5 (same protocol)
- **Driveshafts up to 800 mm really accepted now.** The log of 6.13.4 showed the raised maximum was in place
  ("Driveshaft limit: 800 mm"), but the game decides "too thick" against its 400 mm constant built into its code,
  not through the query the mod changed. Now, right after the game works the shaft diameters out (and twice a
  second in the designer), every connection and shaft under MaxDriveshaftMm loses its "too thick" mark. A shaft
  over the new limit keeps the game's label, which still reads "/400mm" because that number is the game's own
  text.
- (The "Failed to init IL2CPP patch backend ... EngineProperties::.ctor" warning is unrelated and harmless.)

## 6.13.4 (same protocol)
- **Driveshaft limit raised, not removed:** with F7 › Engine › UnlockDriveshaftThickness, the limit is
  MaxDriveshaftMm (800 mm by default, from 100 to 5000) instead of the game's 400 mm.
  - The maximum the game asks for is scaled up, so the label reads "x/800mm".
  - Shafts above the new limit are still marked "Too thick".
  - Connections and shafts under it are no longer marked.

## 6.13.3 (same protocol)
- **Fixed the crash on launch in 6.13.2:** the game's driveshaft limit (PowertrainRules.MaxDriveshaftDiameter) is a
  constant in read-only memory, and writing it crashed the game (AccessViolation). The mod no longer touches it.
  Instead the "Too thick" flag of the powertrain's connections (where the red "Too thick (576/400mm)" comes from)
  is taken off when the game sets it, and hidden when it's read or counted. The shafts' own flag is still cleared
  as in 6.13.1. Same setting: F7 › Engine › UnlockDriveshaftThickness.
- The "Failed to init IL2CPP patch backend ... EngineProperties::.ctor" warning at launch is harmless: that hook
  is installed the normal way.

## 6.13.2 (same protocol)
- **Driveshaft "Too thick" really lifted:** the limit is the game's own setting (PowertrainRules.MaxDriveshaftDiameter,
  400 mm). The mod now raises it 100 times while UnlockDriveshaftThickness is on, and puts it back when it's off.
  Clearing the shafts' flag alone (6.13.1) wasn't enough: the powertrain's connections check the limit too. The
  log shows the game's value. A powertrain flagged before may need one edit (move a part) to be checked again.
- **Extra loader panel shows the guns' names:** "1. 28cm SK C/34, 280 mm" (the cannon's blueprint name and
  calibre) instead of "Gun 1 (280 mm)".
- **F7 settings: the section list scrolls** when there are more sections than fit (17 shown). Use the UP / DOWN
  buttons under it, or the mouse wheel over it.

## 6.13.1 (same protocol)
- **The EXTRA LOADER panel stays open** while the crew seat is selected. Before, it closed after a second: the
  seat editor doesn't redraw its menu every frame. It now stays until the seat editor is closed.
- **Only crew members with no job can be extra loaders** (no role, nothing to operate). For a seat with a job,
  the panel says so and its extra-loader assignment is removed. In battle, a seat that has been given a job
  since doesn't count either.
- The panel only lists the guns that have extra loaders (instead of every gun).
- **Driveshaft thickness limit lifted:** the powertrain designer no longer marks driveshafts thicker than 400 mm
  as "Too thick", which big unlocked engines need. The shafts are valid again when their angles and obstructions
  are fine. Setting: F7 › Engine › UnlockDriveshaftThickness.

## 6.13.0 (same protocol)
- **Engine limits can be lifted** (F7 › Engine › UnlockLimits, on by default):
  - up to 64 cylinders (MaxCylinders, 255 at most: what the design file can store) instead of 24;
  - up to 25 l displacement per cylinder (MaxDisplacementLitres) instead of 5 l.
  - The mod raises the designer's maximum, including the one the tech tree sets, whenever the engine properties
    are built.
- **Extra loaders:** select a crew seat in the designer, and the new EXTRA LOADER panel makes that crew member an
  extra loader of a gun.
  - Reload speed: 1 loader x1, 2 x1.5, 3 x2, 4 x3, 5 x4, 6 x5, then +1 per loader (F7 › Loaders ›
    ReloadMultipliers). The gun's own loader counts as 1.
  - Saved in `BepInEx/config/SprocketLAN_loaders.txt` (design | gun | seats, by part id), not in the design file.
  - In battle, only extra loaders whose crew member is alive count, checked twice a second. A loader killed and not
    replaced slows the reload again.
  - It works by speeding up that gun's load task (its time step). The log says which argument is the time step
    the first time.
  - The shell HUD shows "Loaders: 3/3 loaders x2".

## 6.12.6 (same protocol)
- **Selection display, faster still** (from the 6.12.5 log: 55 ms a frame with the editor open vs 35 ms closed,
  plus 40-70 ms spikes):
  - The hover highlight no longer rebuilds everything. It was the cause of the spikes: each time the hovered point
    changed, all 8 000 points and 19 000 edges were rebuilt. Now only its own tiny mesh is.
  - Zooming only moves the points' corners to the new size, without rebuilding the mesh.
  - The selection is read from the editor's list of selected elements instead of checking every point and edge
    (falls back to the full check if the list doesn't match the counts).
  - Points are small octahedra instead of cubes: a third of the triangles.
  - The point size is measured from the structure's centre instead of the mesh origin.
- **Point size setting:** "point px" in the modelling tools panel, and F7 › Designer › OwnSelectionPointPixels. Now
  3 px by default; the old 7 px default is changed to 3 automatically.

## 6.12.5 (same protocol)
- **Own selection display reworked** after testing 6.12.4:
  - **Magenta edges fixed:** the edge material was the game's LineRenderer material, which doesn't work on a mesh.
    Everything now uses the material the game draws its gizmo meshes with, coloured with the game's selection
    colours.
  - **No more per-frame cost:** 6.12.4 rebuilt camera-facing strips every frame (25-30 ms). Now nothing depends on
    the camera:
    - edges are a line mesh;
    - points are small cubes, resized only when the camera distance changes by 30 %;
    - selected faces are a filled mesh.
    They're rebuilt only when the mesh or the selection changes, or the selected points move while dragging.
  - **The game's old red points no longer show up:** 6.12.4 let the game draw once to measure its sizes, again on
    every mode change. That measurement is gone; the point size is set in pixels (OwnSelectionPointPixels, 7).
  - **Edges mode no longer falls back to the game every frame:** 6.12.4 gave up and retried 4 times a second
    because the editor also draws a face mesh there.
  - **Faces mode is handled too:** selected faces are filled, edges drawn as lines.
  - The hover highlight (Points / Edges) is checked 15 times a second. It turns itself off if the check is slow.

## 6.12.4 (same protocol)
- **Own selection display for big structures (Points and Edges modes).** The game's editor rebuilt every point and
  edge as its own game object on each redraw. Now the mod draws them itself, the way 3D programs do:
  - It reads the mesh directly from the editor: positions, which vertices each edge joins, selection flags.
  - It keeps four meshes (points, selected points, edges, selected edges). Each point is a small camera-facing
    square and each edge a thin camera-facing strip. Sizes are measured from one redraw by the game, and colours
    and materials are the game's own.
  - Only what changed is updated:
    - the topology when the mesh changes;
    - the selection colours when the selection changes;
    - the selected points' positions while you drag;
    - the orientation when the camera or the structure moves.
  - The hovered point or edge is highlighted, checked 30 times a second.
  - The game's own redraw is skipped completely in these modes.
  - If anything goes wrong, it switches back to the game's display and says so in the log. Setting:
    F7 › Designer › OwnSelectionDisplay.
- Faces / Corners modes keep the batched display from 6.12.3 (FastSelectionDisplay).

## 6.12.3 (same protocol)
- **Big structures no longer lag in the structure editor.** The probe showed where the time went on the Ratte
  body: the editor drew about 19 400 edges and 8 400 points every frame, one game object each (a LineRenderer per
  edge, a cube per point). That took 175 ms of a 250 ms frame in the editor's DrawGizmos, so 4 fps.
  - For structures with at least 1500 edges + points, the mod now records what the editor draws without creating
    the objects, and turns it into a few meshes (one line mesh and one cube mesh per colour), drawn with the
    game's own gizmo renderer.
  - Later redraws reuse those meshes. They're rebuilt when the selection, the mesh or the select mode changes or
    the structure moves, and 4 times a second while the mouse moves (for the hover highlight).
  - Edges are 1 pixel thin.
  - Settings in F7 › Designer: FastSelectionDisplay, FastSelectionMinItems, FastSelectionHoverRefreshSeconds.
  - If the editor draws anything the mod doesn't record, the game's own way is used for that structure.

## 6.12.2 (same protocol)
- **DELETE STRUCTURE** button in the modelling tools: deletes the structure being edited, with the same undoable
  delete the designer uses when a part is dragged off the vehicle. Click it twice to confirm; Ctrl+Z brings it
  back. For parts on experimental blueprints that left click can't grab.
- **Designer camera around big vehicles:** the game's clip prevention (the camera pushing itself out of the
  armour) is off by default. The zoom-out limit is raised to at least 120 m and the pan limit to at least 60 m.
  The settings are in F7 › Designer. The camera's original values are written to the log, and battle cameras
  aren't changed.
- **Designer performance:**
  - While a structure is being edited, the mod no longer scans every text object in the scene twice a second
    (it was looking for the Ammo Rack panel). When the editor is closed, the full scan runs at most every
    1.5 s.
  - New performance probe (F7 › Designer › PerformanceLog). When the designer drops below 40 fps, the log says
    every 5 s how long the frame takes and how much of it is the mod, the structure editor's update, its
    selection gizmos and the gizmo renderers. It also counts how many points, edges and labels the editor
    draws.

## 6.12.1 (same protocol) - network security review
- **Joining:**
  - New `[Network] MaxPlayers` setting (default 16).
  - Connections that don't join within 10 s are closed, and at most 16 can wait at once.
  - After a refusal (wrong password, full, wrong version) the connection is closed.
  - 5 wrong passwords lock that address or Steam account out for 30 s, then longer each time.
  - The password is compared in constant time.
  - A second Hello from a player who already joined is ignored. Before, it gave them extra player slots.
- **Steam friend auto-accept (F8) can't be faked any more.** Before, it trusted the Steam id written by the
  sender. Now it uses the Steam id of the real Steam connection, as the host sees it (sent in the roster).
- **Shell settings received with a design only change that design.** Before, a received file could change
  any of your designs' settings. Values are checked too. In the lobby, they no longer replace the settings of
  your own design with the same name.
- **Everything read from the network is bounded:**
  - Forged lengths can't make the game allocate gigabytes.
  - Compressed designs can't unpack past 32 MB.
  - Steam message pieces are capped at 1 MB.
  - Unfinished transfers are limited per player.
  - F8 transfers by IP: designs up to 16 MB, pictures up to 8 MB, 3 at a time.
- **Values from other players are kept in range:**
  - Positions, speeds and rotations: no NaN or infinity.
  - Health fractions.
  - Shell parameters, so a forged spall count can't freeze the victim's game.
  - Shot attachments (60 KB).
  - Lobby environment settings (only plain numbers reach the battle setup file).
- **Flooding:** a connection with more than 4000 unread messages is cut off. More than 150 shots a second from
  one player are ignored. LAN search answers are limited to 20 a second.
- **Names, chat and other remote text are shown as plain text** (no formatting tags, control characters or
  newlines in names or logs). Windows device names (CON, NUL...) can't be used as design file names.

## 6.12.0 (same protocol)
- **Kill feed.** A list of the latest kills, top left by default: killer, the shell they last fired, victim and
  the cause (crew / ammo rack). Your own name is yellow. It also shows AI tanks you destroy and AI tanks that
  destroy you. Settings in F7 › KillFeed: on/off, position (X/Y in % of the screen), size, how long a line stays,
  number of lines, show shell.
- **The kill text and the destroyed banner can be hidden, moved and resized:** F7 › HUD (KillText... and
  DestroyedText...: shown, X/Y position in % of the screen, size). Near a screen edge, the text lines up on that
  edge.
- **Free camera.** [F5] (F7 › FreeCam › Key) turns it on in battle, alive or destroyed:
  - WASD moves, Q/E (or Ctrl/Space) go down/up, Shift is faster, Alt slower, the mouse wheel changes the speed.
  - The game's controls are paused while it's on, so your tank doesn't drive or aim. The mod's own keys still
    work. F5 goes back to your tank; Esc still opens the game's pause menu.
  - Speed and mouse sensitivity are in the settings.

## 6.11.1 (same protocol)
- **Range sight no longer vanishes.** Some shots report a launch speed of 0 m/s. The sight calibrated itself from
  them, which left it with no direction ("the gun points straight up or down") until the next good shot. Such
  shots are now ignored for calibration, and a bad stored calibration falls back to the barrel axis and the
  muzzle velocity.
- **Each fire group keeps its own shell.** Switching group now shows the shell that group's guns actually have
  loaded (AP if you never changed it), instead of the one you last picked on another group. The HUD and the shot
  now always match. This also follows group changes while the shell HUD is hidden (F3).

## 6.11.0 (same protocol)
- **F8 sending works through Steam.** Clicking a player under "Players" sends the design or settings through the
  game connection: compressed, in pieces, relayed by the host to that player only. It works with Steam, LAN or
  mixed players. Typing an IP still sends directly by TCP, as before. (The error "Hôte inconnu" / unknown host
  came from trying to reach a Steam player by IP.)
- **Nothing is saved without your say any more.** F8 › "Auto-accept from":
  - **STEAM FRIENDS** (default): designs from Steam friends are saved straight away.
  - **EVERYONE:** everything is saved straight away.
  - **NOBODY:** nothing is saved automatically.
  Everything else follows "OTHERS: ASK ME", which shows the transfer in F8 with ACCEPT / DENY (up to 8 waiting,
  a re-send replaces the earlier one), or "OTHERS: REFUSE SILENTLY", which refuses it with no notification.
  Settings follow the same rules and are still only applied when you click APPLY. Transfers sent by IP never count
  as from a friend. Tanks sent by the lobby for a battle are not affected.
  Settings: [Designs] AutoAccept, AskForOthers.

## 6.10.3 (same protocol)
- Fixed: "Invite to game" from Steam's own friends list was received but did nothing when accepted. A host with a
  public game is in a Steam lobby, so Steam sends a lobby invite (a different notice than the mod's invite
  buttons). Lobby invites are now handled: the lobby's host is joined, or the inviting friend if Steam hasn't
  sent the lobby's details within 5 s. This also works when Steam starts the game from the invite
  (+connect_lobby).

## 6.10.2 (same protocol)
- Fixed: the lobby's map arrows did nothing. The mod only kept maps flagged for the Sandbox mode, which none
  are, so the list held just the last map played. It now lists the Custom Battle (Deathmatch) maps: Defence,
  Dunes, Fields, No Tank's Land, Railway and Taiga. If no map is flagged, it lists every map.
- Lobby: a map search box next to the map name. Typing picks the first matching map, and the arrows cycle
  through the matches only.
- Lobby browser (F10): a search box (host name, every word typed), pages (6 games a page, < >), and results
  sorted with your version first, then by player count. Up to 200 Steam games are fetched (Steam's default is
  50). Player names are shown as plain text, so names containing "<" don't break the list.

## 6.10.1 (same protocol)
- Fixed: the range-marked sight sometimes followed the wrong gun, or disappeared (for example after a kill), in
  multiplayer. The mod read the fire group from the first vehicle controller in the scene, and other players'
  tanks have controllers too. It now uses the controller of your own tank. Inside a fire group with several
  guns, the gun you last fired is used. This also fixes which guns reload when you change shell type in
  multiplayer.
- When the sight can't be drawn, the log says why ("Range sight hidden: ...").

## 6.10.0 (protocol 19 - everyone must update)
- Fixed: other players' tanks could show up as burning wrecks with the turret blown off, again at every respawn.
  Health was sent as absolute values. When the two games didn't agree on a module's maximum (the ammo rack boost
  not applied yet on the owner's side, or different settings), the puppet's ammo racks dropped to about 25% and
  the game cooked them off. Health is now sent as a fraction of each module's maximum, which is right whatever
  maximum each game uses.

## 6.9.2 (same protocol)
- Steam joining: the host now also receives the "someone wants to connect" notice as an ordinary Steam callback,
  in case the networking callback doesn't fire.
- Steam diagnostics: while a Steam player is connecting, F10 shows the link state (connecting, finding a route,
  connected through relays / direct, or why it closed). The log records every change, every session request
  (accepted, or ignored because that player isn't hosting) and failed sends.

## 6.9.1 (same protocol)
- `steam_api64.dll` is checked when the game starts. If it's missing, 32-bit, too old (Steamworks SDK before
  1.58) or lacks a function the mod uses, Steam play stays off. The F10 Steam status then says why in red, e.g.
  "too old, Steamworks SDK 1.58 or newer is needed (missing SteamAPI_InitFlat, ...)". The log lists every
  missing function.
- `steam_api64.dll` is no longer included in the release (Valve's licence). See the README for how to add it.

## 6.9.0 (same protocol)
- **Lobby browser.** F10 › FIND GAMES lists public Steam games worldwide and games on your network together.
  Each row shows the host, players, in battle or in menus, [PASSWORD] and version. Click one to join (through
  Steam or LAN).
- **Public / private** (F10 button, Network › Visibility). Public games are listed in the browser and answer
  LAN searches. Private games are reachable only by invite or IP.
- **Password** (F10 field, Network › Password). When you host, players need it to join; when you join, it's
  sent to the host as a hash. Wrong or missing passwords are refused with a message. Older versions can still
  join games without a password.
- The Steam status is green when the relays are ready and red otherwise. While hosting, the F10 status shows
  public / private and whether there's a password.

## 6.8.0 (same protocol)
- **Play through Steam.** When you host, Steam friends can join from an invite: no IP, no ports, no VPN, and no
  player limit. Traffic goes direct when it can and through Valve's relays when it can't.
  - F10 › INVITE STEAM FRIENDS opens Steam's invite window. INVITE FRIENDS IN SPROCKET invites every friend
    currently in the game. Either one starts hosting if you aren't already.
  - Friends accept the invite in Steam, or use "Join Game" on you in their friends list. If their game isn't
    running, Steam starts it.
  - A host accepts LAN / Radmin (TCP) players and Steam players in the same game.
  - Needs `steam_api64.dll` next to SprocketLAN.dll, and Steam running. Setting: [Steam] Enabled (restart to
    apply), TimeoutSeconds. Without the DLL, everything works as before.
- The F10 status shows your Steam name and the relay network state.

## 6.7.1 (same protocol)
- The game's gun convergence readout ("300m") is moved to the right so it doesn't sit on the range marks
  (Sight › ConvergenceTextOffsetX / Y; 0 = the game's own position).

## 6.7.0 (same protocol)
- Scoreboard ([Tab], Scoreboard section: key, hold or toggle, on/off, reset each lobby battle): kills, deaths,
  K/D, shots, hits, penetrations, accuracy, ping (AI kills offline). Counted from what every player already
  receives, so nothing new goes over the network.
- Range-marked sight (Sight section): while scoped in, the selected fire group's trajectory is drawn as a curve
  of range marks (every 100 m, labelled every 500 m in hundreds). It is simulated from the real muzzle and
  projected through the sight, so it shows both the drop and the parallax between gun and sight; it follows the
  fire group and the selected shell's velocity, and calibrates itself from your first shot with each shell.
  Colour, sizes, ranges, drag and a toggle key are configurable.

## Release 6.6.5 - summary since 6.6.0
Same protocol (18): plays with 6.5.0 and newer.
- **Pen cam on the GPU**: everything (tank, x-ray, modules, plates, effects) is drawn by the GPU; high-poly
  custom hulls and turrets no longer disappear, and there's no stutter. Mesh limits are now settings.
- **Shell selector**: [F3] hides / shows it; it shows the gun's figures right away and follows the selected
  fire group (it used to wait for the first hit).
- **AI tanks**: knocked-out AI tanks stop instead of driving on; the kill sound on bots is more reliable.
- **Field repair progress ring**, shown in the third-person view only.

## Release 6.6.0 - summary since 6.2.0
Protocol **18**: players on 6.4.x or older can't join 6.5.0+ games (and the other way round).
- **Find LAN games** from the F10 menu, no more typing IPs on a local network.
- **Feature switches** (F7 › Features): shells & armour, pen cam, modelling tools, sandbox inspector changes.
- **AI tanks** get the mod's shells, armour model, pen cam and kill sound.
- **API for other mods** (API.md): custom shells now replicate onto puppets, own messages between players.
- **Modelling tools** in the compartment editor (F4): inset, polygon / round holes, round inset, knife.
- **Sandbox armour inspector** fires the mod's shells (with fuse settings).
- Impact marks on your own tank; APHE fuse delay from the arming plate; many pen cam timing fixes;
  HE / APHE fragment curves configurable; search boxes in the lobby and F8 lists.

## 6.6.5 (same protocol)
- The repair ring only shows in the third-person view, not while scoped in (Repair › ProgressRingThirdPersonOnly).

## 6.6.4 (same protocol)
- Knocked-out AI tanks (scenarios, custom battles) shut their engine down and brake instead of driving on with
  their last throttle. Settings (Battle): StopKnockedOutAiTanks, KnockedOutAiStopDelaySeconds (1 s),
  KnockedOutAiBrake (3 m/s², 0 = just engine off). Other players' tanks are never touched.
- Field repair progress ring (Repair): ShowProgressRing, ProgressRingPosition (Center / BelowCenter /
  BottomCenter / TopRight), ProgressRingSize.

## 6.6.3 (same protocol)
- The shell selector (top right) shows the gun's penetration / filler / fuse from the start of a battle or test
  drive, and switches to the selected fire group's gun right away (it used to wait for the first hit).
- AI tank kill sound: counted up to 10 s after your last hit (PenCam › KillSoundBotWindowSeconds), and a tank
  whose crew the game has taken control from counts as knocked out. When it doesn't count, the log says why.

## 6.6.2 (same protocol)
- Pen cam drawn entirely by the GPU: the solid tank, the hull x-ray, modules and crew, struck plates and the 2D
  effects (shell, fragments, flashes, blasts) are all rasterised by one command buffer into the pen cam texture.
  Nothing goes through uGUI meshes or per-vertex screen projection any more. The only per-frame CPU work left is
  the x-ray rim transparency per vertex (spread over all CPU cores) and a few hundred overlay vertices.
- New settings (PenCam): MaxMeshVertices, MaxDamageModelVertices, MaxDamageModelIndices,
  MaxOverlayChunkVertices, GpuRendering (off = the old CPU drawing, as a fallback).

## 6.6.1 (same protocol)
- [F3] (Settings › ShellHudKey) shows / hides the shell selector text at the top right in battle; remembered.
- Pen cam: high-poly custom hulls and turrets no longer disappear. The mesh limits are raised (render meshes up
  to 2M vertices, damage-model meshes up to 500k vertices / 1.5M indices), the uGUI chunks use 32-bit indices
  and grow as needed, and the hull's x-ray glow is drawn by the GPU into the pen cam's render texture (only
  the per-vertex rim alpha is computed per frame), so dense hulls no longer cost CPU projection every frame.

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

## 6.4.1 - 6.4.8
- Modelling tools fixes: no more crashes with round holes, inset / holes sized and connected properly (also on
  odd-shaped faces), new edges no longer stay stuck selected; panel moved to the bottom right.
- Removed the cut-across-edges, subdivide and bevel tools (they weren't useful).

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

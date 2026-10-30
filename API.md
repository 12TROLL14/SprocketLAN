# SprocketLAN API (for other mods)

`SprocketLAN.Api.Lan` (in SprocketLAN.dll, API version 1, mod 6.5.0+) lets your mod use SprocketLAN's multiplayer
link: send your own messages between players, and attach data to shots so the shot replayed on a puppet behaves
the same as on the shooter's machine.

Every player needs your mod **and** SprocketLAN. Messages from the API go over SprocketLAN's TCP link, so they
arrive reliably and in order. Call everything from the main thread (Update, Harmony patches), never from other
threads. Your handlers also run on the main thread.

## Setup

1. Reference `BepInEx/plugins/SprocketLAN/SprocketLAN.dll` in your .csproj, and set `<Private>false</Private>` so
   you don't ship a copy of it.
2. Declare the dependency:

```csharp
[BepInPlugin("me.mymod", "MyMod", "1.0.0")]
[BepInDependency("SprocketLAN", BepInDependency.DependencyFlags.SoftDependency)]
public class MyMod : BasePlugin { ... }
```

   With `SoftDependency` your mod also loads without SprocketLAN. In that case, keep every call to
   `SprocketLAN.Api` inside a separate class and only touch that class when
   `IL2CPPChainloader.Instance.Plugins.ContainsKey("SprocketLAN")` is true. With a hard dependency (no flag), you
   don't need that check.

## Session

| Member | Meaning |
|---|---|
| `Lan.ApiVersion`, `Lan.ModVersion` | API version (int) and SprocketLAN version ("6.5.0") |
| `Lan.Connected` | in a session, as host or as a client that finished joining |
| `Lan.IsHost` | we are the host |
| `Lan.LocalPlayerId` | our id (the host is 0), or 255 when not connected |
| `Lan.InBattle` | we are driving a tank in a battle (offline too) |
| `Lan.LocalTank` | our tank's GameObject, or null |
| `Lan.Players` | list of `PlayerInfo`, us included: `Id, Name, IsLocal, IsHost, InBattle, Vehicle, Tank, PingMs` |
| `Lan.TankOf(id)` | that player's tank in **our** scene: their puppet, or our own tank for our id |
| `Lan.PlayerOf(gameObject)` | id of the player driving the tank that object belongs to; 255 for AI tanks and scenery |
| `Lan.PlayerJoined`, `Lan.PlayerLeft` | events (`Action<PlayerInfo>`) |
| `Lan.ShellsEnabled`, `Lan.PenCamEnabled`, `Lan.ModellingToolsEnabled` | which SprocketLAN features the player has on (F7 › Features) |

## Messages

```csharp
Lan.RegisterChannel("MyMod.smoke", (sender, data) => {
    var pos = new Vector3(BitConverter.ToSingle(data, 0), BitConverter.ToSingle(data, 4), BitConverter.ToSingle(data, 8));
    SpawnSmoke(pos, Lan.TankOf(sender));
});

// later, when our player pops smoke:
var b = new byte[12];
BitConverter.TryWriteBytes(b.AsSpan(0), p.x); BitConverter.TryWriteBytes(b.AsSpan(4), p.y); BitConverter.TryWriteBytes(b.AsSpan(8), p.z);
Lan.Send("MyMod.smoke", b);            // to everyone else
Lan.SendTo(playerId, "MyMod.smoke", b); // to one player
```

- Use channel names that are unique to your mod, such as `"YourMod.thing"`. Each channel has one handler, and
  registering it again replaces the old one. `UnregisterChannel` removes it.
- `Send` / `SendTo` return false when not connected. The largest message is `Lan.MaxMessageBytes` (900 KB).
  For things that happen every frame, keep messages small and send them at most 10–20 times a second.
- You never receive your own messages. The sender id is filled in by the host, so a player can't fake it.
- Messages on channels nobody registered are dropped silently.

## Shots: making custom shells work on puppets

When a player fires, SprocketLAN sends the shot (weapon, position, velocity, projectile type) to everyone, and
each machine fires the same round from that player's puppet. Data your mod keeps on the shooter's side (a
custom fuse, a tracer colour, changed ballistics) doesn't exist on the other machines. Attach it to the shot so
it travels with it:

```csharp
// shooter side: called for every round WE fire (in battles, offline too)
Lan.LocalShot += shot => {
    if (!MyShellData.TryGetValue(shot.ProjectileId, out var mine)) return;   // ProjectileId = the game's ProjectileRegister id
    shot.Attach("MyMod.shell", mine.ToBytes());
};

// other machines: called right after the round was fired from the shooter's puppet
Lan.RemoteShot += shot => {
    if (!shot.TryGet("MyMod.shell", out var bytes)) return;
    var mine = MyShell.FromBytes(bytes);
    MyShellData[shot.ProjectileId] = mine;     // the puppet's round in OUR scene: apply the same behaviour to it
};
```

`ShotInfo` has the following members:
- `ShooterId`, `Weapon` (the gun's index in the tank), `Position`, `Direction` and `Velocity`.
- `ProjectileGuid` (the game's projectile type) and `ShellKind`.
- `Tank`: the shooter's tank in this scene.
- `ProjectileId`: the game's id of the round launched on this machine. It is 0 if unknown, or if a shell
  handler fired the round itself.
- `IsRemote`, `Attach(key, bytes)`, `TryGet(key, out bytes)` and `Attachments`.

Attachments can be up to 60000 bytes each and are sent with every shot, so keep them small. Players with an
older SprocketLAN can't join anyway, because the protocol version (18) has to match.

### Replacing the round completely

If your shell isn't a game projectile at all (for example a missile you simulate yourself), implement
`SprocketMP.Shells.IShellHandler` and register it with `Lan.RegisterShell(handler)` in your `Load()`:

- `Kind`: a unique number of 1 or more. 1–5 are used by SprocketLAN's AP / APHE / HE / APDS / APFSDS handlers,
  so pick 1000 or more.
- `ClaimLocalLaunch(shot, shooter, source, payload)`: return true if this launch is yours, and write whatever
  the other machines need into `payload`.
- `HandleRemote(shot, puppet)`: called on the other machines for shots of your `Kind`. Read `shot.Payload`, do
  the effect and return true. SprocketLAN then doesn't fire a normal round.

## Notes

- The damage a puppet takes is decided on the machine of the player who owns that tank: a shot on a puppet in
  your scene does nothing to the real tank. If your shell changes damage, run your hit logic on the victim's
  machine, for the round fired from the shooter's puppet in **their** scene, which `RemoteShot` tells you about.
  If you need to, report the result back with a message.
- AI tanks (bots) are only in the scene of whoever is playing against them. `PlayerOf` returns 255 for them.

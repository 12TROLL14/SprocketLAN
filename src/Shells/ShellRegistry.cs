using System;
using System.Collections.Generic;
using System.IO;
using SprocketMP.Net;

namespace SprocketMP.Shells
{
    /// <summary>
    /// Extension point for custom shell types (HE, APCR, HEAT, ... once they exist).
    ///
    /// Every replicated shot carries a ShellKind (ushort) and an opaque payload. Kind 0 = the game's own projectile,
    /// identified by its projectile-type GUID and launched natively on every machine (this is what the base AP shell uses).
    ///
    /// To add a shell type:
    ///   1. implement IShellHandler with a unique Kind (>= 1)
    ///   2. ShellRegistry.Register(new MyShell()) from your plugin's Load()
    ///   3. ClaimLocalLaunch: decide whether a local launch is "yours" (e.g. by projectile GUID / loaded ammo) and
    ///      write any extra data (fuse delay, filler mass, ...) into the payload
    ///   4. HandleRemote: spawn/simulate it on the receiving side. Return false to let the default native launch run
    ///      (useful when the shell is a normal registered projectile type that only needs extra payload data).
    /// </summary>
    public interface IShellHandler
    {
        ushort Kind { get; }
        string Name { get; }
        bool ClaimLocalLaunch(FireMsg shot, VehicleRig shooter, Il2CppSystem.Object source, BinaryWriter payload);
        bool HandleRemote(FireMsg shot, VehicleRig puppet);
    }

    public static class ShellRegistry
    {
        static readonly Dictionary<ushort, IShellHandler> _byKind = new Dictionary<ushort, IShellHandler>();
        static readonly List<IShellHandler> _ordered = new List<IShellHandler>();

        public static IReadOnlyList<IShellHandler> Handlers => _ordered;

        public static void Register(IShellHandler h)
        {
            if (h == null || h.Kind == 0) throw new ArgumentException("Shell kind 0 is reserved for native projectiles");
            if (_byKind.ContainsKey(h.Kind)) throw new ArgumentException($"Shell kind {h.Kind} already registered by {_byKind[h.Kind].Name}");
            _byKind[h.Kind] = h; _ordered.Add(h);
            MPLog.Info($"Shell handler registered: {h.Kind} = {h.Name}");
        }

        internal static void TagLocalLaunch(FireMsg shot, VehicleRig shooter, Il2CppSystem.Object source)
        {
            foreach (var h in _ordered)
            {
                try
                {
                    using var ms = new MemoryStream();
                    using var w = new BinaryWriter(ms);
                    if (h.ClaimLocalLaunch(shot, shooter, source, w))
                    {
                        w.Flush(); shot.ShellKind = h.Kind; shot.Payload = ms.ToArray();
                        return;
                    }
                }
                catch (Exception e) { MPLog.Warn($"Shell handler {h.Name} ClaimLocalLaunch: {e.Message}"); }
            }
        }

        internal static bool HandleRemote(FireMsg shot, VehicleRig puppet)
        {
            if (shot.ShellKind == 0) return false;
            if (!_byKind.TryGetValue(shot.ShellKind, out var h))
            {
                MPLog.Warn($"Unknown shell kind {shot.ShellKind} (missing shell add-on?) - falling back to native projectile");
                return false;
            }
            try { return h.HandleRemote(shot, puppet); }
            catch (Exception e) { MPLog.Error($"Shell handler {h.Name} HandleRemote: {e}"); return false; }
        }
    }
}

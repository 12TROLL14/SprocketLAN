using System;
using Sprocket.DamageModelling;

namespace SprocketMP
{
    /// <summary>Speeds up a just-launched projectile (APDS / APFSDS muzzle velocity multiplier). The projectile pool
    /// holds value-type instances, so the velocity is written straight into the pool array.</summary>
    public static unsafe class ShellVelocity
    {
        static bool _tried, _ok;
        static int _offPool, _elemSize, _offVel, _offId;

        static bool Init()
        {
            if (_tried) return _ok; _tried = true;
            try
            {
                var regK = Il2CppInterop.Runtime.Il2CppClassPointerStore<ProjectileRegister>.NativeClassPtr;
                var instK = Il2CppInterop.Runtime.Il2CppClassPointerStore<ProjectileInstance>.NativeClassPtr;
                _offPool = PenCapture.FieldOff(regK, "pool", false);
                uint align = 0; _elemSize = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_class_value_size(instK, ref align);
                _offVel = PenCapture.FieldOff(instK, "velocity", true);
                _offId = PenCapture.FieldOff(instK, "ID", true);
                _ok = _elemSize > 0 && _elemSize < 1024;
                MPLog.Info($"Shell velocity offsets: pool={_offPool} size={_elemSize} vel={_offVel} id={_offId} ok={_ok}");
            }
            catch (Exception e) { MPLog.Warn("Shell velocity offsets: " + e.Message); _ok = false; }
            return _ok;
        }

        public static bool Scale(IntPtr register, int projectileId, float mult)
        {
            if (register == IntPtr.Zero || Math.Abs(mult - 1f) < 0.001f || !Init()) return false;
            try
            {
                IntPtr arr = *(IntPtr*)((byte*)register + _offPool);
                if (arr == IntPtr.Zero) return false;
                long len = *(long*)((byte*)arr + 3 * IntPtr.Size);
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                for (long i = 0; i < len; i++)
                {
                    byte* e = data + i * _elemSize;
                    if (*(int*)(e + _offId) != projectileId) continue;
                    float* v = (float*)(e + _offVel);
                    v[0] *= mult; v[1] *= mult; v[2] *= mult;
                    return true;
                }
                MPLog.Info($"Shell velocity: projectile {projectileId} not found (this shot flies at normal speed)");
            }
            catch (Exception ex) { MPLog.Warn("Shell velocity: " + ex.Message); }
            return false;
        }
    }
}

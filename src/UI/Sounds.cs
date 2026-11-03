using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SprocketMP
{
    /// <summary>Plays WAV files with winmm PlaySound straight from memory (async: never stalls the game).
    /// Unity AudioClips can't be created from mod code in this IL2CPP build, and the MCI mp3 driver fails in-game.
    /// Volume is applied by scaling the samples. If an .mp3 is configured, the .wav next to it is used.</summary>
    public static class Sounds
    {
        [DllImport("winmm.dll", SetLastError = true)]
        static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
        const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

        class Snd { public GCHandle H; public byte[] Data; }
        static readonly Dictionary<string, Snd> _cache = new Dictionary<string, Snd>();
        static readonly HashSet<string> _failed = new HashSet<string>();
        static Snd _playing;

        static string Resolve(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            var p = Path.IsPathRooted(file) ? file : Path.Combine(BepInEx.Paths.PluginPath, "SprocketLAN", file);
            p = Path.GetFullPath(p);
            if (!p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { var w = Path.ChangeExtension(p, ".wav"); if (File.Exists(w)) return w; }
            return p;
        }

        public static void Preload(string file, float volume) { try { Get(file, volume); } catch (Exception e) { MPLog.Warn("Sound preload: " + e.Message); } }

        static Snd Get(string file, float volume)
        {
            var path = Resolve(file); if (path == null) return null;
            int vq = (int)Math.Round(Math.Clamp(volume, 0f, 3f) * 100f);
            string key = path + "|" + vq;
            if (_cache.TryGetValue(key, out var s)) return s;
            if (_failed.Contains(key)) return null;
            if (!File.Exists(path)) { _failed.Add(key); MPLog.Warn("Sound file not found: " + path); return null; }
            var b = File.ReadAllBytes(path);
            if (!Scale(b, vq / 100f, out string why)) { _failed.Add(key); MPLog.Warn($"Sound {Path.GetFileName(path)}: {why}"); return null; }
            s = new Snd { Data = b, H = GCHandle.Alloc(b, GCHandleType.Pinned) };
            _cache[key] = s;
            MPLog.Info($"Sound loaded: {Path.GetFileName(path)} (volume {vq}%)");
            return s;
        }

        /// <summary>Scales PCM samples in place.</summary>
        static bool Scale(byte[] b, float v, out string why)
        {
            why = null;
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A') { why = "not a WAV file (convert it to .wav)"; return false; }
            int fmt = 0, bits = 0, p = 12;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4); int len = BitConverter.ToInt32(b, p + 4);
                if (id == "fmt ") { fmt = BitConverter.ToInt16(b, p + 8); bits = BitConverter.ToInt16(b, p + 22); }
                else if (id == "data")
                {
                    if (fmt != 1 || bits != 16) { why = "only 16-bit PCM WAV supported"; return false; }
                    if (Math.Abs(v - 1f) < 0.001f) return true;
                    int end = Math.Min(b.Length, p + 8 + len);
                    for (int o = p + 8; o + 1 < end; o += 2)
                    {
                        int x = (short)(b[o] | (b[o + 1] << 8)); x = Math.Clamp((int)(x * v), -32768, 32767);
                        b[o] = (byte)x; b[o + 1] = (byte)(x >> 8);
                    }
                    return true;
                }
                p += 8 + len + (len & 1);
            }
            why = "no data chunk"; return false;
        }

        public static void Play(string file, float volume)
        {
            if (volume <= 0f) return;
            try
            {
                var s = Get(file, volume); if (s == null) return;
                _playing = s;
                if (!PlaySound(s.H.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT))
                    MPLog.Warn("Sound: PlaySound failed (" + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception e) { MPLog.Warn("Sound play: " + e.Message); }
        }

        public static void Update() { }
    }
}

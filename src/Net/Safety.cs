using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace SprocketMP.Net
{
    /// <summary>
    /// BinaryReader for data that came from the network. A length read from the data never makes us allocate more
    /// than the message actually holds (BinaryReader.ReadBytes / ReadString allocate the claimed length first, so a
    /// forged length of 2 GB would run the game out of memory).
    /// </summary>
    public sealed class SafeReader : BinaryReader
    {
        readonly int _maxUnseekable, _maxString;
        public SafeReader(Stream s, int maxUnseekable = 1 << 20, int maxString = 1 << 20) : base(s, Encoding.UTF8) { _maxUnseekable = maxUnseekable; _maxString = maxString; }
        public SafeReader(byte[] data) : this(new MemoryStream(data, false)) { }

        long Left => BaseStream.CanSeek ? BaseStream.Length - BaseStream.Position : _maxUnseekable;

        public override byte[] ReadBytes(int count)
        {
            if (count < 0 || count > Left) throw new InvalidDataException($"bad length {count}");
            return base.ReadBytes(count);
        }

        public override string ReadString()
        {
            int len = Read7BitEncodedInt();
            if (len < 0 || len > Left || len > _maxString) throw new InvalidDataException($"bad string length {len}");
            if (len == 0) return "";
            var b = base.ReadBytes(len);
            if (b.Length != len) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(b);
        }
    }

    /// <summary>Read-only view of a network stream that refuses to read more than a set number of bytes in total
    /// (a stranger can't make us read, and hold in memory, more than one transfer's worth).</summary>
    public sealed class LimitedStream : Stream
    {
        readonly Stream _s; long _left;
        public LimitedStream(Stream s, long max) { _s = s; _left = max; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) throw new InvalidDataException("transfer too big");
            int n = _s.Read(buffer, offset, (int)Math.Min(count, _left)); _left -= n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    public static class Safe
    {
        /// <summary>Text from another player, made safe to show: no rich-text tags, no control characters, limited length.</summary>
        public static string Text(string s, int max = 32)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (var c in s)
            {
                if (sb.Length >= max) break;
                if (c == '<') sb.Append('(');
                else if (c == '>') sb.Append(')');
                else if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Gunzip with a size limit (a tiny compressed blob can expand to gigabytes).</summary>
        public static byte[] Gunzip(byte[] packed, int maxBytes)
        {
            using var src = new GZipStream(new MemoryStream(packed, false), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > maxBytes) throw new InvalidDataException($"unpacks to more than {maxBytes / (1 << 20)} MB");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        public static float F(float v, float lim) => float.IsFinite(v) ? Mathf.Clamp(v, -lim, lim) : 0f;
        public static Vector3 V(Vector3 v, float lim) => new Vector3(F(v.x, lim), F(v.y, lim), F(v.z, lim));
        public static Quaternion Q(Quaternion q)
        {
            if (!float.IsFinite(q.x) || !float.IsFinite(q.y) || !float.IsFinite(q.z) || !float.IsFinite(q.w)) return Quaternion.identity;
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-4f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }
        /// <summary>Clamp a world position (the maps are a few km wide).</summary>
        public const float WorldLimit = 100000f;
    }
}

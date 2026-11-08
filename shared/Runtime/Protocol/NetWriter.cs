using System;
using System.Text;

namespace Game.Shared.Protocol
{
    /// <summary>Little-endian binary writer. No reflection, safe for IL2CPP/WebGL.</summary>
    public sealed class NetWriter
    {
        byte[] _buf;
        int _len;

        public NetWriter(int capacity = 256) { _buf = new byte[capacity]; }

        public int Length => _len;
        public void Reset() { _len = 0; }

        void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int size = _buf.Length * 2;
            while (size < _len + extra) size *= 2;
            Array.Resize(ref _buf, size);
        }

        public void WriteU8(byte v) { Ensure(1); _buf[_len++] = v; }
        public void WriteBool(bool v) { WriteU8(v ? (byte)1 : (byte)0); }

        public void WriteU16(ushort v)
        {
            Ensure(2);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
        }

        public void WriteU32(uint v)
        {
            Ensure(4);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)(v >> 16);
            _buf[_len++] = (byte)(v >> 24);
        }

        public void WriteI32(int v) { WriteU32(unchecked((uint)v)); }

        public void WriteU64(ulong v)
        {
            WriteU32((uint)v);
            WriteU32((uint)(v >> 32));
        }

        public void WriteF32(float v) { WriteI32(BitConverter.SingleToInt32Bits(v)); }
        public void WriteF64(double v) { WriteU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v))); }

        public void WriteString(string s)
        {
            if (string.IsNullOrEmpty(s)) { WriteU16(0); return; }
            int max = Encoding.UTF8.GetMaxByteCount(s.Length);
            Ensure(2 + max);
            int n = Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _len + 2);
            if (n > ushort.MaxValue) throw new ArgumentException("string too long");
            _buf[_len] = (byte)n;
            _buf[_len + 1] = (byte)(n >> 8);
            _len += 2 + n;
        }

        /// <summary>UTF-8 string with a u32 length (for large payloads such as the shared data tables).</summary>
        public void WriteLongString(string s)
        {
            if (string.IsNullOrEmpty(s)) { WriteU32(0); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteU32((uint)bytes.Length);
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _buf, _len, bytes.Length);
            _len += bytes.Length;
        }

        /// <summary>Position in meters → centimeters as int32.</summary>
        public void WritePos(float x, float y, float z)
        {
            WriteI32(Quant.ToCm(x));
            WriteI32(Quant.ToCm(y));
            WriteI32(Quant.ToCm(z));
        }

        public byte[] ToArray()
        {
            var r = new byte[_len];
            Buffer.BlockCopy(_buf, 0, r, 0, _len);
            return r;
        }
    }

    public static class Quant
    {
        public static int ToCm(float m) { return (int)Math.Round(m * 100.0); }
        public static float FromCm(int cm) { return cm / 100f; }

        /// <summary>Yaw degrees (any range) → u16.</summary>
        public static ushort YawToU16(float deg)
        {
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            return (ushort)((int)Math.Round(d / 360.0 * 65536.0) & 0xFFFF);
        }

        public static float YawFromU16(ushort v) { return v * (360f / 65536f); }
    }
}

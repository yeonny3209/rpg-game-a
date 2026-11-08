using System;
using System.Text;

namespace Game.Shared.Protocol
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string msg) : base(msg) { }
    }

    /// <summary>Little-endian binary reader over a byte segment. Throws ProtocolException on malformed input.</summary>
    public sealed class NetReader
    {
        readonly byte[] _buf;
        readonly int _end;
        int _pos;

        public NetReader(byte[] buf, int offset, int count)
        {
            _buf = buf;
            _pos = offset;
            _end = offset + count;
        }

        public NetReader(byte[] buf) : this(buf, 0, buf.Length) { }

        public int Remaining => _end - _pos;

        void Need(int n)
        {
            if (_pos + n > _end) throw new ProtocolException("unexpected end of packet");
        }

        public byte ReadU8() { Need(1); return _buf[_pos++]; }
        public bool ReadBool() { return ReadU8() != 0; }

        public ushort ReadU16()
        {
            Need(2);
            ushort v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint ReadU32()
        {
            Need(4);
            uint v = (uint)(_buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        public int ReadI32() { return unchecked((int)ReadU32()); }

        public ulong ReadU64()
        {
            ulong lo = ReadU32();
            ulong hi = ReadU32();
            return lo | (hi << 32);
        }

        public float ReadF32() { return BitConverter.Int32BitsToSingle(ReadI32()); }
        public double ReadF64() { return BitConverter.Int64BitsToDouble(unchecked((long)ReadU64())); }

        public string ReadString(int maxBytes = 4096)
        {
            int n = ReadU16();
            if (n > maxBytes) throw new ProtocolException("string too long");
            Need(n);
            string s = Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }

        public string ReadLongString(int maxBytes)
        {
            uint n = ReadU32();
            if (n > (uint)maxBytes) throw new ProtocolException("string too long");
            Need((int)n);
            string s = Encoding.UTF8.GetString(_buf, _pos, (int)n);
            _pos += (int)n;
            return s;
        }

        public void ReadPos(out float x, out float y, out float z)
        {
            x = Quant.FromCm(ReadI32());
            y = Quant.FromCm(ReadI32());
            z = Quant.FromCm(ReadI32());
        }

        static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        public float ReadFiniteF32()
        {
            float f = ReadF32();
            if (!IsFinite(f)) throw new ProtocolException("non-finite float");
            return f;
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace RaLanguage.Interpreter.Archive
{
    // Thin little-endian binary writer / reader pair. Centralised here so
    // every Ra archive writer/reader speaks the same on-wire encoding —
    // and so we never depend on `BinaryWriter`'s LEB128 / culture-aware
    // string encoding which is unstable across runtimes.
    //
    // Strings are written as `[u32 byteLen][utf8 bytes]`. A null string
    // is distinct from an empty string and encoded as `0xFFFF_FFFF`.

    public sealed class RacBinaryWriter
    {
        public Stream Stream { get; }

        public RacBinaryWriter(Stream stream)
        {
            Stream = stream;
        }

        public long Position => Stream.Position;

        public void WriteU8(byte v) => Stream.WriteByte(v);

        public void WriteU16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            b[0] = (byte)(v & 0xFF);
            b[1] = (byte)((v >> 8) & 0xFF);
            Stream.Write(b);
        }

        public void WriteU32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            b[0] = (byte)(v & 0xFF);
            b[1] = (byte)((v >> 8) & 0xFF);
            b[2] = (byte)((v >> 16) & 0xFF);
            b[3] = (byte)((v >> 24) & 0xFF);
            Stream.Write(b);
        }

        public void WriteI32(int v) => WriteU32(unchecked((uint)v));

        public void WriteU64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)((v >> (i * 8)) & 0xFF);
            Stream.Write(b);
        }

        public void WriteI64(long v) => WriteU64(unchecked((ulong)v));

        public void WriteBytes(ReadOnlySpan<byte> data) => Stream.Write(data);

        public void WriteString(string? s)
        {
            if (s == null)
            {
                WriteU32(0xFFFF_FFFFu);
                return;
            }
            if (s.Length == 0)
            {
                WriteU32(0u);
                return;
            }
            int byteCount = Encoding.UTF8.GetByteCount(s);
            WriteU32((uint)byteCount);
            // The stackalloc fast path tops out at 1024 chars; longer
            // strings allocate. We never hold the buffer past this call.
            if (byteCount <= 1024)
            {
                Span<byte> buf = stackalloc byte[byteCount];
                Encoding.UTF8.GetBytes(s, buf);
                Stream.Write(buf);
            }
            else
            {
                byte[] buf = Encoding.UTF8.GetBytes(s);
                Stream.Write(buf, 0, buf.Length);
            }
        }

        // Pad the stream with `count` zero bytes — used to reserve a
        // header that's filled in once content offsets are known.
        public void WriteZeros(int count)
        {
            if (count <= 0) return;
            Span<byte> b = stackalloc byte[Math.Min(count, 256)];
            b.Clear();
            int left = count;
            while (left > 0)
            {
                int take = Math.Min(left, b.Length);
                Stream.Write(b.Slice(0, take));
                left -= take;
            }
        }

        public void Seek(long position) => Stream.Position = position;
    }

    public sealed class RacBinaryReader
    {
        public Stream Stream { get; }

        public RacBinaryReader(Stream stream)
        {
            Stream = stream;
        }

        public long Position => Stream.Position;
        public long Length => Stream.Length;

        public byte ReadU8()
        {
            int v = Stream.ReadByte();
            if (v < 0) throw new EndOfStreamException("rac: unexpected EOF reading u8");
            return (byte)v;
        }

        public ushort ReadU16()
        {
            Span<byte> b = stackalloc byte[2];
            ReadExact(b);
            return (ushort)(b[0] | (b[1] << 8));
        }

        public uint ReadU32()
        {
            Span<byte> b = stackalloc byte[4];
            ReadExact(b);
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        public int ReadI32() => unchecked((int)ReadU32());

        public ulong ReadU64()
        {
            Span<byte> b = stackalloc byte[8];
            ReadExact(b);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)b[i]) << (i * 8);
            return v;
        }

        public long ReadI64() => unchecked((long)ReadU64());

        public byte[] ReadBytes(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return Array.Empty<byte>();
            byte[] buf = new byte[count];
            ReadExact(buf);
            return buf;
        }

        public void ReadExact(Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int n = Stream.Read(destination.Slice(total));
                if (n <= 0) throw new EndOfStreamException("rac: unexpected EOF");
                total += n;
            }
        }

        public string? ReadString()
        {
            uint len = ReadU32();
            if (len == 0xFFFF_FFFFu) return null;
            if (len == 0) return string.Empty;
            if (len > int.MaxValue / 2)
                throw new InvalidDataException($"rac: bogus string length {len}");
            byte[] buf = ReadBytes((int)len);
            return Encoding.UTF8.GetString(buf);
        }

        public void Seek(long position) => Stream.Position = position;
        public void Skip(long count) => Stream.Position += count;
    }
}

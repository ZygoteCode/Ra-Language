using System;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // Fixed 96-byte file header. Layout (little-endian throughout):
    //
    //   offset  size   field
    //   ------  ----   -----
    //     0      8     Magic                "RAC\0RAC\x01"
    //     8      2     FormatMajor          u16
    //    10      2     FormatMinor          u16
    //    12      4     Flags                RacFlags
    //    16      4     RaRuntimeRequired    packed u32 semver
    //    20      4     RaRuntimeBuiltWith   packed u32 semver
    //    24      4     SectionCount         u32
    //    28      4     reserved             u32 (zero)
    //    32      8     SectionTableOffset   u64
    //    40      8     ManifestOffset       u64 (== offset of Manifest section payload)
    //    48     32     DirectoryHash        SHA-256 over the section table bytes
    //    80     16     reserved             zeros
    //   ------  ----
    //    96            total
    //
    // Loaders MUST validate Magic, FormatMajor, RuntimeRequired
    // (against the running interpreter), and DirectoryHash before
    // touching any other section.
    public sealed class RacHeader
    {
        public ushort FormatMajor;
        public ushort FormatMinor;
        public RacFlags Flags;
        public uint RaRuntimeRequired;
        public uint RaRuntimeBuiltWith;
        public uint SectionCount;
        public ulong SectionTableOffset;
        public ulong ManifestOffset;
        public byte[] DirectoryHash;

        public RacHeader()
        {
            DirectoryHash = new byte[RacFormat.HashSize];
        }

        public void WriteTo(RacBinaryWriter w)
        {
            long start = w.Position;
            w.WriteBytes(RacFormat.Magic);
            w.WriteU16(FormatMajor);
            w.WriteU16(FormatMinor);
            w.WriteU32((uint)Flags);
            w.WriteU32(RaRuntimeRequired);
            w.WriteU32(RaRuntimeBuiltWith);
            w.WriteU32(SectionCount);
            w.WriteU32(0); // reserved
            w.WriteU64(SectionTableOffset);
            w.WriteU64(ManifestOffset);
            w.WriteBytes(DirectoryHash);
            w.WriteZeros(16); // reserved
            long written = w.Position - start;
            if (written != RacFormat.FileHeaderSize)
                throw new InvalidOperationException(
                    $"rac: internal header size mismatch (wrote {written}, expected {RacFormat.FileHeaderSize})");
        }

        public static RacHeader ReadFrom(RacBinaryReader r)
        {
            long start = r.Position;
            Span<byte> magic = stackalloc byte[RacFormat.Magic.Length];
            r.ReadExact(magic);
            if (!magic.SequenceEqual(RacFormat.Magic))
                throw new InvalidDataException("rac: not a Ra archive (bad magic)");

            var h = new RacHeader
            {
                FormatMajor = r.ReadU16(),
                FormatMinor = r.ReadU16(),
                Flags = (RacFlags)r.ReadU32(),
                RaRuntimeRequired = r.ReadU32(),
                RaRuntimeBuiltWith = r.ReadU32(),
                SectionCount = r.ReadU32(),
            };
            uint reserved0 = r.ReadU32();
            if (reserved0 != 0)
                throw new InvalidDataException("rac: reserved header field must be zero");
            h.SectionTableOffset = r.ReadU64();
            h.ManifestOffset = r.ReadU64();
            r.ReadExact(h.DirectoryHash);
            r.Skip(16); // reserved tail
            long read = r.Position - start;
            if (read != RacFormat.FileHeaderSize)
                throw new InvalidDataException($"rac: header size mismatch (read {read})");
            return h;
        }

        // Versions are packed (major:8 | minor:8 | patch:16). We use a
        // simple lexicographic compare per field — sufficient for the
        // 1.x line and easy to reason about.
        public static int CompareSemver(uint a, uint b)
        {
            int aMaj = (int)((a >> 24) & 0xFF);
            int bMaj = (int)((b >> 24) & 0xFF);
            if (aMaj != bMaj) return aMaj - bMaj;
            int aMin = (int)((a >> 16) & 0xFF);
            int bMin = (int)((b >> 16) & 0xFF);
            if (aMin != bMin) return aMin - bMin;
            int aPatch = (int)(a & 0xFFFF);
            int bPatch = (int)(b & 0xFFFF);
            return aPatch - bPatch;
        }

        public static string FormatSemver(uint v)
        {
            int maj = (int)((v >> 24) & 0xFF);
            int min = (int)((v >> 16) & 0xFF);
            int pat = (int)(v & 0xFFFF);
            return $"{maj}.{min}.{pat}";
        }
    }
}

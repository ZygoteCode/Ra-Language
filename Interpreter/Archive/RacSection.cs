using System;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // One entry in the central section directory. Layout (64 bytes):
    //
    //   offset  size   field
    //   ------  ----   -----
    //     0      4     Kind                  RacSectionKind
    //     4      4     Flags                 RacSectionFlags
    //     8      8     Offset                u64
    //    16      8     StoredSize            u64 (bytes in file, post-compression)
    //    24      8     UncompressedSize      u64 (logical content size)
    //    32     32     Hash                  SHA-256 over the UNCOMPRESSED content
    //   ------  ----
    //    64             total
    //
    // Hashes are intentionally over the *uncompressed* bytes so a
    // writer that re-compresses with different parameters does not
    // invalidate identity. A loader that just wants to copy a section
    // verbatim can still verify against the stored hash by
    // decompressing on the fly.
    public sealed class RacSectionEntry
    {
        public RacSectionKind Kind;
        public RacSectionFlags Flags;
        public ulong Offset;
        public ulong StoredSize;
        public ulong UncompressedSize;
        public byte[] Hash;

        public RacSectionEntry()
        {
            Hash = new byte[RacFormat.HashSize];
        }

        public bool IsCompressed => (Flags & RacSectionFlags.Compressed) != 0;
        public bool MustUnderstand => (Flags & RacSectionFlags.MustUnderstand) != 0;

        public void WriteTo(RacBinaryWriter w)
        {
            long start = w.Position;
            w.WriteU32((uint)Kind);
            w.WriteU32((uint)Flags);
            w.WriteU64(Offset);
            w.WriteU64(StoredSize);
            w.WriteU64(UncompressedSize);
            w.WriteBytes(Hash);
            long written = w.Position - start;
            if (written != RacFormat.SectionEntrySize)
                throw new InvalidOperationException(
                    $"rac: internal section entry size mismatch (wrote {written})");
        }

        public static RacSectionEntry ReadFrom(RacBinaryReader r)
        {
            long start = r.Position;
            var e = new RacSectionEntry
            {
                Kind = (RacSectionKind)r.ReadU32(),
                Flags = (RacSectionFlags)r.ReadU32(),
                Offset = r.ReadU64(),
                StoredSize = r.ReadU64(),
                UncompressedSize = r.ReadU64(),
            };
            r.ReadExact(e.Hash);
            long read = r.Position - start;
            if (read != RacFormat.SectionEntrySize)
                throw new InvalidDataException($"rac: section entry size mismatch (read {read})");
            return e;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace RaLanguage.Interpreter.Archive
{
    // High-level archive builder. Callers append sections in any order,
    // then call `Finish(stream)` which lays out the file in the canonical
    // sequence:
    //
    //   [FileHeader  (96 bytes, with placeholder fields)]
    //   [Section payload 0]
    //   [Section payload 1]
    //   [...]
    //   [Section directory  (sectionCount * 64 bytes)]
    //
    // and then back-patches the header.
    //
    // Each section's payload is hashed UNCOMPRESSED and optionally
    // compressed before it lands on disk. The hash + uncompressed
    // size + stored size go into the directory entry. The Manifest
    // section MUST be appended exactly once; its directory index is
    // captured into ManifestOffset.
    public sealed class RacWriter
    {
        private readonly List<PendingSection> _pending = new();
        private int _manifestIndex = -1;

        public RacFlags ArchiveFlags { get; set; } = RacFlags.None;
        public uint RaRuntimeRequired { get; set; } = RacFormat.RaRuntimeVersion;
        public uint RaRuntimeBuiltWith { get; set; } = RacFormat.RaRuntimeVersion;

        // Add a section. Returns the directory index it will receive
        // (sequential, 0-based). The payload bytes are buffered until
        // `Finish` is called; the writer keeps a reference but does not
        // copy them.
        public int AddSection(RacSectionKind kind, byte[] uncompressedPayload,
            bool compress = true, bool mustUnderstand = false)
        {
            if (uncompressedPayload == null)
                throw new ArgumentNullException(nameof(uncompressedPayload));
            if (uncompressedPayload.LongLength > RacFormat.MaxSectionSize)
                throw new InvalidOperationException(
                    $"rac: section {kind} exceeds max size ({uncompressedPayload.LongLength} bytes)");

            int idx = _pending.Count;
            if (kind == RacSectionKind.Manifest)
            {
                if (_manifestIndex >= 0)
                    throw new InvalidOperationException("rac: Manifest section already added");
                _manifestIndex = idx;
                mustUnderstand = true;
            }

            var flags = RacSectionFlags.None;
            if (mustUnderstand) flags |= RacSectionFlags.MustUnderstand;

            byte[] storedBytes;
            if (compress && uncompressedPayload.Length > 0)
            {
                storedBytes = CompressDeflate(uncompressedPayload);
                // Refuse to compress when the result is no better than
                // the source (small or already-random payloads). The
                // overhead is mostly the 16-byte deflate frame and a
                // few control bytes; once it exceeds 1% of the payload
                // we drop the flag to save the decompress cost on the
                // hot path.
                if (storedBytes.Length >= uncompressedPayload.Length)
                {
                    storedBytes = uncompressedPayload;
                }
                else
                {
                    flags |= RacSectionFlags.Compressed;
                }
            }
            else
            {
                storedBytes = uncompressedPayload;
            }

            var entry = new RacSectionEntry
            {
                Kind = kind,
                Flags = flags,
                Offset = 0, // filled in by Finish
                StoredSize = (ulong)storedBytes.LongLength,
                UncompressedSize = (ulong)uncompressedPayload.LongLength,
                Hash = RacIntegrity.Hash(uncompressedPayload),
            };
            _pending.Add(new PendingSection(entry, storedBytes));
            return idx;
        }

        public void Finish(Stream output)
        {
            if (_manifestIndex < 0)
                throw new InvalidOperationException("rac: no Manifest section provided");
            if (!output.CanSeek)
                throw new InvalidOperationException("rac: output stream must be seekable");

            var header = new RacHeader
            {
                FormatMajor = RacFormat.FormatMajor,
                FormatMinor = RacFormat.FormatMinor,
                Flags = ArchiveFlags,
                RaRuntimeRequired = RaRuntimeRequired,
                RaRuntimeBuiltWith = RaRuntimeBuiltWith,
                SectionCount = (uint)_pending.Count,
            };

            var w = new RacBinaryWriter(output);

            // 1. Reserve header.
            long headerStart = output.Position;
            w.WriteZeros(RacFormat.FileHeaderSize);

            // 2. Write each section payload, capturing the offset.
            foreach (var p in _pending)
            {
                p.Entry.Offset = (ulong)output.Position;
                output.Write(p.StoredBytes, 0, p.StoredBytes.Length);
            }

            // 3. Write the section directory.
            long dirStart = output.Position;
            foreach (var p in _pending)
            {
                p.Entry.WriteTo(w);
            }
            long dirEnd = output.Position;

            // 4. Compute directory hash by re-reading the bytes we
            //    just wrote. Keeps `Finish` allocation-light.
            long savedPos = output.Position;
            output.Position = dirStart;
            byte[] dirBytes = new byte[dirEnd - dirStart];
            int total = 0;
            while (total < dirBytes.Length)
            {
                int n = output.Read(dirBytes, total, dirBytes.Length - total);
                if (n <= 0) throw new IOException("rac: short read during directory hash");
                total += n;
            }
            header.DirectoryHash = RacIntegrity.Hash(dirBytes);
            output.Position = savedPos;

            // 5. Back-patch the header.
            header.SectionTableOffset = (ulong)dirStart;
            header.ManifestOffset = _pending[_manifestIndex].Entry.Offset;
            output.Position = headerStart;
            header.WriteTo(w);

            // 6. Settle the position past the directory so callers
            //    flushing get a consistent stream length.
            output.Position = dirEnd;
        }

        private static byte[] CompressDeflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                ds.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        private readonly struct PendingSection
        {
            public readonly RacSectionEntry Entry;
            public readonly byte[] StoredBytes;

            public PendingSection(RacSectionEntry entry, byte[] stored)
            {
                Entry = entry;
                StoredBytes = stored;
            }
        }
    }
}

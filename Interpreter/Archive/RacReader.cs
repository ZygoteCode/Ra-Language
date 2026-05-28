using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace RaLanguage.Interpreter.Archive
{
    // High-level archive loader. Validates the header, directory hash
    // and the Manifest section's own hash up-front. Other sections are
    // verified lazily on the first `ReadSection(index)` call so the
    // startup cost stays proportional to the manifest size.
    public static class RacReader
    {
        public static RacArchive Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path", nameof(path));
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return OpenInternal(path, fs, ownsStream: true);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public static RacArchive Open(Stream stream, string label = "<stream>")
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return OpenInternal(label, stream, ownsStream: false);
        }

        private static RacArchive OpenInternal(string path, Stream stream, bool ownsStream)
        {
            if (!stream.CanSeek)
                throw new InvalidDataException("rac: input stream must be seekable");
            if (stream.Length > RacFormat.MaxArchiveSize)
                throw new InvalidDataException(
                    $"rac: archive too large ({stream.Length} bytes; max {RacFormat.MaxArchiveSize})");
            if (stream.Length < RacFormat.FileHeaderSize)
                throw new InvalidDataException("rac: file too small to contain a header");

            stream.Position = 0;
            var r = new RacBinaryReader(stream);
            var header = RacHeader.ReadFrom(r);

            if (header.FormatMajor != RacFormat.FormatMajor)
                throw new InvalidDataException(
                    $"rac: incompatible format version {header.FormatMajor}.{header.FormatMinor} (loader expects {RacFormat.FormatMajor}.x)");

            if (RacHeader.CompareSemver(header.RaRuntimeRequired, RacFormat.RaRuntimeVersion) > 0)
                throw new InvalidDataException(
                    $"rac: archive requires Ra runtime {RacHeader.FormatSemver(header.RaRuntimeRequired)}, host is {RacHeader.FormatSemver(RacFormat.RaRuntimeVersion)}");

            // Read the section directory whole and verify its hash.
            long dirOff = (long)header.SectionTableOffset;
            long dirSize = (long)header.SectionCount * RacFormat.SectionEntrySize;
            if (dirOff <= 0 || dirOff + dirSize > stream.Length)
                throw new InvalidDataException("rac: section directory offset/size out of range");

            stream.Position = dirOff;
            byte[] dirBytes = new byte[dirSize];
            r.ReadExact(dirBytes);
            byte[] dirHash = RacIntegrity.Hash(dirBytes);
            if (!RacIntegrity.Equal(dirHash, header.DirectoryHash))
                throw new InvalidDataException("rac: section directory hash mismatch (archive corrupted)");

            // Parse the directory.
            var sections = new RacSectionEntry[header.SectionCount];
            using (var ms = new MemoryStream(dirBytes, writable: false))
            {
                var rd = new RacBinaryReader(ms);
                for (int i = 0; i < sections.Length; i++)
                {
                    sections[i] = RacSectionEntry.ReadFrom(rd);
                    var e = sections[i];
                    if ((long)e.Offset < 0 || (long)e.Offset >= stream.Length)
                        throw new InvalidDataException(
                            $"rac: section #{i} offset out of range ({e.Offset})");
                    if (e.StoredSize > (ulong)RacFormat.MaxSectionSize)
                        throw new InvalidDataException(
                            $"rac: section #{i} oversized ({e.StoredSize})");
                    if ((long)e.Offset + (long)e.StoredSize > stream.Length)
                        throw new InvalidDataException(
                            $"rac: section #{i} extends past archive end");
                }
            }

            // Locate manifest by offset (header.ManifestOffset). It must
            // correspond to exactly one section whose Kind == Manifest.
            int manifestIdx = -1;
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i].Kind == RacSectionKind.Manifest)
                {
                    if (manifestIdx >= 0)
                        throw new InvalidDataException("rac: more than one Manifest section");
                    if (sections[i].Offset != header.ManifestOffset)
                        throw new InvalidDataException("rac: ManifestOffset does not match directory entry");
                    manifestIdx = i;
                }
            }
            if (manifestIdx < 0)
                throw new InvalidDataException("rac: missing required Manifest section");

            // Refuse MustUnderstand sections we do not recognise.
            for (int i = 0; i < sections.Length; i++)
            {
                if (!sections[i].MustUnderstand) continue;
                if (!IsKnownKind(sections[i].Kind))
                    throw new InvalidDataException(
                        $"rac: section #{i} kind {(uint)sections[i].Kind:X8} is marked must-understand but unknown to this runtime");
            }

            byte[] manifestPayload = ReadSectionPayload(stream, sections[manifestIdx]);
            var manifest = RacManifest.Deserialize(manifestPayload);
            // Bind the section directory snapshot to the manifest so
            // RacInspector and RacRunner can correlate by index.
            manifest.Sections = new List<RacSectionEntry>(sections);

            // Validate manifest cross-references against the directory.
            for (int i = 0; i < manifest.Modules.Count; i++)
            {
                var m = manifest.Modules[i];
                if (m.SourceSectionIndex < -1 || m.SourceSectionIndex >= sections.Length)
                    throw new InvalidDataException(
                        $"rac: manifest module #{i} source section index out of range");
                if (m.SourceSectionIndex >= 0 &&
                    sections[m.SourceSectionIndex].Kind != RacSectionKind.ModuleSource)
                    throw new InvalidDataException(
                        $"rac: manifest module #{i} points at non-source section");
                if (m.BytecodeSectionIndex < -1 || m.BytecodeSectionIndex >= sections.Length)
                    throw new InvalidDataException(
                        $"rac: manifest module #{i} bytecode section index out of range");
                if (m.BytecodeSectionIndex >= 0 &&
                    sections[m.BytecodeSectionIndex].Kind != RacSectionKind.ModuleBytecode)
                    throw new InvalidDataException(
                        $"rac: manifest module #{i} bytecode index points at wrong kind");
                foreach (var imp in m.Imports)
                {
                    if (imp < 0 || imp >= manifest.Modules.Count)
                        throw new InvalidDataException(
                            $"rac: manifest module #{i} has out-of-range import target {imp}");
                }
            }

            return new RacArchive(path, stream, ownsStream, header, sections, manifest);
        }

        // Reads (and verifies) a single section's payload, performing
        // decompression if needed. Stable to call multiple times — the
        // archive carries the source stream until disposed.
        internal static byte[] ReadSectionPayload(Stream stream, RacSectionEntry entry)
        {
            stream.Position = (long)entry.Offset;
            byte[] stored = new byte[(int)entry.StoredSize];
            int total = 0;
            while (total < stored.Length)
            {
                int n = stream.Read(stored, total, stored.Length - total);
                if (n <= 0) throw new EndOfStreamException("rac: short read on section payload");
                total += n;
            }

            byte[] uncompressed;
            if (entry.IsCompressed)
            {
                uncompressed = DecompressDeflate(stored, (int)entry.UncompressedSize);
            }
            else
            {
                if (entry.StoredSize != entry.UncompressedSize)
                    throw new InvalidDataException("rac: uncompressed section has mismatched sizes");
                uncompressed = stored;
            }

            byte[] hash = RacIntegrity.Hash(uncompressed);
            if (!RacIntegrity.Equal(hash, entry.Hash))
                throw new InvalidDataException(
                    $"rac: section payload hash mismatch (kind {entry.Kind})");

            return uncompressed;
        }

        private static byte[] DecompressDeflate(byte[] compressed, int expectedSize)
        {
            using var ms = new MemoryStream(compressed, writable: false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            // Pre-size when we know the expected length — avoids the
            // copy-back of MemoryStream's growing buffer.
            byte[] outBuf = new byte[expectedSize];
            int total = 0;
            while (total < expectedSize)
            {
                int n = ds.Read(outBuf, total, expectedSize - total);
                if (n <= 0) break;
                total += n;
            }
            if (total != expectedSize)
                throw new InvalidDataException(
                    $"rac: deflate underflow (got {total} bytes, expected {expectedSize})");
            // Verify the deflate stream is fully consumed — a bogus
            // payload could lie about its uncompressed size.
            if (ds.Read(new byte[1], 0, 1) != 0)
                throw new InvalidDataException("rac: deflate stream had trailing bytes");
            return outBuf;
        }

        private static bool IsKnownKind(RacSectionKind kind)
        {
            return kind == RacSectionKind.Manifest
                || kind == RacSectionKind.ModuleSource
                || kind == RacSectionKind.ModuleBytecode
                || kind == RacSectionKind.DebugInfo
                || kind == RacSectionKind.StdLibIndex
                || kind == RacSectionKind.Signature
                || kind == RacSectionKind.Custom;
        }
    }
}

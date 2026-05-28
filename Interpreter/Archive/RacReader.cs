using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RaLanguage.Interpreter.Archive
{
    // High-level archive loader. Validates the header, directory hash
    // and the Manifest section's own hash up-front. Other sections are
    // verified lazily on the first `ReadSection(index)` call so the
    // startup cost stays proportional to the manifest size, regardless
    // of how large the archive itself is.
    //
    // v1.1 (#4): file-backed archives are opened via MemoryMappedFile.
    // The header (96 bytes) and section directory (64 bytes per entry)
    // are the only regions touched at open; subsequent section reads
    // open per-call view streams so the OS pages in payload bytes only
    // when they are first accessed. A 100 MB archive of which the
    // entry module is 4 KB now opens in roughly the time it takes the
    // kernel to fault the first page of the file in — typically well
    // under a millisecond.
    public static class RacReader
    {
        public static RacArchive Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path", nameof(path));
            RacSource source = new MappedRacSource(path);
            try
            {
                return OpenInternal(path, source);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public static RacArchive Open(Stream stream, string label = "<stream>")
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            RacSource source = new StreamRacSource(stream, ownsStream: false);
            try
            {
                return OpenInternal(label, source);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        private static RacArchive OpenInternal(string path, RacSource source)
        {
            if (source.Length > RacFormat.MaxArchiveSize)
                throw new InvalidDataException(
                    $"rac: archive too large ({source.Length} bytes; max {RacFormat.MaxArchiveSize})");
            if (source.Length < RacFormat.FileHeaderSize)
                throw new InvalidDataException("rac: file too small to contain a header");

            // Read the 96-byte header from the source. This is the single
            // mandatory upfront page-fault.
            byte[] headerBytes = new byte[RacFormat.FileHeaderSize];
            source.ReadExact(0, headerBytes);
            RacHeader header;
            using (var ms = new MemoryStream(headerBytes, writable: false))
            {
                var hr = new RacBinaryReader(ms);
                header = RacHeader.ReadFrom(hr);
            }

            if (header.FormatMajor != RacFormat.FormatMajor)
                throw new InvalidDataException(
                    $"rac: incompatible format version {header.FormatMajor}.{header.FormatMinor} (loader expects {RacFormat.FormatMajor}.x)");

            if (RacHeader.CompareSemver(header.RaRuntimeRequired, RacFormat.RaRuntimeVersion) > 0)
                throw new InvalidDataException(
                    $"rac: archive requires Ra runtime {RacHeader.FormatSemver(header.RaRuntimeRequired)}, host is {RacHeader.FormatSemver(RacFormat.RaRuntimeVersion)}");

            // Section directory: parse + verify hash. We still hash the
            // directory eagerly — its size is O(sectionCount * 64) and a
            // tamper-detection failure here lets us bail before
            // trusting any section offset / hash field.
            long dirOff = (long)header.SectionTableOffset;
            long dirSize = (long)header.SectionCount * RacFormat.SectionEntrySize;
            if (dirOff <= 0 || dirOff + dirSize > source.Length)
                throw new InvalidDataException("rac: section directory offset/size out of range");
            byte[] dirBytes = new byte[dirSize];
            source.ReadExact(dirOff, dirBytes);
            byte[] dirHash = RacIntegrity.Hash(dirBytes);
            if (!RacIntegrity.Equal(dirHash, header.DirectoryHash))
                throw new InvalidDataException("rac: section directory hash mismatch (archive corrupted)");

            // Parse the directory entries.
            var sections = new RacSectionEntry[header.SectionCount];
            using (var ms = new MemoryStream(dirBytes, writable: false))
            {
                var rd = new RacBinaryReader(ms);
                for (int i = 0; i < sections.Length; i++)
                {
                    sections[i] = RacSectionEntry.ReadFrom(rd);
                    var e = sections[i];
                    if ((long)e.Offset < 0 || (long)e.Offset >= source.Length)
                        throw new InvalidDataException(
                            $"rac: section #{i} offset out of range ({e.Offset})");
                    if (e.StoredSize > (ulong)RacFormat.MaxSectionSize)
                        throw new InvalidDataException(
                            $"rac: section #{i} oversized ({e.StoredSize})");
                    if ((long)e.Offset + (long)e.StoredSize > source.Length)
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

            byte[] manifestPayload = ReadSectionPayload(source, sections[manifestIdx]);
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

            return new RacArchive(path, source, header, sections, manifest);
        }

        // Reads (and verifies) a single section's payload, performing
        // decompression if needed. The reads stream through a per-call
        // view over the underlying RacSource — for the mmap-backed
        // source the OS pages in only the bytes that get touched.
        internal static byte[] ReadSectionPayload(RacSource source, RacSectionEntry entry)
        {
            long offset = (long)entry.Offset;
            long stored = (long)entry.StoredSize;
            long uncomp = (long)entry.UncompressedSize;
            byte[] uncompressed;
            if (entry.IsCompressed)
            {
                // Decompress straight out of the view stream. No
                // intermediate `stored` buffer — DeflateStream consumes
                // the view incrementally and we write directly into
                // `uncompressed`.
                using var view = source.OpenView(offset, stored);
                uncompressed = DecompressDeflate(view, (int)uncomp);
            }
            else
            {
                if (entry.StoredSize != entry.UncompressedSize)
                    throw new InvalidDataException("rac: uncompressed section has mismatched sizes");
                uncompressed = new byte[(int)stored];
                source.ReadExact(offset, uncompressed);
            }

            byte[] hash = RacIntegrity.Hash(uncompressed);
            if (!RacIntegrity.Equal(hash, entry.Hash))
                throw new InvalidDataException(
                    $"rac: section payload hash mismatch (kind {entry.Kind})");

            return uncompressed;
        }

        private static byte[] DecompressDeflate(Stream compressed, int expectedSize)
        {
            using var ds = new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: false);
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
                || kind == RacSectionKind.SharedConstPool
                || kind == RacSectionKind.Custom;
        }
    }
}

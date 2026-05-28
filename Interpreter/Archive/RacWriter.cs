using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ZstdSharp;

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

        // v2.0 (#zstd): codec used for the Compressed flag. Default
        // Zstd; --legacy-deflate CLI flag (or callers that own the
        // writer directly) may flip back to Deflate for diagnostics
        // or for producing an archive a v1.x reader can still
        // decompress (the FormatMajor bump still locks v1.x readers
        // out, but the per-section codec stays addressable).
        public RacCodecKind Codec { get; set; } = RacCodecKind.Zstd;

        // Zstd compression level (1..22). Level 3 is fast pack;
        // level 19+ is high-ratio but slow. Default 11 mirrors
        // `CompressionLevel.Optimal` on Deflate — a reasonable
        // size/time balance for archives that ship once and load
        // often. CLI may override via --zstd-level <N>.
        public int ZstdLevel { get; set; } = 11;

        // v1.2 (#sig): signing config. When non-null, Finish() appends
        // a Signature section over the canonical payload (= file-header
        // identity bits + every non-signature directory entry) and
        // sets RacFlags.Signed.
        private RacKeyPair? _signKey;
        private string _signerId = "";
        private RacSignatureKeyMode _signKeyMode = RacSignatureKeyMode.Embedded;

        public void SignWith(RacKeyPair key, string signerId, RacSignatureKeyMode mode = RacSignatureKeyMode.Embedded)
        {
            _signKey = key ?? throw new ArgumentNullException(nameof(key));
            _signerId = signerId ?? "";
            _signKeyMode = mode;
            ArchiveFlags |= RacFlags.Signed;
        }

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
                storedBytes = Codec switch
                {
                    RacCodecKind.Zstd => CompressZstd(uncompressedPayload, ZstdLevel),
                    RacCodecKind.Deflate => CompressDeflate(uncompressedPayload),
                    _ => throw new InvalidOperationException($"rac: unknown codec {Codec}"),
                };
                // Refuse to compress when the result is no better than
                // the source (small or already-random payloads). The
                // overhead is mostly the codec frame; once it exceeds
                // the savings we drop the flag to save the decompress
                // cost on the hot path.
                if (storedBytes.Length >= uncompressedPayload.Length)
                {
                    storedBytes = uncompressedPayload;
                }
                else
                {
                    flags |= RacSectionFlags.Compressed;
                    // Pack codec id into bits 4-7 (no-op when
                    // Codec == Deflate since RacCodecKind.Deflate == 0).
                    flags |= (RacSectionFlags)((uint)Codec << 4);
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
                SectionCount = (uint)_pending.Count, // patched if a signature is appended
            };

            var w = new RacBinaryWriter(output);

            // 1. Reserve header.
            long headerStart = output.Position;
            w.WriteZeros(RacFormat.FileHeaderSize);

            // 2. Write each non-signature section payload, capturing
            //    the offset. The signature itself (if any) gets
            //    appended in step 3, after we know the offsets of
            //    every section it has to sign over.
            foreach (var p in _pending)
            {
                p.Entry.Offset = (ulong)output.Position;
                output.Write(p.StoredBytes, 0, p.StoredBytes.Length);
            }

            // 3. Sign + append. The canonical payload includes every
            //    non-signature entry's directory record (kind, flags,
            //    offset, sizes, hash) plus the file header's identity
            //    bits — so any tamper either invalidates the embedded
            //    signature or invalidates the directory hash that
            //    every loader checks unconditionally.
            int signatureIndex = -1;
            if (_signKey != null)
            {
                var signedEntries = new List<RacSectionEntry>(_pending.Count);
                foreach (var p in _pending) signedEntries.Add(p.Entry);
                byte[] canonical = RacSigner.BuildCanonicalPayload(
                    header.FormatMajor, header.FormatMinor,
                    header.RaRuntimeRequired, header.RaRuntimeBuiltWith,
                    _pending[_manifestIndex].Entry.Offset,
                    signedEntries,
                    signatureSectionIndex: -1);
                byte[] sigBlob = RacSigner.SignCanonical(_signKey, canonical);

                var sigSection = new RacSignatureSection
                {
                    Algorithm = _signKey.Algorithm,
                    KeyMode = _signKeyMode,
                    SignerId = _signerId,
                    Fingerprint = _signKey.Fingerprint(),
                    PublicKey = _signKeyMode == RacSignatureKeyMode.Embedded
                        ? _signKey.PublicKey
                        : Array.Empty<byte>(),
                    Signature = sigBlob,
                };
                byte[] sigPayload = sigSection.Encode();

                // Signature payload stays uncompressed: it's already a
                // dense binary blob and the loader needs predictable
                // bounds.
                var sigEntry = new RacSectionEntry
                {
                    Kind = RacSectionKind.Signature,
                    Flags = RacSectionFlags.MustUnderstand,
                    Offset = (ulong)output.Position,
                    StoredSize = (ulong)sigPayload.LongLength,
                    UncompressedSize = (ulong)sigPayload.LongLength,
                    Hash = RacIntegrity.Hash(sigPayload),
                };
                output.Write(sigPayload, 0, sigPayload.Length);
                signatureIndex = _pending.Count;
                _pending.Add(new PendingSection(sigEntry, sigPayload));
                header.SectionCount = (uint)_pending.Count;
            }

            // 4. Write the section directory.
            long dirStart = output.Position;
            foreach (var p in _pending)
            {
                p.Entry.WriteTo(w);
            }
            long dirEnd = output.Position;

            // 5. Compute directory hash by re-reading the bytes we
            //    just wrote. Keeps `Finish` allocation-light. The
            //    directory hash covers the signature entry too — a
            //    tamper that flips a signature byte therefore fails
            //    the cheap directory-hash check before the verifier
            //    is even consulted.
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

            // 6. Back-patch the header.
            header.SectionTableOffset = (ulong)dirStart;
            header.ManifestOffset = _pending[_manifestIndex].Entry.Offset;
            output.Position = headerStart;
            header.WriteTo(w);

            // 7. Settle the position past the directory so callers
            //    flushing get a consistent stream length.
            output.Position = dirEnd;
            _ = signatureIndex; // reserved for future inspection hooks
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

        // Thread-local Zstd compressor keyed on the level. Allocates
        // a fresh context only when the requested level differs from
        // the cached one — within a single Build the level is fixed,
        // so the steady-state cost is one Compressor for the entire
        // archive build regardless of section count.
        [ThreadStatic] private static Compressor? t_zstdComp;
        [ThreadStatic] private static int t_zstdCompLevel;

        private static byte[] CompressZstd(byte[] data, int level)
        {
            if (t_zstdComp == null || t_zstdCompLevel != level)
            {
                t_zstdComp?.Dispose();
                t_zstdComp = new Compressor(level);
                t_zstdCompLevel = level;
            }
            return t_zstdComp.Wrap(data).ToArray();
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

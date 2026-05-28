using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Archive
{
    // Decoded view of a .rac file. Produced by RacReader, consumed by
    // RacRunner and RacInspector. Holds metadata only — section payloads
    // are loaded on demand through `ReadSection(index)` and the OS pages
    // in only the bytes that get touched (mmap path) or the bytes that
    // get requested (stream path).
    public sealed class RacArchive : IDisposable
    {
        public RacHeader Header { get; }
        public IReadOnlyList<RacSectionEntry> Sections { get; }
        public RacManifest Manifest { get; }

        public string SourcePath { get; }

        private readonly RacSource _source;
        private bool _disposed;
        // v1.1 (#7): lazily-decoded shared constant pool. First access
        // through `SharedConstPool` triggers the section lookup +
        // decode; subsequent calls return the cached instance.
        private SharedConstPool? _sharedConstPool;
        private bool _sharedConstPoolResolved;

        internal RacArchive(string sourcePath, RacSource source,
            RacHeader header, IReadOnlyList<RacSectionEntry> sections, RacManifest manifest)
        {
            SourcePath = sourcePath;
            _source = source;
            Header = header;
            Sections = sections;
            Manifest = manifest;
        }

        public byte[] ReadSection(int index)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
            if (index < 0 || index >= Sections.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var entry = Sections[index];
            return RacReader.ReadSectionPayload(_source, entry);
        }

        // Index of the Signature section in the directory, or -1 when
        // the archive is unsigned. Resolved lazily on first access.
        public int SignatureSectionIndex
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
                if (_sigIndexResolved) return _sigIndex;
                _sigIndex = -1;
                for (int i = 0; i < Sections.Count; i++)
                {
                    if (Sections[i].Kind == RacSectionKind.Signature)
                    {
                        if (_sigIndex >= 0)
                            throw new System.IO.InvalidDataException("rac: archive carries more than one Signature section");
                        _sigIndex = i;
                    }
                }
                _sigIndexResolved = true;
                return _sigIndex;
            }
        }

        private int _sigIndex = -1;
        private bool _sigIndexResolved;

        public bool IsSigned => SignatureSectionIndex >= 0;

        // Verify the embedded signature (when present) using the
        // supplied trust store. Behaviour matrix:
        //
        //   unsigned + no trust   -> Missing
        //   unsigned + trust      -> Missing
        //   signed, Embedded mode -> Valid (signature OK against the
        //                            embedded public key), Invalid
        //                            (signature math fails) or
        //                            Malformed (section bytes corrupt).
        //                            The caller decides whether to
        //                            ALSO require the embedded key to
        //                            be present in the trust store.
        //   signed, Fingerprint   -> looks up the public key in the
        //                            trust store via the section's
        //                            fingerprint. Missing trust store
        //                            or unknown fingerprint -> Untrusted.
        //   unknown algorithm     -> AlgorithmUnsupported.
        public RacSignatureVerifyResult VerifySignature(RacTrustStore? trustStore = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
            int idx = SignatureSectionIndex;
            if (idx < 0)
                return RacSignatureVerifyResult.Missing();

            RacSignatureSection sig;
            try
            {
                byte[] payload = ReadSection(idx);
                sig = RacSignatureSection.Decode(payload);
            }
            catch (System.IO.InvalidDataException ex)
            {
                return RacSignatureVerifyResult.Malformed(ex.Message);
            }

            if (!IsKnownAlgorithm(sig.Algorithm))
                return RacSignatureVerifyResult.AlgorithmUnsupported(sig);

            byte[]? pubKey;
            RacTrustedKey? trustedKey = null;
            if (sig.KeyMode == RacSignatureKeyMode.Embedded)
            {
                pubKey = sig.PublicKey;
                if (pubKey == null || pubKey.Length == 0)
                    return RacSignatureVerifyResult.Malformed("Signature section is Embedded but carries no public key bytes");
                // Defence-in-depth: the section also carries the
                // expected fingerprint. Make sure it matches the bytes
                // we just trusted — a mismatch usually means someone
                // edited the section by hand.
                byte[] computed = RacIntegrity.Hash(pubKey);
                if (!RacIntegrity.Equal(computed, sig.Fingerprint))
                    return RacSignatureVerifyResult.Malformed("Signature section fingerprint does not match embedded public key");
                if (trustStore != null)
                {
                    string fpHex = RacIntegrity.FormatHex(sig.Fingerprint);
                    trustStore.TryGet(fpHex, out trustedKey!);
                }
            }
            else if (sig.KeyMode == RacSignatureKeyMode.Fingerprint)
            {
                if (trustStore == null)
                    return RacSignatureVerifyResult.UntrustedKey(sig, "PKI mode but no trust store supplied");
                string fpHex = RacIntegrity.FormatHex(sig.Fingerprint);
                if (!trustStore.TryGet(fpHex, out var match))
                    return RacSignatureVerifyResult.UntrustedKey(sig, $"no trusted key with fingerprint {fpHex}");
                if (match.Algorithm != sig.Algorithm)
                    return RacSignatureVerifyResult.UntrustedKey(sig,
                        $"trusted key algorithm {match.Algorithm} differs from signature algorithm {sig.Algorithm}");
                trustedKey = match;
                pubKey = match.PublicKey;
            }
            else
            {
                return RacSignatureVerifyResult.Malformed($"Signature section uses unknown key mode {(int)sig.KeyMode}");
            }

            // Excluding the signature entry mirrors how the writer
            // built the canonical payload — guarantees signer/verifier
            // hash the same bytes.
            byte[] canonical = RacSigner.BuildCanonicalPayload(
                Header.FormatMajor, Header.FormatMinor,
                Header.RaRuntimeRequired, Header.RaRuntimeBuiltWith,
                Header.ManifestOffset,
                Sections, signatureSectionIndex: idx);

            bool ok = RacSigner.VerifyCanonical(sig.Algorithm, pubKey, canonical, sig.Signature);
            if (!ok)
                return RacSignatureVerifyResult.Invalid(sig, trustedKey, "signature math failed (tamper or wrong key)");
            return RacSignatureVerifyResult.Valid(sig, trustedKey);
        }

        private static bool IsKnownAlgorithm(RacSignatureAlgorithm algo) => algo switch
        {
            RacSignatureAlgorithm.Ed25519 => true,
            RacSignatureAlgorithm.RsaPss2048Sha256 => true,
            RacSignatureAlgorithm.RsaPss4096Sha256 => true,
            RacSignatureAlgorithm.EcdsaP256Sha256 => true,
            _ => false,
        };

        // Locate + decode the archive-level SharedConstPool, or return
        // null when the archive has none (every const inline). The
        // result is cached for the archive's lifetime so multi-module
        // bytecode loads pay the decode cost once.
        public SharedConstPool? SharedConstPool
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
                if (_sharedConstPoolResolved) return _sharedConstPool;
                for (int i = 0; i < Sections.Count; i++)
                {
                    if (Sections[i].Kind != RacSectionKind.SharedConstPool) continue;
                    byte[] payload = ReadSection(i);
                    _sharedConstPool = Archive.SharedConstPool.Decode(payload);
                    break;
                }
                _sharedConstPoolResolved = true;
                return _sharedConstPool;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}

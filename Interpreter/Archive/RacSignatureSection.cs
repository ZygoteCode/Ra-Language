using System;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // Algorithm tag used inside the Signature section (and shared by
    // RacSigner / RacVerifier). The numeric values are part of the wire
    // format — appending is allowed, renumbering is NOT.
    public enum RacSignatureAlgorithm : byte
    {
        Ed25519             = 0,
        // RSA-PSS-SHA256 with MGF1-SHA256 + salt-len = hash-len (32).
        RsaPss2048Sha256    = 1,
        RsaPss4096Sha256    = 2,
        // ECDSA over NIST P-256 with SHA-256 — modern compact
        // alternative to RSA, fully supported by the BCL on every
        // .NET target the runtime ships on.
        EcdsaP256Sha256     = 3,
    }

    // How the verifier resolves the public key.
    //
    //   Embedded:   the section carries the full SPKI / raw pub-key
    //               bytes. Loader can verify with no external state.
    //               Use for distribution to untrusted environments
    //               where the signature acts as a self-contained
    //               identity assertion (CDN, package mirror).
    //   Fingerprint: only the SHA-256 fingerprint of the public key is
    //               shipped. Verifier MUST resolve the matching key
    //               from a trust store (directory of `.pub` files
    //               keyed by fingerprint). Use for PKI deployments
    //               where keys rotate independently of payloads, or
    //               where the operator wants the verifier to fail
    //               closed on unknown signers.
    public enum RacSignatureKeyMode : byte
    {
        Embedded    = 0,
        Fingerprint = 1,
    }

    // Outcome of a verification attempt — surfaced to callers (and to
    // the `--verify-signature` CLI) so each failure mode can be
    // reported precisely.
    public enum RacSignatureStatus
    {
        // No Signature section in the archive. Strict mode rejects this.
        Missing,
        // Section parsed, signature math verified, key trusted.
        Valid,
        // Section parsed, signature math failed (tamper or wrong key).
        Invalid,
        // Section parsed but the algorithm tag is unknown to this
        // build (forward-compatibility — adding a new tag should not
        // crash old loaders, just be reported).
        AlgorithmUnsupported,
        // Fingerprint mode but no trust store entry matches.
        UntrustedKey,
        // Section bytes themselves are corrupt / out-of-range.
        Malformed,
    }

    // Outcome record returned from RacArchive.VerifySignature. Carries
    // enough context for the CLI inspector to render a human-readable
    // line and for RacRunner --strict-signature to refuse the archive
    // with a precise reason.
    public sealed class RacSignatureVerifyResult
    {
        public RacSignatureStatus Status { get; }
        public RacSignatureSection? Section { get; }
        public RacTrustedKey? TrustedKey { get; }
        public string? Detail { get; }

        public bool IsVerified => Status == RacSignatureStatus.Valid;
        public bool IsTrustedByStore => TrustedKey != null;

        private RacSignatureVerifyResult(RacSignatureStatus status, RacSignatureSection? section,
            RacTrustedKey? key, string? detail)
        {
            Status = status;
            Section = section;
            TrustedKey = key;
            Detail = detail;
        }

        public static RacSignatureVerifyResult Missing()
            => new(RacSignatureStatus.Missing, null, null, "archive carries no Signature section");
        public static RacSignatureVerifyResult Malformed(string detail)
            => new(RacSignatureStatus.Malformed, null, null, detail);
        public static RacSignatureVerifyResult AlgorithmUnsupported(RacSignatureSection s)
            => new(RacSignatureStatus.AlgorithmUnsupported, s, null,
                $"loader does not recognise algorithm tag {(int)s.Algorithm}");
        public static RacSignatureVerifyResult UntrustedKey(RacSignatureSection s, string detail)
            => new(RacSignatureStatus.UntrustedKey, s, null, detail);
        public static RacSignatureVerifyResult Invalid(RacSignatureSection s, RacTrustedKey? key, string detail)
            => new(RacSignatureStatus.Invalid, s, key, detail);
        public static RacSignatureVerifyResult Valid(RacSignatureSection s, RacTrustedKey? key)
            => new(RacSignatureStatus.Valid, s, key, null);
    }

    public sealed class RacSignatureSection
    {
        // "RSIG" little-endian — Ra archive SIGnature.
        public const uint MagicHead =
            (uint)'R' | ((uint)'S' << 8) | ((uint)'I' << 16) | ((uint)'G' << 24);

        public const ushort WireVersion = 1;

        public RacSignatureAlgorithm Algorithm { get; set; }
        public RacSignatureKeyMode KeyMode { get; set; }
        public string SignerId { get; set; } = "";
        // 32-byte SHA-256 of the canonical public-key encoding (raw 32
        // bytes for Ed25519, DER-encoded SubjectPublicKeyInfo for RSA /
        // ECDSA). Always populated regardless of KeyMode.
        public byte[] Fingerprint { get; set; } = Array.Empty<byte>();
        // The canonical public-key encoding itself. Populated only when
        // KeyMode == Embedded; empty otherwise (KeyMode == Fingerprint
        // means the verifier must source the key from a trust store).
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        // Algorithm-specific signature bytes.
        //   Ed25519     : 64 bytes  (R || S)
        //   RSA-PSS     : 256 / 512 bytes  (modulus-sized)
        //   ECDSA-P256  : DER-encoded SEQUENCE OF (r, s)  (~70 bytes)
        public byte[] Signature { get; set; } = Array.Empty<byte>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(MagicHead);
            w.WriteU16(WireVersion);
            w.WriteU16(0); // reserved
            w.WriteU8((byte)Algorithm);
            w.WriteU8((byte)KeyMode);
            w.WriteU16(0); // reserved2
            w.WriteString(SignerId ?? "");
            w.WriteI32(Fingerprint?.Length ?? 0);
            if (Fingerprint != null && Fingerprint.Length > 0) w.WriteBytes(Fingerprint);
            w.WriteI32(PublicKey?.Length ?? 0);
            if (PublicKey != null && PublicKey.Length > 0) w.WriteBytes(PublicKey);
            w.WriteI32(Signature?.Length ?? 0);
            if (Signature != null && Signature.Length > 0) w.WriteBytes(Signature);
            return ms.ToArray();
        }

        public static RacSignatureSection Decode(ReadOnlySpan<byte> payload)
        {
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            uint magic = r.ReadU32();
            if (magic != MagicHead)
                throw new InvalidDataException("rac: Signature magic mismatch");
            ushort version = r.ReadU16();
            if (version != WireVersion)
                throw new InvalidDataException($"rac: Signature wire version {version} unsupported");
            ushort reserved = r.ReadU16();
            if (reserved != 0) throw new InvalidDataException("rac: Signature reserved must be zero");
            var algo = (RacSignatureAlgorithm)r.ReadU8();
            var mode = (RacSignatureKeyMode)r.ReadU8();
            ushort reserved2 = r.ReadU16();
            if (reserved2 != 0) throw new InvalidDataException("rac: Signature reserved2 must be zero");
            var sig = new RacSignatureSection
            {
                Algorithm = algo,
                KeyMode = mode,
                SignerId = r.ReadString() ?? "",
            };
            int fpLen = r.ReadI32();
            if (fpLen < 0 || fpLen > 1024)
                throw new InvalidDataException($"rac: bogus fingerprint length {fpLen}");
            sig.Fingerprint = fpLen > 0 ? r.ReadBytes(fpLen) : Array.Empty<byte>();
            int pkLen = r.ReadI32();
            if (pkLen < 0 || pkLen > 8192)
                throw new InvalidDataException($"rac: bogus public-key length {pkLen}");
            sig.PublicKey = pkLen > 0 ? r.ReadBytes(pkLen) : Array.Empty<byte>();
            int signLen = r.ReadI32();
            if (signLen < 0 || signLen > 8192)
                throw new InvalidDataException($"rac: bogus signature length {signLen}");
            sig.Signature = signLen > 0 ? r.ReadBytes(signLen) : Array.Empty<byte>();
            return sig;
        }
    }
}

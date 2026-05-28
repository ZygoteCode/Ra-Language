using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace RaLanguage.Interpreter.Archive
{
    // Build + verify the canonical signed-payload byte sequence and run
    // the algorithm-specific sign / verify primitives. The "canonical
    // signed payload" intentionally covers the full archive
    // identity — file header version, manifest offset, every section
    // entry except the signature section itself — so flipping any
    // section's content, kind, size or offset invalidates the
    // signature.
    //
    // The Signature section's OWN entry is excluded from the signed
    // payload to break the chicken-and-egg dependency (the signature
    // bytes change the section's hash, which changes the directory
    // entry, which changes the directory hash, etc.). The verifier
    // mirrors the exclusion when rebuilding the payload.
    public static class RacSigner
    {
        public const uint CanonicalMagic =
            (uint)'R' | ((uint)'A' << 8) | ((uint)'C' << 16) | ((uint)'S' << 24); // "RACS"

        // Wire-version of the canonical signed-payload encoding. Bump
        // on any breaking change to BuildCanonicalPayload below.
        public const ushort CanonicalVersion = 1;

        // Build the canonical signed-payload bytes for an archive whose
        // directory consists of `entries` (in directory order), with
        // the file header carrying the given format major/minor,
        // runtime-version requirements, and manifest offset.
        // `signatureSectionIndex` is the index of the entry that holds
        // (will hold) the Signature section payload — pass -1 when the
        // signature section has not been allocated yet (i.e. during
        // signing); the writer fills it in when it knows the index.
        // The signed payload excludes whichever entry sits at
        // signatureSectionIndex.
        //
        // Wire layout (all little-endian, no padding):
        //   u32  magic "RACS"
        //   u16  canonical version (1)
        //   u16  reserved (0)
        //   u16  formatMajor
        //   u16  formatMinor
        //   u32  raRuntimeRequired   (semver-packed)
        //   u32  raRuntimeBuiltWith  (semver-packed)
        //   u64  manifestOffset
        //   i32  signedSectionCount  (= entries.Count - {1 if sig present else 0})
        //   per signed entry (in directory order, sig entry skipped):
        //     u32 Kind
        //     u32 Flags
        //     u64 Offset
        //     u64 StoredSize
        //     u64 UncompressedSize
        //     32 bytes Hash
        public static byte[] BuildCanonicalPayload(
            ushort formatMajor, ushort formatMinor,
            uint raRuntimeRequired, uint raRuntimeBuiltWith,
            ulong manifestOffset,
            IReadOnlyList<RacSectionEntry> entries, int signatureSectionIndex)
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(CanonicalMagic);
            w.WriteU16(CanonicalVersion);
            w.WriteU16(0); // reserved
            w.WriteU16(formatMajor);
            w.WriteU16(formatMinor);
            w.WriteU32(raRuntimeRequired);
            w.WriteU32(raRuntimeBuiltWith);
            w.WriteU64(manifestOffset);
            int signedCount = 0;
            for (int i = 0; i < entries.Count; i++)
                if (i != signatureSectionIndex) signedCount++;
            w.WriteI32(signedCount);
            for (int i = 0; i < entries.Count; i++)
            {
                if (i == signatureSectionIndex) continue;
                var e = entries[i];
                w.WriteU32((uint)e.Kind);
                w.WriteU32((uint)e.Flags);
                w.WriteU64(e.Offset);
                w.WriteU64(e.StoredSize);
                w.WriteU64(e.UncompressedSize);
                w.WriteBytes(e.Hash);
            }
            return ms.ToArray();
        }

        // Run the algorithm's sign primitive over the canonical payload.
        // Returns the algorithm-specific signature blob.
        public static byte[] SignCanonical(RacKeyPair key, byte[] canonicalPayload)
        {
            switch (key.Algorithm)
            {
                case RacSignatureAlgorithm.Ed25519:
                    return Crypto.Ed25519.Sign(canonicalPayload, key.PrivateKey, key.PublicKey);
                case RacSignatureAlgorithm.RsaPss2048Sha256:
                case RacSignatureAlgorithm.RsaPss4096Sha256:
                {
                    using var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(key.PrivateKey, out _);
                    return rsa.SignData(canonicalPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                }
                case RacSignatureAlgorithm.EcdsaP256Sha256:
                {
                    using var ec = ECDsa.Create();
                    ec.ImportPkcs8PrivateKey(key.PrivateKey, out _);
                    // DER format keeps the signature self-describing and
                    // tooling-friendly (`openssl` round-trips DER ECDSA
                    // signatures without further transformation).
                    return ec.SignData(canonicalPayload, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence);
                }
                default:
                    throw new InvalidOperationException($"sign: unsupported algorithm {key.Algorithm}");
            }
        }

        // Verify a signature against the canonical payload using the
        // supplied public-key bytes (in the algorithm's canonical
        // encoding — see RacKeyStore comments).
        public static bool VerifyCanonical(RacSignatureAlgorithm algo, byte[] publicKey,
            byte[] canonicalPayload, byte[] signature)
        {
            try
            {
                switch (algo)
                {
                    case RacSignatureAlgorithm.Ed25519:
                        return Crypto.Ed25519.Verify(canonicalPayload, signature, publicKey);
                    case RacSignatureAlgorithm.RsaPss2048Sha256:
                    case RacSignatureAlgorithm.RsaPss4096Sha256:
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return rsa.VerifyData(canonicalPayload, signature,
                            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                    }
                    case RacSignatureAlgorithm.EcdsaP256Sha256:
                    {
                        using var ec = ECDsa.Create();
                        ec.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return ec.VerifyData(canonicalPayload, signature,
                            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                    }
                    default:
                        return false;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        // Convenience name used by inspect / CLI output.
        public static string DescribeAlgorithm(RacSignatureAlgorithm algo) => algo switch
        {
            RacSignatureAlgorithm.Ed25519 => "Ed25519",
            RacSignatureAlgorithm.RsaPss2048Sha256 => "RSA-PSS-2048-SHA256",
            RacSignatureAlgorithm.RsaPss4096Sha256 => "RSA-PSS-4096-SHA256",
            RacSignatureAlgorithm.EcdsaP256Sha256 => "ECDsa-P256-SHA256",
            _ => $"<unknown:{(int)algo}>",
        };
    }
}

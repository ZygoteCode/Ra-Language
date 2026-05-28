using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RaLanguage.Interpreter.Archive
{
    // In-memory pair (algorithm + key bytes). Concrete encoding:
    //   Ed25519:        raw 32 bytes (private = seed, public = compressed).
    //   RSA / ECDsa:    PKCS#8 PrivateKeyInfo (private) / SubjectPublicKeyInfo (public)
    //                   DER blobs — the BCL-canonical form.
    public sealed class RacKeyPair
    {
        public RacSignatureAlgorithm Algorithm { get; }
        public byte[] PrivateKey { get; }
        public byte[] PublicKey { get; }

        public RacKeyPair(RacSignatureAlgorithm algo, byte[] priv, byte[] pub)
        {
            Algorithm = algo;
            PrivateKey = priv ?? throw new ArgumentNullException(nameof(priv));
            PublicKey = pub ?? throw new ArgumentNullException(nameof(pub));
        }

        public byte[] Fingerprint() => RacIntegrity.Hash(PublicKey);
        public string FingerprintHex() => RacIntegrity.FormatHex(Fingerprint());
    }

    // PEM read/write + fingerprint-based trust store. PEM is the
    // interoperability format every signing tool, HSM, CI store and
    // operator-facing config file already speaks; sticking to it means
    // a `.priv` produced by `openssl` (for RSA / ECDsa) or by this
    // tool (for Ed25519) just works.
    //
    // PEM band labels:
    //   "RA ED25519 PRIVATE KEY"  raw 32-byte seed
    //   "RA ED25519 PUBLIC KEY"   raw 32-byte compressed point
    //   "PRIVATE KEY"             PKCS#8 PrivateKeyInfo (RSA / ECDsa)
    //   "PUBLIC KEY"              SubjectPublicKeyInfo (RSA / ECDsa)
    //
    // The Ed25519 band labels are intentionally Ra-prefixed because
    // PKCS#8 v2 support for Ed25519 is not uniformly available across
    // .NET targets, and we don't want a `.priv` file silently picked
    // up as a different algorithm.
    public static class RacKeyStore
    {
        // === Generation ===

        public static RacKeyPair Generate(RacSignatureAlgorithm algo)
        {
            switch (algo)
            {
                case RacSignatureAlgorithm.Ed25519:
                {
                    byte[] priv = Crypto.Ed25519.GeneratePrivateKey();
                    byte[] pub = Crypto.Ed25519.GetPublicKey(priv);
                    return new RacKeyPair(algo, priv, pub);
                }
                case RacSignatureAlgorithm.RsaPss2048Sha256:
                    return GenerateRsa(2048);
                case RacSignatureAlgorithm.RsaPss4096Sha256:
                    return GenerateRsa(4096);
                case RacSignatureAlgorithm.EcdsaP256Sha256:
                {
                    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var priv = ec.ExportPkcs8PrivateKey();
                    var pub = ec.ExportSubjectPublicKeyInfo();
                    return new RacKeyPair(algo, priv, pub);
                }
                default:
                    throw new ArgumentException($"unknown algorithm {algo}", nameof(algo));
            }
        }

        private static RacKeyPair GenerateRsa(int bits)
        {
            using var rsa = RSA.Create(bits);
            var priv = rsa.ExportPkcs8PrivateKey();
            var pub = rsa.ExportSubjectPublicKeyInfo();
            var algo = bits == 2048 ? RacSignatureAlgorithm.RsaPss2048Sha256 : RacSignatureAlgorithm.RsaPss4096Sha256;
            return new RacKeyPair(algo, priv, pub);
        }

        // === PEM I/O ===

        public static void WriteKeyPair(RacKeyPair pair, string privPath, string pubPath)
        {
            File.WriteAllText(privPath, EncodePem(PrivateLabel(pair.Algorithm), pair.PrivateKey));
            File.WriteAllText(pubPath, EncodePem(PublicLabel(pair.Algorithm), pair.PublicKey));
        }

        public static RacKeyPair LoadPrivateKey(string path)
        {
            string text = File.ReadAllText(path);
            var (label, body) = DecodePem(text, path);
            return label switch
            {
                "RA ED25519 PRIVATE KEY" => new RacKeyPair(
                    RacSignatureAlgorithm.Ed25519, body, Crypto.Ed25519.GetPublicKey(body)),
                "PRIVATE KEY" => RecoverPrivateBcl(body, path),
                _ => throw new InvalidDataException(
                    $"{path}: PEM label '{label}' is not a recognised Ra private-key kind"),
            };
        }

        public static (RacSignatureAlgorithm Algo, byte[] PublicKey) LoadPublicKey(string path)
        {
            string text = File.ReadAllText(path);
            var (label, body) = DecodePem(text, path);
            return label switch
            {
                "RA ED25519 PUBLIC KEY" => (RacSignatureAlgorithm.Ed25519, body),
                "PUBLIC KEY" => RecoverPublicBcl(body, path),
                _ => throw new InvalidDataException(
                    $"{path}: PEM label '{label}' is not a recognised Ra public-key kind"),
            };
        }

        private static RacKeyPair RecoverPrivateBcl(byte[] pkcs8, string path)
        {
            // Try RSA first, then ECDsa. We can't use a single
            // SubjectPublicKeyInfo discriminator because PKCS#8 keys
            // carry the algorithm OID inside the DER, which the BCL
            // surfaces as a successful Import on the right class only.
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                var pub = rsa.ExportSubjectPublicKeyInfo();
                int bits = rsa.KeySize;
                var algo = bits switch
                {
                    2048 => RacSignatureAlgorithm.RsaPss2048Sha256,
                    4096 => RacSignatureAlgorithm.RsaPss4096Sha256,
                    _ => throw new InvalidDataException(
                        $"{path}: unsupported RSA key size {bits}; supported: 2048, 4096"),
                };
                return new RacKeyPair(algo, pkcs8, pub);
            }
            catch (CryptographicException) { }
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(pkcs8, out _);
                var p = ec.ExportParameters(includePrivateParameters: false);
                if (p.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                    throw new InvalidDataException(
                        $"{path}: ECDsa key uses curve '{p.Curve.Oid?.FriendlyName ?? "<unknown>"}'; only P-256 is supported");
                var pub = ec.ExportSubjectPublicKeyInfo();
                return new RacKeyPair(RacSignatureAlgorithm.EcdsaP256Sha256, pkcs8, pub);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"{path}: PKCS#8 private key is neither RSA nor ECDsa-P256: {ex.Message}");
            }
        }

        private static (RacSignatureAlgorithm Algo, byte[] PublicKey) RecoverPublicBcl(byte[] spki, string path)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(spki, out _);
                int bits = rsa.KeySize;
                var algo = bits switch
                {
                    2048 => RacSignatureAlgorithm.RsaPss2048Sha256,
                    4096 => RacSignatureAlgorithm.RsaPss4096Sha256,
                    _ => throw new InvalidDataException(
                        $"{path}: unsupported RSA key size {bits}; supported: 2048, 4096"),
                };
                return (algo, spki);
            }
            catch (CryptographicException) { }
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(spki, out _);
                var p = ec.ExportParameters(includePrivateParameters: false);
                if (p.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                    throw new InvalidDataException(
                        $"{path}: ECDsa key uses curve '{p.Curve.Oid?.FriendlyName ?? "<unknown>"}'; only P-256 is supported");
                return (RacSignatureAlgorithm.EcdsaP256Sha256, spki);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"{path}: SPKI public key is neither RSA nor ECDsa-P256: {ex.Message}");
            }
        }

        private static string PrivateLabel(RacSignatureAlgorithm algo) => algo switch
        {
            RacSignatureAlgorithm.Ed25519 => "RA ED25519 PRIVATE KEY",
            _ => "PRIVATE KEY",
        };

        private static string PublicLabel(RacSignatureAlgorithm algo) => algo switch
        {
            RacSignatureAlgorithm.Ed25519 => "RA ED25519 PUBLIC KEY",
            _ => "PUBLIC KEY",
        };

        private static string EncodePem(string label, byte[] body)
        {
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            string b64 = Convert.ToBase64String(body);
            for (int i = 0; i < b64.Length; i += 64)
            {
                sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
            }
            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }

        private static (string Label, byte[] Body) DecodePem(string text, string path)
        {
            int begin = text.IndexOf("-----BEGIN ", StringComparison.Ordinal);
            if (begin < 0) throw new InvalidDataException($"{path}: missing PEM BEGIN header");
            int beginEnd = text.IndexOf("-----", begin + 11, StringComparison.Ordinal);
            if (beginEnd < 0) throw new InvalidDataException($"{path}: malformed PEM BEGIN header");
            string label = text.Substring(begin + 11, beginEnd - (begin + 11));
            int bodyStart = beginEnd + 5;
            int end = text.IndexOf("-----END ", bodyStart, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException($"{path}: missing PEM END marker");
            string b64 = text.Substring(bodyStart, end - bodyStart)
                .Replace("\r", "").Replace("\n", "").Replace(" ", "");
            try
            {
                return (label, Convert.FromBase64String(b64));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"{path}: PEM body is not valid base64: {ex.Message}");
            }
        }

        // === Trust store ===

        // Loads every *.pub file in `directory` and indexes them by
        // fingerprint hex (lower-case, no separators). Files whose
        // PEM contents cannot be parsed are skipped with a console
        // warning rather than failing the whole load — operators
        // routinely mix valid and stale `.pub` files in a trust
        // directory and a single bad file should not lock everyone
        // out.
        public static RacTrustStore LoadTrustStore(string directory)
        {
            var store = new RacTrustStore();
            if (!Directory.Exists(directory)) return store;
            foreach (var file in Directory.EnumerateFiles(directory, "*.pub"))
            {
                try
                {
                    var (algo, pub) = LoadPublicKey(file);
                    string fp = RacIntegrity.FormatHex(RacIntegrity.Hash(pub));
                    store.Add(fp, algo, pub, file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Ra Language] trust-store: skipping '{file}' ({ex.Message})");
                }
            }
            return store;
        }
    }

    public sealed class RacTrustStore
    {
        private readonly Dictionary<string, RacTrustedKey> _byFingerprint =
            new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byFingerprint.Count;

        public void Add(string fingerprintHex, RacSignatureAlgorithm algo, byte[] pubKey, string sourcePath)
        {
            _byFingerprint[fingerprintHex] = new RacTrustedKey(fingerprintHex, algo, pubKey, sourcePath);
        }

        public bool TryGet(string fingerprintHex, out RacTrustedKey key)
        {
            return _byFingerprint.TryGetValue(fingerprintHex, out key!);
        }

        public IEnumerable<RacTrustedKey> Keys => _byFingerprint.Values;
    }

    public sealed class RacTrustedKey
    {
        public string FingerprintHex { get; }
        public RacSignatureAlgorithm Algorithm { get; }
        public byte[] PublicKey { get; }
        public string SourcePath { get; }

        public RacTrustedKey(string fp, RacSignatureAlgorithm algo, byte[] pub, string src)
        {
            FingerprintHex = fp;
            Algorithm = algo;
            PublicKey = pub;
            SourcePath = src;
        }
    }
}

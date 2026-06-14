using System;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Archive.Crypto;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.crypto — hashing / digest / checksum helpers.
    //
    // Deterministic, cross-platform, AOT-safe: the one-shot static HashData
    // APIs and HMAC are fully supported under NativeAOT (no reflection, no
    // dynamic providers). Inputs are UTF-8 text; outputs are lowercase hex.
    // SHA-1 / MD5 are provided for interop/checksum use, NOT for security.
    public static class CryptoBuiltins
    {
        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        private static byte[] FromB64(string s, string what)
        {
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { throw new RuntimeBuiltinException($"invalid base64 {what}"); }
        }

        private static MapValue KeyPairMap(byte[] priv, byte[] pub)
        {
            return new MapValue(new List<(RuntimeValue, RuntimeValue)>
            {
                (new StringValue("private"), new StringValue(Convert.ToBase64String(priv))),
                (new StringValue("public"), new StringValue(Convert.ToBase64String(pub))),
            });
        }

        public static void Register()
        {
            BuiltInRegistry.Register("sha256_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("sha256_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(AsString(args[0]))))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("sha1_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("sha1_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Hex(SHA1.HashData(Encoding.UTF8.GetBytes(AsString(args[0]))))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("sha512_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("sha512_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Hex(SHA512.HashData(Encoding.UTF8.GetBytes(AsString(args[0]))))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("md5_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("md5_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Hex(MD5.HashData(Encoding.UTF8.GetBytes(AsString(args[0]))))), ctx, p1, p2);
            });

            // hmac_sha256_hex(key, message) -> lowercase hex MAC
            BuiltInRegistry.Register("hmac_sha256_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("hmac_sha256_hex", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AsString(args[0])), Encoding.UTF8.GetBytes(AsString(args[1])));
                return Ok(new StringValue(Hex(mac)), ctx, p1, p2);
            });

            BuiltInRegistry.Register("sha384_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("sha384_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Hex(SHA384.HashData(Encoding.UTF8.GetBytes(AsString(args[0]))))), ctx, p1, p2);
            });

            // hmac_sha512_hex(key, message) / hmac_sha1_hex(key, message)
            BuiltInRegistry.Register("hmac_sha512_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("hmac_sha512_hex", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] mac = HMACSHA512.HashData(Encoding.UTF8.GetBytes(AsString(args[0])), Encoding.UTF8.GetBytes(AsString(args[1])));
                return Ok(new StringValue(Hex(mac)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("hmac_sha1_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("hmac_sha1_hex", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] mac = HMACSHA1.HashData(Encoding.UTF8.GetBytes(AsString(args[0])), Encoding.UTF8.GetBytes(AsString(args[1])));
                return Ok(new StringValue(Hex(mac)), ctx, p1, p2);
            });

            // pbkdf2_sha256_hex(password, salt, iterations, key_len) -> derived
            // key as lowercase hex. The standard, AOT-safe password-stretching
            // KDF (HMAC-SHA256). Use a high iteration count and a random salt.
            BuiltInRegistry.Register("pbkdf2_sha256_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("pbkdf2_sha256_hex", args, 4, ctx, p1, p2, out var err)) return err;
                int iterations = AsInt(args[2]);
                int keyLen = AsInt(args[3]);
                if (iterations < 1) return Fail(ctx, p1, p2, "pbkdf2_sha256_hex: iterations must be >= 1");
                if (keyLen < 1 || keyLen > 1024) return Fail(ctx, p1, p2, "pbkdf2_sha256_hex: key_len must be in [1, 1024]");
                byte[] dk = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(AsString(args[0])),
                    Encoding.UTF8.GetBytes(AsString(args[1])),
                    iterations, HashAlgorithmName.SHA256, keyLen);
                return Ok(new StringValue(Hex(dk)), ctx, p1, p2);
            });

            // ---- Asymmetric signatures (sign / verify / keypair) -----------
            // Keys and signatures are base64 strings; messages are UTF-8 text.
            // All three back ends are AOT-safe (Ed25519 is a vendored managed
            // RFC 8032 impl; RSA-PSS and ECDSA-P256 are BCL one-shot APIs).
            // A keypair returns a map { "private": b64, "public": b64 }.

            // Ed25519 — 32-byte keys, 64-byte signatures (RFC 8032).
            BuiltInRegistry.Register("ed25519_keypair", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ed25519_keypair", args, 0, ctx, p1, p2, out var err)) return err;
                byte[] priv = Ed25519.GeneratePrivateKey();
                byte[] pub = Ed25519.GetPublicKey(priv);
                return Ok(KeyPairMap(priv, pub), ctx, p1, p2);
            });
            BuiltInRegistry.Register("ed25519_sign", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ed25519_sign", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] priv = FromB64(AsString(args[1]), "private key");
                if (priv.Length != 32) return Fail(ctx, p1, p2, "ed25519_sign: private key must be 32 bytes");
                byte[] pub = Ed25519.GetPublicKey(priv);
                byte[] sig = Ed25519.Sign(Encoding.UTF8.GetBytes(AsString(args[0])), priv, pub);
                return Ok(new StringValue(Convert.ToBase64String(sig)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("ed25519_verify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ed25519_verify", args, 3, ctx, p1, p2, out var err)) return err;
                byte[] sig = FromB64(AsString(args[1]), "signature");
                byte[] pub = FromB64(AsString(args[2]), "public key");
                if (sig.Length != 64 || pub.Length != 32) return Ok(MakeBool(false), ctx, p1, p2);
                bool okv = Ed25519.Verify(Encoding.UTF8.GetBytes(AsString(args[0])), sig, pub);
                return Ok(MakeBool(okv), ctx, p1, p2);
            });

            // RSA-PSS (SHA-256). rsa_keypair([bits]) — default 2048.
            BuiltInRegistry.Register("rsa_keypair", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("rsa_keypair", args, 0, 1, ctx, p1, p2, out var err)) return err;
                int bits = args.Count == 1 ? AsInt(args[0]) : 2048;
                if (bits != 2048 && bits != 3072 && bits != 4096) return Fail(ctx, p1, p2, "rsa_keypair: bits must be 2048, 3072 or 4096");
                using var rsa = RSA.Create(bits);
                return Ok(KeyPairMap(rsa.ExportPkcs8PrivateKey(), rsa.ExportSubjectPublicKeyInfo()), ctx, p1, p2);
            });
            BuiltInRegistry.Register("rsa_sign", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("rsa_sign", args, 2, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(FromB64(AsString(args[1]), "private key"), out _);
                    byte[] sig = rsa.SignData(Encoding.UTF8.GetBytes(AsString(args[0])), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                    return Ok(new StringValue(Convert.ToBase64String(sig)), ctx, p1, p2);
                }
                catch (CryptographicException ce) { return Fail(ctx, p1, p2, "rsa_sign: " + ce.Message); }
            });
            BuiltInRegistry.Register("rsa_verify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("rsa_verify", args, 3, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportSubjectPublicKeyInfo(FromB64(AsString(args[2]), "public key"), out _);
                    bool okv = rsa.VerifyData(Encoding.UTF8.GetBytes(AsString(args[0])), FromB64(AsString(args[1]), "signature"), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                    return Ok(MakeBool(okv), ctx, p1, p2);
                }
                catch (CryptographicException) { return Ok(MakeBool(false), ctx, p1, p2); }
            });

            // ECDSA on NIST P-256 (SHA-256).
            BuiltInRegistry.Register("ecdsa_keypair", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ecdsa_keypair", args, 0, ctx, p1, p2, out var err)) return err;
                using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                return Ok(KeyPairMap(ec.ExportPkcs8PrivateKey(), ec.ExportSubjectPublicKeyInfo()), ctx, p1, p2);
            });
            BuiltInRegistry.Register("ecdsa_sign", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ecdsa_sign", args, 2, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var ec = ECDsa.Create();
                    ec.ImportPkcs8PrivateKey(FromB64(AsString(args[1]), "private key"), out _);
                    byte[] sig = ec.SignData(Encoding.UTF8.GetBytes(AsString(args[0])), HashAlgorithmName.SHA256);
                    return Ok(new StringValue(Convert.ToBase64String(sig)), ctx, p1, p2);
                }
                catch (CryptographicException ce) { return Fail(ctx, p1, p2, "ecdsa_sign: " + ce.Message); }
            });
            BuiltInRegistry.Register("ecdsa_verify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("ecdsa_verify", args, 3, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var ec = ECDsa.Create();
                    ec.ImportSubjectPublicKeyInfo(FromB64(AsString(args[2]), "public key"), out _);
                    bool okv = ec.VerifyData(Encoding.UTF8.GetBytes(AsString(args[0])), FromB64(AsString(args[1]), "signature"), HashAlgorithmName.SHA256);
                    return Ok(MakeBool(okv), ctx, p1, p2);
                }
                catch (CryptographicException) { return Ok(MakeBool(false), ctx, p1, p2); }
            });

            // crc32(text) -> unsigned 32-bit CRC (IEEE 802.3 polynomial), as a number
            BuiltInRegistry.Register("crc32", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("crc32", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(NumberFor((long)Crc32(Encoding.UTF8.GetBytes(AsString(args[0])))), ctx, p1, p2);
            });
        }

        // Standard reflected CRC-32 (poly 0xEDB88320). Small table built once.
        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

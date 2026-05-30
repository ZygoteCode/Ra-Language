using System;
using System.Text;
using System.Security.Cryptography;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
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

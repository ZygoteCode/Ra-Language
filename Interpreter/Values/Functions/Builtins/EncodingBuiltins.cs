using System;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.encoding — text/byte encoding helpers.
    //
    // All operate on UTF-8 text (Ra has no distinct byte type), are
    // deterministic, cross-platform, and AOT-safe — they touch only the BCL
    // Convert / Uri surface, with no reflection.
    public static class EncodingBuiltins
    {
        public static void Register()
        {
            // base64_encode(text) -> Base64 of the UTF-8 bytes
            BuiltInRegistry.Register("base64_encode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base64_encode", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToBase64String(Encoding.UTF8.GetBytes(AsString(args[0])))), ctx, p1, p2);
            });

            // base64_decode(base64) -> UTF-8 text
            BuiltInRegistry.Register("base64_decode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base64_decode", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new StringValue(Encoding.UTF8.GetString(Convert.FromBase64String(AsString(args[0])))), ctx, p1, p2); }
                catch (FormatException) { return Fail(ctx, p1, p2, "base64_decode: input is not valid Base64"); }
            });

            // hex_encode(text) -> lowercase hex of the UTF-8 bytes
            BuiltInRegistry.Register("hex_encode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("hex_encode", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToHexString(Encoding.UTF8.GetBytes(AsString(args[0]))).ToLowerInvariant()), ctx, p1, p2);
            });

            // hex_decode(hex) -> UTF-8 text
            BuiltInRegistry.Register("hex_decode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("hex_decode", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new StringValue(Encoding.UTF8.GetString(Convert.FromHexString(AsString(args[0])))), ctx, p1, p2); }
                catch (FormatException) { return Fail(ctx, p1, p2, "hex_decode: input is not valid hexadecimal"); }
            });

            // url_encode(text) -> RFC 3986 percent-encoding (data-component safe)
            BuiltInRegistry.Register("url_encode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("url_encode", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Uri.EscapeDataString(AsString(args[0]))), ctx, p1, p2);
            });

            // url_decode(text) -> percent-decoded text
            BuiltInRegistry.Register("url_decode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("url_decode", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Uri.UnescapeDataString(AsString(args[0]))), ctx, p1, p2);
            });

            // base64url_encode(text) -> URL-safe Base64 (RFC 4648 §5): '+'/'/'
            // become '-'/'_' and trailing '=' padding is dropped.
            BuiltInRegistry.Register("base64url_encode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base64url_encode", args, 1, ctx, p1, p2, out var err)) return err;
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(AsString(args[0])));
                return Ok(new StringValue(b64.TrimEnd('=').Replace('+', '-').Replace('/', '_')), ctx, p1, p2);
            });

            // base64url_decode(text) -> UTF-8 text (padding optional)
            BuiltInRegistry.Register("base64url_decode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base64url_decode", args, 1, ctx, p1, p2, out var err)) return err;
                string s = AsString(args[0]).Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
                try { return Ok(new StringValue(Encoding.UTF8.GetString(Convert.FromBase64String(s))), ctx, p1, p2); }
                catch (FormatException) { return Fail(ctx, p1, p2, "base64url_decode: input is not valid URL-safe Base64"); }
            });

            // base32_encode(text) -> RFC 4648 Base32 (alphabet A-Z2-7, '=' pad)
            BuiltInRegistry.Register("base32_encode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base32_encode", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Base32Encode(Encoding.UTF8.GetBytes(AsString(args[0])))), ctx, p1, p2);
            });

            // base32_decode(text) -> UTF-8 text (case-insensitive, padding optional)
            BuiltInRegistry.Register("base32_decode", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("base32_decode", args, 1, ctx, p1, p2, out var err)) return err;
                if (!Base32Decode(AsString(args[0]), out var bytes))
                    return Fail(ctx, p1, p2, "base32_decode: input is not valid Base32");
                return Ok(new StringValue(Encoding.UTF8.GetString(bytes)), ctx, p1, p2);
            });
        }

        // RFC 4648 Base32 — hand-rolled (the BCL ships no Base32). Deterministic
        // and AOT-safe: pure arithmetic over a fixed alphabet.
        private const string B32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        private static string Base32Encode(byte[] data)
        {
            if (data.Length == 0) return "";
            var sb = new StringBuilder((data.Length + 4) / 5 * 8);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(B32Alphabet[(buffer >> bits) & 31]);
                }
            }
            if (bits > 0) sb.Append(B32Alphabet[(buffer << (5 - bits)) & 31]);
            while (sb.Length % 8 != 0) sb.Append('=');
            return sb.ToString();
        }

        private static bool Base32Decode(string s, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var outBytes = new System.Collections.Generic.List<byte>(s.Length * 5 / 8 + 1);
            int buffer = 0, bits = 0;
            foreach (char rawc in s)
            {
                char c = char.ToUpperInvariant(rawc);
                if (c == '=' || c == ' ' || c == '\n' || c == '\r' || c == '\t') continue;
                int idx = B32Alphabet.IndexOf(c);
                if (idx < 0) return false;
                buffer = (buffer << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    outBytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            bytes = outBytes.ToArray();
            return true;
        }
    }
}

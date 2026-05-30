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
        }
    }
}

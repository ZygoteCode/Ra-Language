using System;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.net — URI / URL parsing and composition.
    //
    // Pure, deterministic, cross-platform, AOT-safe (System.Uri only). Live
    // network I/O (HTTP/TCP/DNS) is intentionally NOT here yet — it is not
    // unit-testable in a sandboxed/offline build and carries AOT/trim and
    // cross-platform caveats; see RA_STDLIB_PRELUDE_DESIGN.md "future work".
    public static class NetBuiltins
    {
        private static bool TryAbs(string s, out Uri uri) => Uri.TryCreate(s, UriKind.Absolute, out uri!);

        public static void Register()
        {
            // uri_is_valid(s) -> true when s is a well-formed absolute URI
            BuiltInRegistry.Register("uri_is_valid", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("uri_is_valid", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(TryAbs(AsString(args[0]), out _)), ctx, p1, p2);
            });

            // uri_parse(s) -> map { scheme, host, port, path, query, fragment, userinfo }
            BuiltInRegistry.Register("uri_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("uri_parse", args, 1, ctx, p1, p2, out var err)) return err;
                if (!TryAbs(AsString(args[0]), out var u)) return Fail(ctx, p1, p2, "uri_parse: not a valid absolute URI");
                var pairs = new List<(RuntimeValue, RuntimeValue)>
                {
                    (new StringValue("scheme"),   new StringValue(u.Scheme)),
                    (new StringValue("host"),     new StringValue(u.Host)),
                    (new StringValue("port"),     NumberFor((long)u.Port)),
                    (new StringValue("path"),     new StringValue(u.AbsolutePath)),
                    (new StringValue("query"),    new StringValue(u.Query)),
                    (new StringValue("fragment"), new StringValue(u.Fragment)),
                    (new StringValue("userinfo"), new StringValue(u.UserInfo)),
                };
                return Ok(new MapValue(pairs), ctx, p1, p2);
            });

            RegisterPart("uri_scheme",   u => u.Scheme);
            RegisterPart("uri_host",     u => u.Host);
            RegisterPart("uri_path",     u => u.AbsolutePath);
            RegisterPart("uri_query",    u => u.Query);
            RegisterPart("uri_fragment", u => u.Fragment);

            // uri_port(s) -> the (possibly default) port as a number
            BuiltInRegistry.Register("uri_port", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("uri_port", args, 1, ctx, p1, p2, out var err)) return err;
                if (!TryAbs(AsString(args[0]), out var u)) return Fail(ctx, p1, p2, "uri_port: not a valid absolute URI");
                return Ok(NumberFor((long)u.Port), ctx, p1, p2);
            });

            // uri_join(base, relative) -> resolved absolute URI string
            BuiltInRegistry.Register("uri_join", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("uri_join", args, 2, ctx, p1, p2, out var err)) return err;
                if (!TryAbs(AsString(args[0]), out var b)) return Fail(ctx, p1, p2, "uri_join: base is not a valid absolute URI");
                if (!Uri.TryCreate(b, AsString(args[1]), out var joined)) return Fail(ctx, p1, p2, "uri_join: cannot resolve relative reference");
                return Ok(new StringValue(joined.ToString()), ctx, p1, p2);
            });
        }

        private static void RegisterPart(string name, Func<Uri, string> sel)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs(name, args, 1, ctx, p1, p2, out var err)) return err;
                if (!TryAbs(AsString(args[0]), out var u)) return Fail(ctx, p1, p2, name + ": not a valid absolute URI");
                return Ok(new StringValue(sel(u)), ctx, p1, p2);
            });
        }
    }
}

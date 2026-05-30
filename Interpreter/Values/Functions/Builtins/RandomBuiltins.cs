using System;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.random — pseudo-random generation + UUIDs.
    //
    // Backed by one process-wide System.Random. `random_seed(n)` reseeds it,
    // making every subsequent draw reproducible (same seed -> same sequence
    // within a given .NET runtime, identically on Windows/macOS/Linux). All
    // AOT-safe. `uuid_v4` uses Guid (non-deterministic by design).
    public static class RandomBuiltins
    {
        private static Random _rng = new Random();
        private static readonly object _lock = new();

        public static void Register()
        {
            // random_seed(n) -> reseed the generator for reproducible draws
            BuiltInRegistry.Register("random_seed", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("random_seed", args, 1, ctx, p1, p2, out var err)) return err;
                lock (_lock) { _rng = new Random((int)AsLong(args[0])); }
                return OkNull(ctx, p1, p2);
            });

            // random_int(min, max) -> integer in [min, max] inclusive
            BuiltInRegistry.Register("random_int", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("random_int", args, 2, ctx, p1, p2, out var err)) return err;
                long lo = AsLong(args[0]);
                long hi = AsLong(args[1]);
                if (lo > hi) { (lo, hi) = (hi, lo); }
                long v;
                lock (_lock) { v = _rng.NextInt64(lo, hi == long.MaxValue ? hi : hi + 1); }
                return Ok(NumberFor(v), ctx, p1, p2);
            });

            // random() / random_float() -> double in [0.0, 1.0)
            // (`random` is the long-standing bare name; both share the PRNG.)
            BuiltInHandler nextDouble = (ctx, args, p1, p2) =>
            {
                if (args.Count != 0) return Fail(ctx, p1, p2, "random expects 0 argument(s), got " + args.Count);
                double v;
                lock (_lock) { v = _rng.NextDouble(); }
                return Ok(new DoubleValue(v), ctx, p1, p2);
            };
            BuiltInRegistry.Register("random", nextDouble);
            BuiltInRegistry.Register("random_float", nextDouble);

            // random_bool() -> boolean
            BuiltInRegistry.Register("random_bool", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("random_bool", args, 0, ctx, p1, p2, out var err)) return err;
                bool v;
                lock (_lock) { v = _rng.Next(2) == 1; }
                return Ok(MakeBool(v), ctx, p1, p2);
            });

            // random_bytes(n) -> list of n integers in [0, 255]
            BuiltInRegistry.Register("random_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("random_bytes", args, 1, ctx, p1, p2, out var err)) return err;
                int n = AsInt(args[0]);
                if (n < 0) return Fail(ctx, p1, p2, "random_bytes: count must be non-negative");
                var buf = new byte[n];
                lock (_lock) { _rng.NextBytes(buf); }
                var outList = new List<RuntimeValue>(n);
                for (int i = 0; i < n; i++) outList.Add(new IntegerValue(buf[i]));
                return Ok(new ListValue(outList), ctx, p1, p2);
            });

            // random_choice(list) -> a uniformly-chosen element
            BuiltInRegistry.Register("random_choice", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("random_choice", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not ListValue lv)
                    return Fail(ctx, p1, p2, "random_choice: argument must be a list");
                if (lv.Elements.Count == 0)
                    return Fail(ctx, p1, p2, "random_choice: list is empty");
                int idx;
                lock (_lock) { idx = _rng.Next(lv.Elements.Count); }
                return Ok(lv.Elements[idx], ctx, p1, p2);
            });

            // uuid_v4() -> a random RFC 4122 v4 UUID string
            BuiltInRegistry.Register("uuid_v4", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("uuid_v4", args, 0, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Guid.NewGuid().ToString()), ctx, p1, p2);
            });
        }
    }
}

using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // Surface API for the regex subsystem.
    //
    // Two categories of builtin:
    //   * pattern factory + tests  (regex / re_is_match / re_match / ...)
    //   * match accessors          (re_match_text / re_group / re_groups / ...)
    //
    // Every "pattern" parameter accepts either a precompiled RegexValue or a
    // raw string, which is funnelled through RegexValue.Compile so the global
    // pattern cache amortises repeated invocations. This is the same cache
    // that backs `re"..."` literals, so dynamic and literal call sites share
    // the same compiled artifact.
    public static class RegexBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("regex",            Make);
            BuiltInRegistry.Register("re_is_match",      IsMatch);
            BuiltInRegistry.Register("re_match",         MatchOne);
            BuiltInRegistry.Register("re_find_all",      FindAll);
            BuiltInRegistry.Register("re_replace",       Replace);
            BuiltInRegistry.Register("re_replace_all",   Replace);
            BuiltInRegistry.Register("re_split",         Split);
            BuiltInRegistry.Register("re_escape",        Escape);

            BuiltInRegistry.Register("re_pattern",       PatternOf);
            BuiltInRegistry.Register("re_flags",         FlagsOf);

            BuiltInRegistry.Register("re_match_text",    MatchText);
            BuiltInRegistry.Register("re_match_start",   MatchStart);
            BuiltInRegistry.Register("re_match_end",     MatchEnd);
            BuiltInRegistry.Register("re_match_length",  MatchLength);
            BuiltInRegistry.Register("re_group",         GroupAt);
            BuiltInRegistry.Register("re_groups",        AllGroups);
            BuiltInRegistry.Register("re_named_group",   NamedGroup);
            BuiltInRegistry.Register("re_group_count",   GroupCount);
            BuiltInRegistry.Register("re_success",       MatchSuccess);

            BuiltInRegistry.Register("re_cache_size",    CacheSize);
            BuiltInRegistry.Register("re_cache_clear",   CacheClear);
        }

        // ---------------------------------------------------------------
        // Pattern factory / introspection
        // ---------------------------------------------------------------

        private static RuntimeResult Make(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("regex", args, 1, 2, ctx, p1, p2, out var err)) return err;

            if (args[0] is RegexValue alreadyCompiled && args.Count == 1)
            {
                return Ok(alreadyCompiled, ctx, p1, p2);
            }

            if (!(args[0] is StringValue patStr))
            {
                return Fail(ctx, p1, p2, "regex(pattern, flags?) expects pattern to be a string");
            }

            string flags = "";
            if (args.Count == 2)
            {
                if (!(args[1] is StringValue fs)) return Fail(ctx, p1, p2, "regex flags must be a string");
                flags = fs.Value;
            }

            RegexOptions opts;
            try { opts = RegexValue.ParseFlags(flags); }
            catch (ArgumentException ex)
            {
                return new RuntimeResult().Failure(new RuntimeError(p1, p2, ex.Message, ctx,
                    code: DiagnosticCode.RuntimeRegexCompile,
                    primaryLabel: "bad flag character",
                    help: "valid flags are i, m, s, x, n"));
            }

            Regex regex;
            try { regex = RegexValue.Compile(patStr.Value, opts); }
            catch (ArgumentException ex)
            {
                return new RuntimeResult().Failure(new RuntimeError(p1, p2,
                    $"invalid regex pattern: {ex.Message}",
                    ctx,
                    code: DiagnosticCode.RuntimeRegexCompile,
                    primaryLabel: "pattern rejected by the regex engine",
                    help: "check escaping, group syntax, and quantifier placement"));
            }

            return Ok(new RegexValue(patStr.Value, flags, opts, regex), ctx, p1, p2);
        }

        private static RuntimeResult PatternOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_pattern", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is RegexValue rv)) return Fail(ctx, p1, p2, "re_pattern expects a regex");
            return Ok(new StringValue(rv.Pattern), ctx, p1, p2);
        }

        private static RuntimeResult FlagsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_flags", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is RegexValue rv)) return Fail(ctx, p1, p2, "re_flags expects a regex");
            return Ok(new StringValue(rv.Flags), ctx, p1, p2);
        }

        // ---------------------------------------------------------------
        // Match / search
        // ---------------------------------------------------------------

        private static RuntimeResult IsMatch(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_is_match", args, 2, ctx, p1, p2, out var err)) return err;
            if (!TryResolveRegex(args[0], ctx, p1, p2, out var regex, out var fail)) return fail;
            if (!(args[1] is StringValue sv)) return Fail(ctx, p1, p2, "re_is_match expects (pattern, string)");

            try
            {
                bool ok = regex.IsMatch(sv.Value);
                return Ok(BooleanValue.Of(ok), ctx, p1, p2);
            }
            catch (RegexMatchTimeoutException)
            {
                return RegexTimeout(ctx, p1, p2);
            }
        }

        private static RuntimeResult MatchOne(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("re_match", args, 2, 3, ctx, p1, p2, out var err)) return err;
            if (!TryResolveRegex(args[0], ctx, p1, p2, out var regex, out var fail)) return fail;
            if (!(args[1] is StringValue sv)) return Fail(ctx, p1, p2, "re_match expects (pattern, string, start?)");

            int start = 0;
            if (args.Count == 3) start = ClampInt(AsInt(args[2]), 0, sv.Value.Length);

            try
            {
                var m = regex.Match(sv.Value, start);
                if (!m.Success) return OkNull(ctx, p1, p2);
                return Ok(new MatchValue(m, sv.Value), ctx, p1, p2);
            }
            catch (RegexMatchTimeoutException)
            {
                return RegexTimeout(ctx, p1, p2);
            }
        }

        private static RuntimeResult FindAll(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_find_all", args, 2, ctx, p1, p2, out var err)) return err;
            if (!TryResolveRegex(args[0], ctx, p1, p2, out var regex, out var fail)) return fail;
            if (!(args[1] is StringValue sv)) return Fail(ctx, p1, p2, "re_find_all expects (pattern, string)");

            try
            {
                var matches = regex.Matches(sv.Value);
                var list = new List<RuntimeValue>(matches.Count);
                foreach (Match m in matches) list.Add(new MatchValue(m, sv.Value));
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            catch (RegexMatchTimeoutException)
            {
                return RegexTimeout(ctx, p1, p2);
            }
        }

        private static RuntimeResult Replace(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_replace", args, 3, ctx, p1, p2, out var err)) return err;
            if (!TryResolveRegex(args[0], ctx, p1, p2, out var regex, out var fail)) return fail;
            if (!(args[1] is StringValue input)) return Fail(ctx, p1, p2, "re_replace expects (pattern, string, replacement)");
            if (!(args[2] is StringValue replacement)) return Fail(ctx, p1, p2, "re_replace replacement must be a string");

            try
            {
                string outStr = regex.Replace(input.Value, replacement.Value);
                return Ok(new StringValue(outStr), ctx, p1, p2);
            }
            catch (RegexMatchTimeoutException)
            {
                return RegexTimeout(ctx, p1, p2);
            }
            catch (ArgumentException ex)
            {
                return new RuntimeResult().Failure(new RuntimeError(p1, p2,
                    $"invalid replacement string: {ex.Message}",
                    ctx,
                    code: DiagnosticCode.RuntimeRegexMatch,
                    primaryLabel: "replacement rejected by the regex engine",
                    help: "use $1, $2, ${name} for capture references; literal $ must be doubled as $$"));
            }
        }

        private static RuntimeResult Split(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("re_split", args, 2, 3, ctx, p1, p2, out var err)) return err;
            if (!TryResolveRegex(args[0], ctx, p1, p2, out var regex, out var fail)) return fail;
            if (!(args[1] is StringValue input)) return Fail(ctx, p1, p2, "re_split expects (pattern, string, limit?)");

            int limit = int.MaxValue;
            if (args.Count == 3) limit = System.Math.Max(0, AsInt(args[2]));

            try
            {
                string[] parts = limit == int.MaxValue
                    ? regex.Split(input.Value)
                    : regex.Split(input.Value, limit);
                var list = new List<RuntimeValue>(parts.Length);
                foreach (var p in parts) list.Add(new StringValue(p));
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            catch (RegexMatchTimeoutException)
            {
                return RegexTimeout(ctx, p1, p2);
            }
        }

        private static RuntimeResult Escape(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_escape", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is StringValue sv)) return Fail(ctx, p1, p2, "re_escape expects a string");
            return Ok(new StringValue(Regex.Escape(sv.Value)), ctx, p1, p2);
        }

        // ---------------------------------------------------------------
        // Match accessors
        // ---------------------------------------------------------------

        private static RuntimeResult MatchText(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_match_text", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_match_text expects a regex match");
            return Ok(new StringValue(mv.Match.Success ? mv.Match.Value : ""), ctx, p1, p2);
        }

        private static RuntimeResult MatchStart(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_match_start", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_match_start expects a regex match");
            return Ok(new IntegerValue(mv.Match.Success ? mv.Match.Index : -1), ctx, p1, p2);
        }

        private static RuntimeResult MatchEnd(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_match_end", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_match_end expects a regex match");
            int end = mv.Match.Success ? mv.Match.Index + mv.Match.Length : -1;
            return Ok(new IntegerValue(end), ctx, p1, p2);
        }

        private static RuntimeResult MatchLength(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_match_length", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_match_length expects a regex match");
            return Ok(new IntegerValue(mv.Match.Success ? mv.Match.Length : 0), ctx, p1, p2);
        }

        private static RuntimeResult GroupAt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_group", args, 2, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_group expects (match, index|name)");

            if (args[1] is StringValue ns)
            {
                var g = mv.Match.Groups[ns.Value];
                if (g == null || !g.Success) return OkNull(ctx, p1, p2);
                return Ok(new StringValue(g.Value), ctx, p1, p2);
            }

            int idx = AsInt(args[1]);
            if (idx < 0 || idx >= mv.Match.Groups.Count) return OkNull(ctx, p1, p2);
            var grp = mv.Match.Groups[idx];
            if (!grp.Success) return OkNull(ctx, p1, p2);
            return Ok(new StringValue(grp.Value), ctx, p1, p2);
        }

        private static RuntimeResult AllGroups(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_groups", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_groups expects a regex match");

            var groups = mv.Match.Groups;
            var list = new List<RuntimeValue>(groups.Count);
            // Slot 0 is the whole match; capture-only consumers can skip it.
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                list.Add(g.Success
                    ? (RuntimeValue)new StringValue(g.Value)
                    : NullValue.Null);
            }
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult NamedGroup(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_named_group", args, 2, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_named_group expects (match, name)");
            if (!(args[1] is StringValue ns)) return Fail(ctx, p1, p2, "re_named_group name must be a string");

            var g = mv.Match.Groups[ns.Value];
            if (g == null || !g.Success) return OkNull(ctx, p1, p2);
            return Ok(new StringValue(g.Value), ctx, p1, p2);
        }

        private static RuntimeResult GroupCount(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_group_count", args, 1, ctx, p1, p2, out var err)) return err;
            if (!(args[0] is MatchValue mv)) return Fail(ctx, p1, p2, "re_group_count expects a regex match");
            // Caller-facing count excludes the whole-match slot at index 0.
            int n = System.Math.Max(0, mv.Match.Groups.Count - 1);
            return Ok(new IntegerValue(n), ctx, p1, p2);
        }

        private static RuntimeResult MatchSuccess(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_success", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is MatchValue mv) return Ok(BooleanValue.Of(mv.Match.Success), ctx, p1, p2);
            return Ok(BooleanValue.Of(false), ctx, p1, p2);
        }

        // ---------------------------------------------------------------
        // Cache introspection (mostly for testing / observability)
        // ---------------------------------------------------------------

        private static RuntimeResult CacheSize(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_cache_size", args, 0, ctx, p1, p2, out var err)) return err;
            return Ok(new IntegerValue(RegexValue.CacheCount), ctx, p1, p2);
        }

        private static RuntimeResult CacheClear(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("re_cache_clear", args, 0, ctx, p1, p2, out var err)) return err;
            RegexValue.ClearCache();
            return OkNull(ctx, p1, p2);
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static bool TryResolveRegex(RuntimeValue v, Context ctx, Position p1, Position p2,
                                            out Regex regex, out RuntimeResult fail)
        {
            switch (v)
            {
                case RegexValue rv:
                    regex = rv.Regex;
                    fail = default;
                    return true;
                case StringValue sv:
                    try
                    {
                        regex = RegexValue.Compile(sv.Value, RegexOptions.CultureInvariant);
                        fail = default;
                        return true;
                    }
                    catch (ArgumentException ex)
                    {
                        regex = null!;
                        fail = new RuntimeResult().Failure(new RuntimeError(p1, p2,
                            $"invalid regex pattern: {ex.Message}",
                            ctx,
                            code: DiagnosticCode.RuntimeRegexCompile,
                            primaryLabel: "pattern rejected by the regex engine",
                            help: "use the regex(pattern, flags) builtin to inspect compile errors"));
                        return false;
                    }
                default:
                    regex = null!;
                    fail = Fail(ctx, p1, p2, "expected a regex or pattern string");
                    return false;
            }
        }

        private static RuntimeResult RegexTimeout(Context ctx, Position p1, Position p2)
        {
            return new RuntimeResult().Failure(new RuntimeError(p1, p2,
                "regex match exceeded the 5s safety timeout",
                ctx,
                code: DiagnosticCode.RuntimeRegexMatch,
                primaryLabel: "engine aborted to prevent catastrophic backtracking",
                help: "simplify the pattern, anchor it, or avoid nested quantifiers"));
        }

        private static int ClampInt(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}

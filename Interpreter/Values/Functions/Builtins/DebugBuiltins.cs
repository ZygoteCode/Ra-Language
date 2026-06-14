using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class DebugBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("dump", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dump", args, 1, ctx, p1, p2, out var err)) return err;
                var s = DumpValue(args[0], 0);
                Console.WriteLine(s);
                return Ok(new StringValue(s), ctx, p1, p2);
            });
            BuiltInRegistry.Register("dump_str", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dump_str", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(DumpValue(args[0], 0)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("gc_collect", (ctx, args, p1, p2) =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("gc_memory", (ctx, args, p1, p2) =>
                Ok(new LongValue(GC.GetTotalMemory(false)), ctx, p1, p2));
            BuiltInRegistry.Register("gc_total_allocated", (ctx, args, p1, p2) =>
                Ok(new LongValue(GC.GetTotalAllocatedBytes(false)), ctx, p1, p2));
            BuiltInRegistry.Register("gc_max_generation", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(GC.MaxGeneration), ctx, p1, p2));
            BuiltInRegistry.Register("gc_generation", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("gc_generation", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new IntegerValue(GC.GetGeneration(args[0])), ctx, p1, p2); }
                catch { return Ok(new IntegerValue(-1), ctx, p1, p2); }
            });
            BuiltInRegistry.Register("breakpoint", (ctx, args, p1, p2) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("assert", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert", args, 1, 2, ctx, p1, p2, out var err)) return err;
                if (AsBool(args[0])) return OkNull(ctx, p1, p2);
                var msg = args.Count == 2 ? AsString(args[1]) : "assertion failed";
                return Fail(ctx, p1, p2, "assert: " + msg);
            });
            BuiltInRegistry.Register("assert_eq", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert_eq", args, 2, 3, ctx, p1, p2, out var err)) return err;
                var (eq, _) = args[0].GetComparisonEq(args[1]);
                if (eq != null && eq.IsTrue()) return OkNull(ctx, p1, p2);
                var msg = args.Count == 3 ? AsString(args[2]) : $"assert_eq failed: {args[0]} != {args[1]}";
                return Fail(ctx, p1, p2, msg);
            });
            BuiltInRegistry.Register("assert_ne", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert_ne", args, 2, 3, ctx, p1, p2, out var err)) return err;
                var (eq, _) = args[0].GetComparisonEq(args[1]);
                if (eq == null || !eq.IsTrue()) return OkNull(ctx, p1, p2);
                var msg = args.Count == 3 ? AsString(args[2]) : $"assert_ne failed: {args[0]} == {args[1]}";
                return Fail(ctx, p1, p2, msg);
            });
            BuiltInRegistry.Register("assert_true", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert_true", args, 1, 2, ctx, p1, p2, out var err)) return err;
                if (AsBool(args[0])) return OkNull(ctx, p1, p2);
                return Fail(ctx, p1, p2, "assert_true: " + (args.Count == 2 ? AsString(args[1]) : "expected true"));
            });
            BuiltInRegistry.Register("assert_false", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert_false", args, 1, 2, ctx, p1, p2, out var err)) return err;
                if (!AsBool(args[0])) return OkNull(ctx, p1, p2);
                return Fail(ctx, p1, p2, "assert_false: " + (args.Count == 2 ? AsString(args[1]) : "expected false"));
            });
            // assert_approx(a, b [, eps]) — float-tolerant equality. Default
            // epsilon 1e-9; the form that keeps `==` from biting on rounding.
            BuiltInRegistry.Register("assert_approx", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("assert_approx", args, 2, 3, ctx, p1, p2, out var err)) return err;
                double a = AsDouble(args[0]), b = AsDouble(args[1]);
                double eps = args.Count == 3 ? Math.Abs(AsDouble(args[2])) : 1e-9;
                if (Math.Abs(a - b) <= eps) return OkNull(ctx, p1, p2);
                return Fail(ctx, p1, p2, $"assert_approx failed: |{a} - {b}| > {eps}");
            });
            // Unconditional failures, the catchable cousins of Rust's panic! /
            // todo! / unreachable!. Each raises a runtime error (try/catch-able)
            // carrying a clear, prefixed message.
            BuiltInRegistry.Register("panic", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("panic", args, 0, 1, ctx, p1, p2, out var err)) return err;
                return Fail(ctx, p1, p2, "panic: " + (args.Count == 1 ? AsString(args[0]) : "explicit panic"));
            });
            BuiltInRegistry.Register("todo", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("todo", args, 0, 1, ctx, p1, p2, out var err)) return err;
                return Fail(ctx, p1, p2, "todo: not yet implemented" + (args.Count == 1 ? ": " + AsString(args[0]) : ""));
            });
            BuiltInRegistry.Register("unreachable", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("unreachable", args, 0, 1, ctx, p1, p2, out var err)) return err;
                return Fail(ctx, p1, p2, "unreachable: entered unreachable code" + (args.Count == 1 ? ": " + AsString(args[0]) : ""));
            });
            BuiltInRegistry.Register("warn", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("warn", args, 1, ctx, p1, p2, out var err)) return err;
                Console.Error.WriteLine("[warn] " + args[0]);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("eprintln", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("eprintln", args, 1, ctx, p1, p2, out var err)) return err;
                Console.Error.WriteLine(args[0]?.ToString() ?? "");
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("eprint", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("eprint", args, 1, ctx, p1, p2, out var err)) return err;
                Console.Error.Write(args[0]?.ToString() ?? "");
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("println", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("println", args, 1, ctx, p1, p2, out var err)) return err;
                Console.WriteLine(args[0]?.ToString() ?? "");
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("print_no_newline", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("print_no_newline", args, 1, ctx, p1, p2, out var err)) return err;
                Console.Write(args[0]?.ToString() ?? "");
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("read_line", (ctx, args, p1, p2) =>
                Ok(new StringValue(Console.ReadLine() ?? ""), ctx, p1, p2));
            BuiltInRegistry.Register("clear_console", (ctx, args, p1, p2) =>
            {
                try { Console.Clear(); } catch { }
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("debug_break_if", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("debug_break_if", args, 1, ctx, p1, p2, out var err)) return err;
                if (AsBool(args[0]) && Debugger.IsAttached) Debugger.Break();
                return OkNull(ctx, p1, p2);
            });
        }

        private static string DumpValue(RuntimeValue v, int depth)
        {
            if (depth > 8) return "...";
            var ind = new string(' ', depth * 2);
            switch (v)
            {
                case null: return "null";
                case NullValue: return "null";
                case BooleanValue bv: return bv.Value ? "true" : "false";
                case StringValue sv: return "\"" + sv.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                case ListValue lv:
                {
                    if (lv.Elements.Count == 0) return "[]";
                    var sb = new StringBuilder("[\n");
                    foreach (var e in lv.Elements) sb.Append(new string(' ', (depth + 1) * 2)).Append(DumpValue(e, depth + 1)).Append(",\n");
                    sb.Append(ind).Append(']');
                    return sb.ToString();
                }
                case TupleValue tv:
                    return "(" + string.Join(", ", tv.Elements.Select(e => DumpValue(e, depth + 1))) + ")";
                case SetValue setv:
                    return "{" + string.Join(", ", setv.Elements.Select(e => DumpValue(e, depth + 1))) + "}";
                case MapValue mv:
                {
                    if (mv.Pairs.Count == 0) return "{}";
                    var sb = new StringBuilder("{\n");
                    foreach (var (k, val) in mv.Pairs)
                        sb.Append(new string(' ', (depth + 1) * 2)).Append(DumpValue(k, depth + 1)).Append(": ").Append(DumpValue(val, depth + 1)).Append(",\n");
                    sb.Append(ind).Append('}');
                    return sb.ToString();
                }
                case ClassInstanceValue ci:
                {
                    var sb = new StringBuilder(ci.Definition.ClassName).Append(" {\n");
                    foreach (var (n, fv) in ci.Fields)
                        sb.Append(new string(' ', (depth + 1) * 2)).Append(n).Append(": ").Append(DumpValue(fv, depth + 1)).Append(",\n");
                    sb.Append(ind).Append('}');
                    return sb.ToString();
                }
                case StructInstanceValue si:
                {
                    var sb = new StringBuilder(si.Definition.StructName).Append(" {\n");
                    foreach (var (n, fv) in si.Fields)
                        sb.Append(new string(' ', (depth + 1) * 2)).Append(n).Append(": ").Append(DumpValue(fv, depth + 1)).Append(",\n");
                    sb.Append(ind).Append('}');
                    return sb.ToString();
                }
                case AnnotationInstanceValue ai:
                {
                    var args = string.Join(", ", ai.NamedArgs.Select(kv => kv.Key + ": " + DumpValue(kv.Value, depth + 1)));
                    return "@" + ai.DefinitionName + "(" + args + ")";
                }
                default: return v.ToString() ?? "<null>";
            }
        }
    }
}

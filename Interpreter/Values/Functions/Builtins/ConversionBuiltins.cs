using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class ConversionBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("to_int", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_int", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new IntegerValue(AsInt(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_long", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_long", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new LongValue(AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_short", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_short", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new ShortValue((short)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_byte", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_byte", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new ByteValue((byte)(AsLong(args[0]) & 0xff)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_uint", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_uint", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new UnsignedIntegerValue((uint)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_ulong", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_ulong", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new UnsignedLongValue((ulong)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_ushort", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_ushort", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new UnsignedShortValue((ushort)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_int128", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_int128", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new Int128Value((System.Int128)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_uint128", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_uint128", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new UnsignedInt128Value((System.UInt128)(ulong)AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_float", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_float", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new FloatValue((float)AsDouble(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_double", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_double", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new DoubleValue(AsDouble(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_decimal", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_decimal", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new DecimalValue((decimal)AsDouble(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_bool", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_bool", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(AsBool(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_string", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_string", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(args[0]?.ToString() ?? ""), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_list", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_list", args, 1, ctx, p1, p2, out var err)) return err;
                switch (args[0])
                {
                    case ListValue lv: return Ok(new ListValue(new List<RuntimeValue>(lv.Elements)), ctx, p1, p2);
                    case SetValue setv: return Ok(new ListValue(setv.Elements.ToList()), ctx, p1, p2);
                    case TupleValue tv: return Ok(new ListValue(new List<RuntimeValue>(tv.Elements)), ctx, p1, p2);
                    case MapValue mv:
                    {
                        var l = new List<RuntimeValue>();
                        foreach (var (k, v) in mv.Pairs) l.Add(new TupleValue(new List<RuntimeValue> { k, v }));
                        return Ok(new ListValue(l), ctx, p1, p2);
                    }
                    case StringValue sv:
                    {
                        var l = new List<RuntimeValue>();
                        var idx = StringInfo.ParseCombiningCharacters(sv.Value);
                        for (int i = 0; i < idx.Length; i++)
                        {
                            int s = idx[i];
                            int len = (i + 1 < idx.Length) ? idx[i + 1] - s : sv.Value.Length - s;
                            l.Add(new StringValue(sv.Value.Substring(s, len)));
                        }
                        return Ok(new ListValue(l), ctx, p1, p2);
                    }
                }
                return Ok(new ListValue(new List<RuntimeValue> { args[0] }), ctx, p1, p2);
            });
            BuiltInRegistry.Register("to_set", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("to_set", args, 1, ctx, p1, p2, out var err)) return err;
                var hs = new HashSet<RuntimeValue>();
                IEnumerable<RuntimeValue> src = args[0] switch
                {
                    ListValue lv => lv.Elements,
                    SetValue setv => setv.Elements,
                    TupleValue tv => tv.Elements,
                    _ => new[] { args[0] }
                };
                foreach (var e in src)
                {
                    bool exists = false;
                    foreach (var v in hs) if (v.Equals(e)) { exists = true; break; }
                    if (!exists) hs.Add(e);
                }
                return Ok(new SetValue(hs), ctx, p1, p2);
            });

            BuiltInRegistry.Register("parse_int", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("parse_int", args, 1, 2, ctx, p1, p2, out var err)) return err;
                int radix = args.Count == 2 ? AsInt(args[1]) : 10;
                if (radix < 2 || radix > 36) return Fail(ctx, p1, p2, "parse_int: radix must be in 2..36");
                try { return Ok(new LongValue(Convert.ToInt64(AsString(args[0]).Trim(), radix)), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("parse_float", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("parse_float", args, 1, ctx, p1, p2, out var err)) return err;
                if (double.TryParse(AsString(args[0]).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return Ok(new DoubleValue(d), ctx, p1, p2);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("parse_bool", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("parse_bool", args, 1, ctx, p1, p2, out var err)) return err;
                var s = AsString(args[0]).Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes" || s == "on") return Ok(MakeBool(true), ctx, p1, p2);
                if (s == "false" || s == "0" || s == "no" || s == "off") return Ok(MakeBool(false), ctx, p1, p2);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("parse_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("parse_hex", args, 1, ctx, p1, p2, out var err)) return err;
                var s = AsString(args[0]).Trim();
                if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
                try { return Ok(new LongValue(Convert.ToInt64(s, 16)), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("parse_bin", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("parse_bin", args, 1, ctx, p1, p2, out var err)) return err;
                var s = AsString(args[0]).Trim();
                if (s.StartsWith("0b") || s.StartsWith("0B")) s = s.Substring(2);
                try { return Ok(new LongValue(Convert.ToInt64(s, 2)), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("parse_oct", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("parse_oct", args, 1, ctx, p1, p2, out var err)) return err;
                var s = AsString(args[0]).Trim();
                if (s.StartsWith("0o") || s.StartsWith("0O")) s = s.Substring(2);
                try { return Ok(new LongValue(Convert.ToInt64(s, 8)), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });

            BuiltInRegistry.Register("format_int", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("format_int", args, 1, 2, ctx, p1, p2, out var err)) return err;
                long v = AsLong(args[0]);
                int radix = args.Count == 2 ? AsInt(args[1]) : 10;
                if (radix < 2 || radix > 36) return Fail(ctx, p1, p2, "format_int: radix 2..36");
                return Ok(new StringValue(Convert.ToString(v, radix == 10 ? 10 : (radix == 16 ? 16 : (radix == 8 ? 8 : (radix == 2 ? 2 : 10))))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("format_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("format_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToString(AsLong(args[0]), 16)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("format_bin", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("format_bin", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToString(AsLong(args[0]), 2)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("format_oct", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("format_oct", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToString(AsLong(args[0]), 8)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("format_float", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("format_float", args, 1, 2, ctx, p1, p2, out var err)) return err;
                double v = AsDouble(args[0]);
                int prec = args.Count == 2 ? AsInt(args[1]) : 6;
                return Ok(new StringValue(v.ToString("F" + prec, CultureInfo.InvariantCulture)), ctx, p1, p2);
            });
        }
    }
}

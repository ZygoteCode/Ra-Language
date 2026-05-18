using System.Collections.Generic;
using System.Globalization;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class CoercerRegistry
    {
        private static readonly Dictionary<string, System.Func<RuntimeValue, Context, (RuntimeValue? value, string? msg)>> _strategies
            = new(System.StringComparer.Ordinal)
        {
            ["to_int"] = ToInt,
            ["to_long"] = ToLong,
            ["to_float"] = ToFloat,
            ["to_double"] = ToDouble,
            ["to_string"] = ToStr,
            ["to_bool"] = ToBool,
            ["trim"] = Trim,
            ["lower"] = Lower,
            ["upper"] = Upper,
            ["abs"] = Abs,
            ["clamp_non_negative"] = ClampNonNegative,
        };

        public static (RuntimeValue? value, string? msg) Apply(string strategy, RuntimeValue value, Context ctx)
        {
            if (!_strategies.TryGetValue(strategy, out var fn))
                return (null, $"unknown coercer strategy '{strategy}'");
            return fn(value, ctx);
        }

        public static IEnumerable<string> AvailableStrategies => _strategies.Keys;

        public static void Register(string name, System.Func<RuntimeValue, Context, (RuntimeValue? value, string? msg)> fn)
        {
            _strategies[name] = fn;
        }

        private static (RuntimeValue?, string?) ToInt(RuntimeValue v, Context ctx)
        {
            if (v is IntegerValue) return (v, null);
            if (v is StringValue s)
            {
                if (int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                    return (new IntegerValue(p).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                return (null, $"cannot parse '{s.Value}' as int");
            }
            if (v is NumberValue n)
            {
                try { return (new IntegerValue((int)n.Value.ToBigInteger()).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null); }
                catch { return (null, "number out of int range"); }
            }
            if (v is LongValue lv) return (new IntegerValue((int)lv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is FloatValue fv) return (new IntegerValue((int)fv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is DoubleValue dv) return (new IntegerValue((int)dv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is BooleanValue bv) return (new IntegerValue(bv.Value ? 1 : 0).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            return (null, $"cannot coerce {v.Type} to int");
        }

        private static (RuntimeValue?, string?) ToLong(RuntimeValue v, Context ctx)
        {
            if (v is LongValue) return (v, null);
            if (v is IntegerValue iv) return (new LongValue(iv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is StringValue s)
            {
                if (long.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p))
                    return (new LongValue(p).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                return (null, $"cannot parse '{s.Value}' as long");
            }
            if (v is NumberValue n)
            {
                try { return (new LongValue((long)n.Value.ToBigInteger()).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null); }
                catch { return (null, "number out of long range"); }
            }
            return (null, $"cannot coerce {v.Type} to long");
        }

        private static (RuntimeValue?, string?) ToFloat(RuntimeValue v, Context ctx)
        {
            if (v is FloatValue) return (v, null);
            if (v is IntegerValue iv) return (new FloatValue(iv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is LongValue lv) return (new FloatValue(lv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is DoubleValue dv) return (new FloatValue((float)dv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is StringValue s)
            {
                if (float.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float p))
                    return (new FloatValue(p).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                return (null, $"cannot parse '{s.Value}' as float");
            }
            return (null, $"cannot coerce {v.Type} to float");
        }

        private static (RuntimeValue?, string?) ToDouble(RuntimeValue v, Context ctx)
        {
            if (v is DoubleValue) return (v, null);
            if (v is IntegerValue iv) return (new DoubleValue(iv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is LongValue lv) return (new DoubleValue(lv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is FloatValue fv) return (new DoubleValue(fv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is StringValue s)
            {
                if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    return (new DoubleValue(p).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                return (null, $"cannot parse '{s.Value}' as double");
            }
            return (null, $"cannot coerce {v.Type} to double");
        }

        private static (RuntimeValue?, string?) ToStr(RuntimeValue v, Context ctx)
        {
            if (v is StringValue) return (v, null);
            return (new StringValue(v.ToString() ?? string.Empty).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
        }

        private static (RuntimeValue?, string?) ToBool(RuntimeValue v, Context ctx)
        {
            if (v is BooleanValue) return (v, null);
            return (new BooleanValue(v.IsTrue()).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
        }

        private static (RuntimeValue?, string?) Trim(RuntimeValue v, Context ctx)
        {
            if (v is not StringValue sv) return (null, $"trim requires string, got {v.Type}");
            return (new StringValue(sv.Value?.Trim() ?? string.Empty).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
        }

        private static (RuntimeValue?, string?) Lower(RuntimeValue v, Context ctx)
        {
            if (v is not StringValue sv) return (null, $"lower requires string, got {v.Type}");
            return (new StringValue(sv.Value?.ToLowerInvariant() ?? string.Empty).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
        }

        private static (RuntimeValue?, string?) Upper(RuntimeValue v, Context ctx)
        {
            if (v is not StringValue sv) return (null, $"upper requires string, got {v.Type}");
            return (new StringValue(sv.Value?.ToUpperInvariant() ?? string.Empty).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
        }

        private static (RuntimeValue?, string?) Abs(RuntimeValue v, Context ctx)
        {
            if (v is IntegerValue iv) return (new IntegerValue(System.Math.Abs(iv.Value)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is LongValue lv) return (new LongValue(System.Math.Abs(lv.Value)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is FloatValue fv) return (new FloatValue(System.MathF.Abs(fv.Value)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is DoubleValue dv) return (new DoubleValue(System.Math.Abs(dv.Value)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is NumberValue nv)
            {
                try
                {
                    var bi = nv.Value.ToBigInteger();
                    if (bi.Sign < 0) bi = -bi;
                    return (new NumberValue(new BigNumber(bi, System.Numerics.BigInteger.Zero)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                }
                catch { return (null, "abs failed on number"); }
            }
            return (null, $"abs requires numeric, got {v.Type}");
        }

        private static (RuntimeValue?, string?) ClampNonNegative(RuntimeValue v, Context ctx)
        {
            if (v is IntegerValue iv) return (new IntegerValue(iv.Value < 0 ? 0 : iv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is LongValue lv) return (new LongValue(lv.Value < 0 ? 0 : lv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is FloatValue fv) return (new FloatValue(fv.Value < 0 ? 0 : fv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is DoubleValue dv) return (new DoubleValue(dv.Value < 0 ? 0 : dv.Value).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
            if (v is NumberValue nv)
            {
                try
                {
                    var bi = nv.Value.ToBigInteger();
                    if (bi.Sign < 0) bi = System.Numerics.BigInteger.Zero;
                    return (new NumberValue(new BigNumber(bi, System.Numerics.BigInteger.Zero)).SetContext(ctx).SetPos(v.PositionStart, v.PositionEnd), null);
                }
                catch { return (null, "clamp_non_negative failed on number"); }
            }
            return (null, $"clamp_non_negative requires numeric, got {v.Type}");
        }
    }
}

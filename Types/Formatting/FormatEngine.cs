using System.Globalization;
using System.Numerics;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Types.Formatting
{
    // Type-aware, AOT-friendly numeric / textual formatter for `${value:spec}`
    // segments inside interpolated strings.
    //
    // Dispatch path:
    //   FormattedInterpolationNodeVisitor → FormatEngine.Format(value, spec)
    //     → FormatInteger / FormatRational / FormatString / ...
    //
    // The engine never uses reflection: every runtime primitive has a direct
    // branch and writes through CultureInfo.InvariantCulture so output is
    // identical across hosts. Errors are returned as runtime errors so the
    // call site can wrap them in the standard interpreter result protocol.
    public static class FormatEngine
    {
        // Returns formatted text on success and a runtime error on type / spec
        // incompatibility. `posStart` / `posEnd` should bracket the offending
        // interpolation segment so the user gets a precise span.
        public static (string? Text, Error? Error) Format(
            RuntimeValue value,
            FormatSpec spec,
            Position posStart,
            Position posEnd,
            Context context)
        {
            if (value == null)
            {
                return (FormatNullForSpec(spec), null);
            }

            if (spec.IsDefault)
            {
                return (Utilities.StringConversionUtility.ConvertToString(value), null);
            }

            switch (value.Type)
            {
                case RuntimeValueType.Integer:
                    return FormatBigInteger(new BigInteger(((IntegerValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.Long:
                    return FormatBigInteger(new BigInteger(((LongValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.Short:
                    return FormatBigInteger(new BigInteger(((ShortValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.Byte:
                    return FormatBigInteger(new BigInteger(((ByteValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.UnsignedInteger:
                    return FormatBigInteger(new BigInteger(((UnsignedIntegerValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.UnsignedLong:
                    return FormatBigInteger(new BigInteger(((UnsignedLongValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.UnsignedShort:
                    return FormatBigInteger(new BigInteger(((UnsignedShortValue)value).Value), spec, value, posStart, posEnd, context);

                case RuntimeValueType.Int128:
                    return FormatBigInteger((BigInteger)((Int128Value)value).Value, spec, value, posStart, posEnd, context);

                case RuntimeValueType.UnsignedInt128:
                    return FormatBigInteger((BigInteger)((UnsignedInt128Value)value).Value, spec, value, posStart, posEnd, context);

                case RuntimeValueType.Float:
                    return FormatDouble(((FloatValue)value).Value, spec, value, posStart, posEnd, context);

                case RuntimeValueType.Double:
                    return FormatDouble(((DoubleValue)value).Value, spec, value, posStart, posEnd, context);

                case RuntimeValueType.Decimal:
                    return FormatDecimal(((DecimalValue)value).Value, spec, value, posStart, posEnd, context);

                case RuntimeValueType.Number:
                    return FormatNumber((NumberValue)value, spec, posStart, posEnd, context);

                case RuntimeValueType.String:
                    return (null, IncompatibleSpec("string", spec, posStart, posEnd, context));

                case RuntimeValueType.Boolean:
                    return (null, IncompatibleSpec("bool", spec, posStart, posEnd, context));

                default:
                    return (null, IncompatibleSpec(value.Type.ToString().ToLowerInvariant(), spec, posStart, posEnd, context));
            }
        }

        private static (string? Text, Error? Error) FormatBigInteger(
            BigInteger value, FormatSpec spec, RuntimeValue source,
            Position posStart, Position posEnd, Context context)
        {
            switch (spec.Kind)
            {
                case FormatKind.Default:
                case FormatKind.Decimal:
                    return (value.ToString(CultureInfo.InvariantCulture), null);

                case FormatKind.Hex:
                    return (FormatIntegerInBase(value, 16, spec, prefix: "0x", prefixUpper: "0X"), null);

                case FormatKind.Binary:
                    return (FormatIntegerInBase(value, 2, spec, prefix: "0b", prefixUpper: "0B"), null);

                case FormatKind.Octal:
                    return (FormatIntegerInBase(value, 8, spec, prefix: "0o", prefixUpper: "0O"), null);

                case FormatKind.Percent:
                    return FormatPercent((double)value, spec);

                case FormatKind.Float:
                case FormatKind.Exponential:
                case FormatKind.General:
                    // Allow integers to be rendered with float-style specs by
                    // widening to double (small loss for huge int128 values is
                    // documented behavior — users hit this rarely).
                    return FormatDouble((double)value, spec, source, posStart, posEnd, context);

                default:
                    return (null, IncompatibleSpec("integer", spec, posStart, posEnd, context));
            }
        }

        private static string FormatIntegerInBase(
            BigInteger value,
            int radix,
            FormatSpec spec,
            string prefix,
            string prefixUpper)
        {
            bool negative = value.Sign < 0;
            BigInteger abs = negative ? -value : value;

            string digits;
            if (radix == 16)
            {
                // BigInteger.ToString("X") prefers a leading 0 for positive
                // values whose high bit would otherwise look negative. Strip
                // it so output matches user expectations for `0x` formatting.
                string raw = abs.ToString(spec.UpperCase ? "X" : "x", CultureInfo.InvariantCulture);
                int firstNonZero = 0;
                while (firstNonZero < raw.Length - 1 && raw[firstNonZero] == '0') firstNonZero++;
                digits = raw.Substring(firstNonZero);
            }
            else
            {
                digits = BigIntegerToString(abs, radix);
            }

            var sb = new StringBuilder(digits.Length + 4);
            if (negative) sb.Append('-');
            if (spec.AlternateForm) sb.Append(spec.UpperCase ? prefixUpper : prefix);
            sb.Append(digits);
            return sb.ToString();
        }

        private static string BigIntegerToString(BigInteger value, int radix)
        {
            if (value.IsZero) return "0";
            var sb = new StringBuilder(32);
            var b = BigInteger.Abs(value);
            while (!b.IsZero)
            {
                int rem = (int)(b % radix);
                b = b / radix;
                sb.Insert(0, (char)(rem < 10 ? ('0' + rem) : ('a' + rem - 10)));
            }
            return sb.ToString();
        }

        private static (string? Text, Error? Error) FormatDouble(
            double value, FormatSpec spec, RuntimeValue source,
            Position posStart, Position posEnd, Context context)
        {
            int precision = spec.HasPrecision ? spec.Precision : 6;
            string fmt;

            switch (spec.Kind)
            {
                case FormatKind.Default:
                    if (!spec.HasPrecision)
                        return (value.ToString("R", CultureInfo.InvariantCulture), null);
                    fmt = "G" + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Float:
                    fmt = "F" + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Exponential:
                    fmt = (spec.UpperCase ? "E" : "e") + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.General:
                    fmt = (spec.UpperCase ? "G" : "G") + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Percent:
                    return FormatPercent(value, spec);

                case FormatKind.Decimal:
                    if (value == System.Math.Floor(value) && !double.IsInfinity(value))
                        return (((long)value).ToString(CultureInfo.InvariantCulture), null);
                    return (null, IncompatibleSpec("float / double", spec, posStart, posEnd, context));

                case FormatKind.Hex:
                case FormatKind.Binary:
                case FormatKind.Octal:
                    return (null, IncompatibleSpec("float / double", spec, posStart, posEnd, context));

                default:
                    return (null, IncompatibleSpec("float / double", spec, posStart, posEnd, context));
            }
        }

        private static (string? Text, Error? Error) FormatDecimal(
            decimal value, FormatSpec spec, RuntimeValue source,
            Position posStart, Position posEnd, Context context)
        {
            int precision = spec.HasPrecision ? spec.Precision : 6;
            string fmt;
            switch (spec.Kind)
            {
                case FormatKind.Default:
                    if (!spec.HasPrecision)
                        return (value.ToString(CultureInfo.InvariantCulture), null);
                    fmt = "G" + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Float:
                    fmt = "F" + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Exponential:
                    fmt = (spec.UpperCase ? "E" : "e") + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.General:
                    fmt = "G" + precision;
                    return (value.ToString(fmt, CultureInfo.InvariantCulture), null);

                case FormatKind.Percent:
                    return FormatPercent((double)value, spec);

                case FormatKind.Decimal:
                    if (value == decimal.Truncate(value))
                        return (decimal.Truncate(value).ToString("F0", CultureInfo.InvariantCulture), null);
                    return (null, IncompatibleSpec("decimal", spec, posStart, posEnd, context));

                case FormatKind.Hex:
                case FormatKind.Binary:
                case FormatKind.Octal:
                    return (null, IncompatibleSpec("decimal", spec, posStart, posEnd, context));

                default:
                    return (null, IncompatibleSpec("decimal", spec, posStart, posEnd, context));
            }
        }

        private static (string? Text, Error? Error) FormatNumber(
            NumberValue numberValue,
            FormatSpec spec,
            Position posStart,
            Position posEnd,
            Context context)
        {
            var bn = numberValue.Value;
            bool isIntegral = bn.Scale.IsZero;

            switch (spec.Kind)
            {
                case FormatKind.Hex:
                case FormatKind.Binary:
                case FormatKind.Octal:
                case FormatKind.Decimal:
                    if (!isIntegral)
                        return (null, IncompatibleSpec("number (non-integral)", spec, posStart, posEnd, context));
                    return FormatBigInteger(bn.Unscaled, spec, numberValue, posStart, posEnd, context);

                case FormatKind.Float:
                case FormatKind.Exponential:
                case FormatKind.General:
                case FormatKind.Percent:
                    if (!double.TryParse(bn.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        return (null, new RuntimeError(posStart, posEnd,
                            "number value too large to format with a floating-point spec",
                            context,
                            code: DiagnosticCode.RuntimeTypeMismatch,
                            primaryLabel: "value exceeds double-precision range",
                            help: "use the default spec ':' or render the value as an integer with ':d' / ':#x'"));
                    }
                    return FormatDouble(d, spec, numberValue, posStart, posEnd, context);

                case FormatKind.Default:
                    if (!spec.HasPrecision) return (bn.ToString(), null);
                    if (!double.TryParse(bn.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dDef))
                        return (bn.ToString(), null);
                    return FormatDouble(dDef, spec, numberValue, posStart, posEnd, context);

                default:
                    return (null, IncompatibleSpec("number", spec, posStart, posEnd, context));
            }
        }

        private static (string? Text, Error? Error) FormatPercent(double value, FormatSpec spec)
        {
            int precision = spec.HasPrecision ? spec.Precision : 0;
            string fmt = "F" + precision;
            string text = (value * 100).ToString(fmt, CultureInfo.InvariantCulture);
            return (text + "%", null);
        }

        private static string FormatNullForSpec(FormatSpec spec)
        {
            // `null` falls through to the same printable form the default
            // interpolation path uses, so `${maybe:.2f}` doesn't crash when
            // the expression resolves to null mid-format. The diagnostic for
            // "should not be null" lives on the consuming expression, not on
            // the format directive.
            return "null";
        }

        private static Error IncompatibleSpec(string typeLabel, FormatSpec spec, Position posStart, Position posEnd, Context context)
        {
            string suggestion = spec.Kind switch
            {
                FormatKind.Float       => "use ':d' or ':#x' for integers, drop the spec to render with the default form",
                FormatKind.Hex         => "':#x' only formats integral values; use ':.2f' or ':e' for floating-point",
                FormatKind.Binary      => "':#b' only formats integral values; use ':d' for decimal output",
                FormatKind.Octal       => "':#o' only formats integral values",
                FormatKind.Percent     => "':%' expects a numeric value",
                FormatKind.Exponential => "':e' / ':E' expect a numeric value",
                FormatKind.General     => "':g' / ':G' expect a numeric value",
                FormatKind.Decimal     => "':d' expects an integral value",
                _                      => "drop the format spec, or use one compatible with this value's type",
            };

            return new RuntimeError(posStart, posEnd,
                $"format specifier ':{spec}' is not compatible with values of type '{typeLabel}'",
                context,
                code: DiagnosticCode.RuntimeTypeMismatch,
                primaryLabel: $"cannot apply ':{spec}' here",
                help: suggestion);
        }
    }
}

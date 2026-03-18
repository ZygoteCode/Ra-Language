using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class Int128Value : RuntimeValue
    {
        public Int128 Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.Int128;
        public override bool IsCopy => true;

        public Int128Value(Int128 value)
        {
            Value = value;
        }

        public static Int128Value FromLiteral(string literal)
        {
            return new Int128Value(ParseLiteralToInt128(literal));
        }

        public static Int128Value? TryParseLiteral(string literal)
        {
            try
            {
                return new Int128Value(ParseLiteralToInt128(literal));
            }
            catch
            {
                return null;
            }
        }

        public static Int128Value FromBigInteger(System.Numerics.BigInteger value)
        {
            if (value < Int128.MinValue || value > Int128.MaxValue)
            {
                throw new OverflowException("Integer literal out of int128 range");
            }

            return new Int128Value((Int128)value);
        }

        private static Int128 ParseLiteralToInt128(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 16);

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 2);

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 8);

            return Int128.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static Int128 ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
                return (Int128)0;

            bool negative = false;
            if (digits.StartsWith("-"))
            {
                negative = true;
                digits = digits.Substring(1);
            }

            checked
            {
                Int128 result = 0;
                foreach (char c in digits)
                {
                    int d = c switch
                    {
                        >= '0' and <= '9' => c - '0',
                        >= 'a' and <= 'f' => 10 + (c - 'a'),
                        >= 'A' and <= 'F' => 10 + (c - 'A'),
                        _ => -1
                    };

                    if (d < 0 || d >= numberBase)
                        throw new FormatException("Invalid int128 literal");

                    result = (result * numberBase) + d;
                }

                return negative ? -result : result;
            }
        }

        private static bool IsWholeFloat(float f) => MathF.Abs(f - MathF.Truncate(f)) <= 0.000001f;
        private static bool IsWholeDouble(double d) => Math.Abs(d - Math.Truncate(d)) <= 0.000001d;

        private static bool TryAsInt128(RuntimeValue v, out Int128 result)
        {
            result = 0;

            switch (v.Type)
            {
                case RuntimeValueType.Int128:
                    result = ((Int128Value)v).Value;
                    return true;
                case RuntimeValueType.UnsignedInt128:
                    result = (Int128)((UnsignedInt128Value)v).Value;
                    return true;
                case RuntimeValueType.Decimal:
                    result = (Int128)((DecimalValue)v).Value;
                    return true;
                case RuntimeValueType.Short:
                    result = ((ShortValue)v).Value;
                    return true;
                case RuntimeValueType.UnsignedShort:
                    result = ((UnsignedShortValue)v).Value;
                    return true;
                case RuntimeValueType.Integer:
                    result = ((IntegerValue)v).Value;
                    return true;
                case RuntimeValueType.UnsignedInteger:
                    result = ((UnsignedIntegerValue)v).Value;
                    return true;
                case RuntimeValueType.Long:
                    result = ((LongValue)v).Value;
                    return true;
                case RuntimeValueType.UnsignedLong:
                    result = (Int128)((UnsignedLongValue)v).Value;
                    return true;
                case RuntimeValueType.Float:
                    {
                        float f = ((FloatValue)v).Value;
                        if (!IsWholeFloat(f)) return false;
                        result = (Int128)f;
                        return true;
                    }
                case RuntimeValueType.Double:
                    {
                        double d = ((DoubleValue)v).Value;
                        if (!IsWholeDouble(d)) return false;
                        result = (Int128)d;
                        return true;
                    }
                case RuntimeValueType.Number:
                    {
                        var s = ((NumberValue)v).Value.ToString();
                        return Int128.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                    }
                default:
                    return false;
            }
        }

        private NumberValue PromoteToNumber() => new NumberValue(BigNumber.Parse(Value.ToString()));

        private RuntimeValue PromoteIntegralResult(Int128 value)
        {
            if (value >= short.MinValue && value <= short.MaxValue)
                return new ShortValue((short)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value >= int.MinValue && value <= int.MaxValue)
                return new IntegerValue((int)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value >= long.MinValue && value <= long.MaxValue)
                return new LongValue((long)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            return new Int128Value(value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        private RuntimeValue PromoteUnsignedResult(UInt128 value)
        {
            if (value <= ushort.MaxValue)
                return new UnsignedShortValue((ushort)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value <= uint.MaxValue)
                return new UnsignedIntegerValue((uint)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value <= ulong.MaxValue)
                return new UnsignedLongValue((ulong)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value <= UInt128.MaxValue)
                return new Int128Value((Int128)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            return new DoubleValue((double)value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult(Value + ((Int128Value)other).Value), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value + (double)((Int128Value)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (PromoteIntegralResult(Value + rhs), null);
                        }
                    }
                    catch
                    {
                        return (new DoubleValue((double)Value + (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult(Value - ((Int128Value)other).Value), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value - (double)((Int128Value)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (PromoteIntegralResult(Value - rhs), null);
                        }
                    }
                    catch
                    {
                        return (new DoubleValue((double)Value - (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult(Value * ((Int128Value)other).Value), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value * (double)((Int128Value)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (PromoteIntegralResult(Value * rhs), null);
                        }
                    }
                    catch
                    {
                        return (new DoubleValue((double)Value * (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                var rhs = ((Int128Value)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (PromoteIntegralResult(Value / rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                    return (PromoteIntegralResult(Value / rhs), null);
                }
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var rhs = ((FloatValue)other).Value;
                if (rhs == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new FloatValue((float)Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var rhs = ((DoubleValue)other).Value;
                if (rhs == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DoubleValue((double)Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128 && ((Int128Value)other).Value >= 0 && ((Int128Value)other).Value <= int.MaxValue)
            {
                try
                {
                    checked
                    {
                        Int128 exp = ((Int128Value)other).Value;
                        Int128 result = 1;
                        Int128 baseVal = Value;

                        while (exp > 0)
                        {
                            if ((exp & 1) == 1)
                                result *= baseVal;

                            exp >>= 1;
                            if (exp > 0)
                                baseVal *= baseVal;
                        }

                        return (PromoteIntegralResult(result), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(Math.Pow((double)Value, (double)((Int128Value)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    if (rhs < 0)
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));

                    try
                    {
                        checked
                        {
                            Int128 exp = rhs;
                            Int128 result = 1;
                            Int128 baseVal = Value;

                            while (exp > 0)
                            {
                                if ((exp & 1) == 1)
                                    result *= baseVal;

                                exp >>= 1;
                                if (exp > 0)
                                    baseVal *= baseVal;
                            }

                            return (PromoteIntegralResult(result), null);
                        }
                    }
                    catch
                    {
                        return (new DoubleValue(Math.Pow((double)Value, (double)rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(MathF.Pow((float)Value, ((FloatValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Math.Pow((double)Value, ((DoubleValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().PowedBy(other);

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                var rhs = ((Int128Value)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (PromoteIntegralResult(Value % rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                {
                    if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                    return (PromoteIntegralResult(Value % rhs), null);
                }
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var rhs = ((FloatValue)other).Value;
                if (rhs == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new FloatValue((float)Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var rhs = ((DoubleValue)other).Value;
                if (rhs == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DoubleValue((double)Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().ModuledBy(other);

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128 && ((Int128Value)other).Value == Value)
                return (new BooleanValue(true).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                    return (new BooleanValue(Value == rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonEq(other);

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
                return (new BooleanValue(Value.ToString() == ((StringValue)other).Value).SetContext(Context), null);

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b) return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128 && ((Int128Value)other).Value == Value)
                return (new BooleanValue(false).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                    return (new BooleanValue(Value < rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonLt(other);

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128 && ((Int128Value)other).Value == Value)
                return (new BooleanValue(false).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal)
            {
                if (TryAsInt128(other, out var rhs))
                    return (new BooleanValue(Value > rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonGt(other);

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            var gt = GetComparisonGt(other).Item1;
            if (gt is BooleanValue b) return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            var lt = GetComparisonLt(other).Item1;
            if (lt is BooleanValue b) return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "decimal", StringComparison.Ordinal) ||
                string.Equals(tn, "f128", StringComparison.Ordinal))
            {
                return (new DecimalValue((decimal)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal) ||
                string.Equals(tn, "i128", StringComparison.Ordinal) ||
                string.Equals(tn, "integer128", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal) ||
                string.Equals(tn, "ui128", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger128", StringComparison.Ordinal))
            {
                if ((UInt128) Value < UInt128.MinValue || (UInt128)Value > UInt128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to uint128 without overflow", Context));

                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                if (Value < short.MinValue || Value > short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal) ||
                string.Equals(tn, "ui16", StringComparison.Ordinal) ||
                string.Equals(tn, "uint16", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedshort", StringComparison.Ordinal))
            {
                if (Value < 0 || Value > ushort.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to ushort without overflow", Context));

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                if (Value < int.MinValue || Value > int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to int without overflow", Context));

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger", StringComparison.Ordinal) ||
                string.Equals(tn, "ui32", StringComparison.Ordinal))
            {
                if (Value < 0 || Value > uint.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to uint without overflow", Context));

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (Value < long.MinValue || Value > long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to long without overflow", Context));

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedlong", StringComparison.Ordinal) ||
                string.Equals(tn, "ui64", StringComparison.Ordinal))
            {
                if (Value < 0 || Value > (Int128)ulong.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to ulong without overflow", Context));

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal) ||
                string.Equals(tn, "f32", StringComparison.Ordinal))
            {
                return (new FloatValue((float)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal) ||
                string.Equals(tn, "f64", StringComparison.Ordinal))
            {
                return (new DoubleValue((double)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.Ordinal) || string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(Value != 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new Int128Value(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString();
    }
}
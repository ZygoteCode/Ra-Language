using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class UnsignedLongValue : RuntimeValue
    {
        public ulong Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.UnsignedLong;
        public override bool IsCopy => true;

        public UnsignedLongValue(ulong value)
        {
            Value = value;
        }

        public static UnsignedLongValue FromLiteral(string literal)
        {
            return new UnsignedLongValue(ParseLiteralToULong(literal));
        }

        public static UnsignedLongValue FromBigInteger(System.Numerics.BigInteger value)
        {
            if (value < uint.MinValue || value > uint.MaxValue)
            {
                throw new OverflowException("Integer literal out of int range");
            }

            return new UnsignedLongValue((uint)value);
        }

        public static UnsignedLongValue? TryParseLiteral(string literal)
        {
            try
            {
                return new UnsignedLongValue(ParseLiteralToULong(literal));
            }
            catch
            {
                return null;
            }
        }

        private static ulong ParseLiteralToULong(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
                throw new FormatException("Unsigned long cannot be negative");

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 16);

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 2);

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 8);

            return ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static ulong ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
                return 0UL;

            checked
            {
                ulong result = 0UL;

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
                        throw new FormatException("Invalid ulong literal");

                    result = (result * (ulong)numberBase) + (ulong)d;
                }

                return result;
            }
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString()));
        }

        private static bool TryAsDecimal(RuntimeValue value, out decimal result)
        {
            result = 0m;

            switch (value.Type)
            {
                case RuntimeValueType.UnsignedLong:
                    result = ((UnsignedLongValue)value).Value;
                    return true;
                case RuntimeValueType.UnsignedInteger:
                    result = ((UnsignedIntegerValue)value).Value;
                    return true;
                case RuntimeValueType.Integer:
                    result = (ulong)((IntegerValue)value).Value;
                    return true;
                case RuntimeValueType.Long:
                    result = (ulong)((LongValue)value).Value;
                    return true;
                case RuntimeValueType.Float:
                    result = (ulong)((FloatValue)value).Value;
                    return true;
                case RuntimeValueType.Double:
                    result = (ulong)((DoubleValue)value).Value;
                    return true;
                case RuntimeValueType.Short:
                    result = (ulong)((ShortValue)value).Value;
                    return true;
                case RuntimeValueType.UnsignedShort:
                    result = ((UnsignedShortValue)value).Value;
                    return true;
                case RuntimeValueType.Int128:
                    result = (ulong)((Int128Value)value).Value;
                    return true;
                default:
                    return false;
            }
        }

        private RuntimeValue FromDecimalResult(decimal value, bool allowUnsigned = true)
        {
            if (value == decimal.Truncate(value))
            {
                if (value >= 0m && value <= ulong.MaxValue && allowUnsigned)
                    return new UnsignedLongValue((ulong)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

                if (value >= long.MinValue && value <= long.MaxValue)
                    return new LongValue((long)value).SetContext(Context).SetPos(PositionStart, PositionEnd);
            }

            return new DoubleValue((double)value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        private static ulong ToUlongChecked(RuntimeValue value)
        {
            return value.Type switch
            {
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)value).Value,
                RuntimeValueType.UnsignedInt128 => (ulong)((UnsignedInt128Value)value).Value,
                RuntimeValueType.Int128 => ((Int128Value)value).Value < 0 ? throw new OverflowException() : (ulong)((Int128Value)value).Value,
                RuntimeValueType.Short => ((ShortValue)value).Value < 0 ? throw new OverflowException() : (ulong)((ShortValue)value).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)value).Value,
                RuntimeValueType.Integer => ((IntegerValue)value).Value < 0 ? throw new OverflowException() : (ulong)((IntegerValue)value).Value,
                RuntimeValueType.Long => ((LongValue)value).Value < 0 ? throw new OverflowException() : (ulong)((LongValue)value).Value,
                _ => throw new InvalidOperationException()
            };
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (FromDecimalResult((decimal)Value + rhs), null);
                        }
                    }
                    catch
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned long overflow", Context));
                    }
                }
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (FromDecimalResult((decimal)Value - rhs), null);
                        }
                    }
                    catch
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned long overflow", Context));
                    }
                }
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                {
                    try
                    {
                        checked
                        {
                            return (FromDecimalResult((decimal)Value * rhs), null);
                        }
                    }
                    catch
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned long overflow", Context));
                    }
                }
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
            {
                var d = ((FloatValue)other).Value;
                if (d == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new FloatValue(Value / d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = ((DoubleValue)other).Value;
                if (d == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DoubleValue(Value / d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                {
                    if (rhs == 0m)
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));

                    decimal q = (decimal)Value / rhs;
                    return (FromDecimalResult(q), null);
                }
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)MathF.Pow(Value, ((FloatValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Math.Pow(Value, ((DoubleValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().PowedBy(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (!TryAsDecimal(other, out var rhs))
                    return base.PowedBy(other);

                if (rhs < 0m)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));

                double result = Math.Pow((double)Value, (double)rhs);
                if (double.IsNaN(result) || double.IsInfinity(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned long overflow", Context));

                if (Math.Abs(result - Math.Truncate(result)) <= 0.000001d && result >= 0d && result <= ulong.MaxValue)
                    return (new UnsignedLongValue((ulong)result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
            {
                var d = ((FloatValue)other).Value;
                if (d == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new FloatValue(Value % d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = ((DoubleValue)other).Value;
                if (d == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DoubleValue(Value % d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().ModuledBy(other);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (!TryAsDecimal(other, out var rhs))
                    return base.ModuledBy(other);

                if (rhs == 0m)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (FromDecimalResult((decimal)Value % rhs), null);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            try
            {
                ulong shift = ToUlongChecked(other);
                if (shift > 63) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid shift amount", Context));
                return (new UnsignedLongValue(Value << (int)shift).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            catch
            {
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Bitwise shift requires non-negative integer-like value", Context));
            }
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            try
            {
                ulong shift = ToUlongChecked(other);
                if (shift > 63) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid shift amount", Context));
                return (new UnsignedLongValue(Value >> (int)shift).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            catch
            {
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Bitwise shift requires non-negative integer-like value", Context));
            }
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            try
            {
                ulong rhs = ToUlongChecked(other);
                return (new UnsignedLongValue(Value & rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            catch
            {
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Bitwise operations require non-negative integer-like values", Context));
            }
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            try
            {
                ulong rhs = ToUlongChecked(other);
                return (new UnsignedLongValue(Value | rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            catch
            {
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Bitwise operations require non-negative integer-like values", Context));
            }
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new UnsignedLongValue(Value == 0 ? 1UL : 0UL).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new UnsignedLongValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            try
            {
                checked
                {
                    ulong factorial = 1UL;
                    for (ulong i = 2UL; i <= Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new UnsignedLongValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned long overflow", Context));
            }
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonEq(other);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((double)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                    return (new BooleanValue((decimal)Value == rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
                return (new BooleanValue(((BooleanValue)other).Value ? Value == 1UL : Value == 0UL).SetContext(Context), null);

            if (other.Type == RuntimeValueType.String)
                return (new BooleanValue(Value.ToString() == ((StringValue)other).Value).SetContext(Context), null);

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonLt(other);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((double)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                    return (new BooleanValue((decimal)Value < rhs).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonGt(other);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((double)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                    return (new BooleanValue((decimal)Value > rhs).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonLte(other);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((double)Value <= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value <= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                    return (new BooleanValue((decimal)Value <= rhs).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonGte(other);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((double)Value >= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value >= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128)
            {
                if (TryAsDecimal(other, out var rhs))
                    return (new BooleanValue((decimal)Value >= rhs).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "int128", StringComparison.Ordinal) ||
                string.Equals(tn, "i128", StringComparison.Ordinal) ||
                string.Equals(tn, "integer128", StringComparison.Ordinal))
            {
                if ((Int128)Value > Int128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to int128 without overflow", Context));

                return (new Int128Value((Int128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal) ||
                string.Equals(tn, "ui128", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger128", StringComparison.Ordinal))
            {
                if ((UInt128)Value > UInt128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to uint128 without overflow", Context));

                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                if ((short) Value > short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedshort", StringComparison.Ordinal) ||
                string.Equals(tn, "ui16", StringComparison.Ordinal) ||
                string.Equals(tn, "uint16", StringComparison.Ordinal))
            {
                if (Value > ushort.MaxValue || Value < ushort.MinValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to ushort without overflow", Context));

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedlong", StringComparison.Ordinal) ||
                string.Equals(tn, "ui64", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                if (Value > int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to int without overflow", Context));

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger", StringComparison.Ordinal) ||
                string.Equals(tn, "ui32", StringComparison.Ordinal))
            {
                if (Value > uint.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to uint without overflow", Context));

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (Value > long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to long without overflow", Context));

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal) ||
                string.Equals(tn, "f32", StringComparison.Ordinal))
            {
                return (new FloatValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal) ||
                string.Equals(tn, "f64", StringComparison.Ordinal))
            {
                return (new DoubleValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.Ordinal) ||
                string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(Value != 0UL).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new UnsignedLongValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0UL;

        public override string ToString() => Value.ToString();
    }
}
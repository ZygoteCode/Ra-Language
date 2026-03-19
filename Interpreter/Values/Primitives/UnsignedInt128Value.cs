using System;
using System.Globalization;
using System.Numerics;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class UnsignedInt128Value : RuntimeValue
    {
        public UInt128 Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.UnsignedInt128;
        public override bool IsCopy => true;

        public UnsignedInt128Value(UInt128 value)
        {
            Value = value;
        }

        public static UnsignedInt128Value FromLiteral(string literal)
        {
            return new UnsignedInt128Value(ParseLiteralToUInt128(literal));
        }

        public static UnsignedInt128Value? TryParseLiteral(string literal)
        {
            try
            {
                return new UnsignedInt128Value(ParseLiteralToUInt128(literal));
            }
            catch
            {
                return null;
            }
        }

        private static UInt128 ParseLiteralToUInt128(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
                throw new FormatException("Unsigned int128 cannot be negative");

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 16);

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 2);

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return ParseWithBase(s.Substring(2), 8);

            if (!UInt128.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                throw new FormatException("Invalid uint128 literal");

            return result;
        }

        private static UInt128 ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
                return 0;

            checked
            {
                UInt128 result = 0;
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
                        throw new FormatException("Invalid uint128 literal");

                    result = (result * (UInt128)numberBase) + (UInt128)d;
                }

                return result;
            }
        }

        private static BigInteger ToBigInteger(RuntimeValue value)
        {
            return value.Type switch
            {
                RuntimeValueType.UnsignedInt128 => BigInteger.Parse(((UnsignedInt128Value)value).Value.ToString()),
                RuntimeValueType.UnsignedLong => new BigInteger(((UnsignedLongValue)value).Value),
                RuntimeValueType.UnsignedInteger => new BigInteger(((UnsignedIntegerValue)value).Value),
                RuntimeValueType.UnsignedShort => new BigInteger(((UnsignedShortValue)value).Value),
                RuntimeValueType.Int128 => BigInteger.Parse(((Int128Value)value).Value.ToString()),
                RuntimeValueType.Long => new BigInteger(((LongValue)value).Value),
                RuntimeValueType.Integer => new BigInteger(((IntegerValue)value).Value),
                RuntimeValueType.Short => new BigInteger(((ShortValue)value).Value),
                RuntimeValueType.Byte => new BigInteger(((ByteValue)value).Value),
                _ => throw new InvalidOperationException("Value is not integral")
            };
        }

        private static bool IsWholeFloat(float f) => MathF.Abs(f - MathF.Truncate(f)) <= 0.000001f;
        private static bool IsWholeDouble(double d) => Math.Abs(d - Math.Truncate(d)) <= 0.000001d;

        private RuntimeValue PromoteBigInteger(BigInteger value)
        {
            if (value >= 0 && UInt128.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
            {
                return new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd);
            }

            if (value >= Int128.MinValue && value <= Int128.MaxValue &&
                Int128.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                return new Int128Value(i).SetContext(Context).SetPos(PositionStart, PositionEnd);
            }

            return new NumberValue(BigNumber.Parse(value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        private RuntimeValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString()));
        }

        private static bool TryAsBigInteger(RuntimeValue other, out BigInteger value)
        {
            value = 0;
            try
            {
                value = ToBigInteger(other);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue((decimal)Value + ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                try
                {
                    checked
                    {
                        return (PromoteBigInteger((BigInteger.Parse(Value.ToString()) + rhs)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value + (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue((decimal)Value - ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                try
                {
                    checked
                    {
                        return (PromoteBigInteger((BigInteger.Parse(Value.ToString()) - rhs)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value - (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue((float)Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue((double)Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue((decimal)Value * ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                try
                {
                    checked
                    {
                        return (PromoteBigInteger((BigInteger.Parse(Value.ToString()) * rhs)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue((double)Value * (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                var rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DecimalValue((decimal)Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            if (TryAsBigInteger(other, out var rhsInt))
            {
                if (rhsInt == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) / rhsInt), null);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(MathF.Pow((float)Value, ((FloatValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Math.Pow((double)Value, ((DoubleValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue((decimal) Math.Pow((double)Value, (double) ((DecimalValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().PowedBy(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                if (rhs < 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));

                try
                {
                    checked
                    {
                        BigInteger result = 1;
                        BigInteger baseVal = BigInteger.Parse(Value.ToString());
                        BigInteger exp = rhs;

                        while (exp > 0)
                        {
                            if ((exp & 1) == 1)
                                result *= baseVal;

                            exp >>= 1;
                            if (exp > 0)
                                baseVal *= baseVal;
                        }

                        return (PromoteBigInteger(result), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(Math.Pow((double)Value, (double)rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                var rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DecimalValue((decimal)Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().ModuledBy(other);

            if (TryAsBigInteger(other, out var rhsInt))
            {
                if (rhsInt == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) % rhsInt), null);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double || other.Type == RuntimeValueType.Number)
                return base.BitwiseLeftShiftedBy(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                if (rhs < 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Shift amount cannot be negative", Context));
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) << (int)rhs), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double || other.Type == RuntimeValueType.Number)
                return base.BitwiseRightShiftedBy(other);

            if (TryAsBigInteger(other, out var rhs))
            {
                if (rhs < 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Shift amount cannot be negative", Context));
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) >> (int)rhs), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double || other.Type == RuntimeValueType.Number)
                return base.BitwiseAndedBy(other);

            if (TryAsBigInteger(other, out var rhs))
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) & rhs), null);

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double || other.Type == RuntimeValueType.Number)
                return base.BitwiseOredBy(other);

            if (TryAsBigInteger(other, out var rhs))
                return (PromoteBigInteger(BigInteger.Parse(Value.ToString()) | rhs), null);

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new UnsignedInt128Value(Value == 0 ? (UInt128)1 : (UInt128)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new UnsignedInt128Value(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            try
            {
                checked
                {
                    UInt128 factorial = 1;
                    for (UInt128 i = 2; i <= Value; i++)
                        factorial *= i;

                    return (new UnsignedInt128Value(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (new NumberValue(BigNumber.Parse(((BigInteger)Value).ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value == ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonEq(other);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.Byte)
            {
                if (TryAsBigInteger(other, out var rhs))
                    return (new BooleanValue((BigInteger)Value == rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
                return (new BooleanValue(Value.ToString() == ((StringValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedInt128)
                return (new BooleanValue(Value == ((UnsignedInt128Value)other).Value).SetContext(Context), null);

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
            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value < ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonLt(other);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128 ||
                other.Type == RuntimeValueType.Byte)
            {
                if (TryAsBigInteger(other, out var rhs))
                    return (new BooleanValue((BigInteger)Value < rhs).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value > ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().GetComparisonGt(other);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128 ||
                other.Type == RuntimeValueType.Byte)
            {
                if (TryAsBigInteger(other, out var rhs))
                    return (new BooleanValue((BigInteger)Value > rhs).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            var gt = GetComparisonGt(other).Item1;
            if (gt is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            var lt = GetComparisonLt(other).Item1;
            if (lt is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (Value > byte.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to byte without overflow", Context));

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal) ||
                string.Equals(tn, "f128", StringComparison.Ordinal))
            {
                return (new DecimalValue((decimal)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal) ||
                string.Equals(tn, "ui128", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger128", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal) ||
                string.Equals(tn, "i128", StringComparison.Ordinal) ||
                string.Equals(tn, "integer128", StringComparison.Ordinal))
            {
                if (Value > (UInt128)Int128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to int128 without overflow", Context));

                if (!Int128.TryParse(Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to int128", Context));

                return (new Int128Value(i).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                if (Value > (UInt128)short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal) ||
                string.Equals(tn, "ui16", StringComparison.Ordinal) ||
                string.Equals(tn, "uint16", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedshort", StringComparison.Ordinal))
            {
                if (Value > ushort.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to ushort without overflow", Context));

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                if (Value > (UInt128)int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to int without overflow", Context));

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger", StringComparison.Ordinal) ||
                string.Equals(tn, "ui32", StringComparison.Ordinal))
            {
                if (Value > uint.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to uint without overflow", Context));

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (Value > (UInt128)long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to long without overflow", Context));

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedlong", StringComparison.Ordinal) ||
                string.Equals(tn, "ui64", StringComparison.Ordinal))
            {
                if (Value > ulong.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to ulong without overflow", Context));

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
            return new UnsignedInt128Value(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString();
    }
}
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class LongValue : RuntimeValue
    {
        public long Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Long;
        public sealed override bool IsCopy => true;

        public LongValue(long value)
        {
            Value = value;
        }

        public static LongValue FromLiteral(string literal)
        {
            return new LongValue(ParseLiteralToLong(literal));
        }

        public static LongValue? TryParseLiteral(string literal)
        {
            try
            {
                return new LongValue(ParseLiteralToLong(literal));
            }
            catch
            {
                return null;
            }
        }

        private static long ParseLiteralToLong(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
            {
                checked
                {
                    return -ParseLiteralToLong(s.Substring(1));
                }
            }

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 16);
            }

            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 2);
            }

            if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 8);
            }

            return long.Parse(s);
        }

        private static long ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
            {
                return 0L;
            }

            checked
            {
                long result = 0L;

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
                    {
                        throw new FormatException("Invalid long literal");
                    }

                    result = (result * numberBase) + d;
                }

                return result;
            }
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString()));
        }

        private static LongValue Promote(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                return new LongValue(((IntegerValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return new LongValue((long)((FloatValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return new LongValue((long)((DoubleValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Long)
            {
                return (LongValue)other;
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return new LongValue(((UnsignedIntegerValue)other).Value);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return new LongValue((long)((UnsignedLongValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return new LongValue((long)((ShortValue)other).Value);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return new LongValue((long)((UnsignedShortValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                return new LongValue((long)((Int128Value)other).Value);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return new LongValue((long)((UnsignedInt128Value)other).Value);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return new LongValue((long)((DecimalValue)other).Value);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return new LongValue((byte)((ByteValue)other).Value);
            }

            throw new InvalidOperationException("Cannot promote value to long");
        }

        public sealed override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                try
                {
                    checked
                    {
                        return (new LongValue(Value + o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Long overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            return base.AddedTo(other);
        }

        public sealed override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                try
                {
                    checked
                    {
                        return (new LongValue(Value - o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Long overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            return base.SubbedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                try
                {
                    checked
                    {
                        return (new LongValue(Value * o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Long overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            return base.MultedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);

                if (o.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new LongValue(Value / o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            return base.DivedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);

                if (o.Value < 0 || o.Value > int.MaxValue)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid exponent for long", Context));
                }

                try
                {
                    checked
                    {
                        long result = 1;
                        long baseVal = Value;
                        long exp = o.Value;

                        while (exp > 0)
                        {
                            if ((exp & 1) == 1)
                            {
                                result *= baseVal;
                            }

                            exp >>= 1;
                            if (exp > 0)
                            {
                                baseVal *= baseVal;
                            }
                        }

                        return (new LongValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Long overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            return base.PowedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);

                if (o.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new LongValue(Value % o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            return base.ModuledBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (new LongValue(Value << (int)o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer 
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (new LongValue(Value >> (int)o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (new LongValue(Value & o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (new LongValue(Value | o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseOredBy(other);
        }

        public sealed override (RuntimeValue?, Error?) Notted()
        {
            return (new LongValue(Value == 0 ? 1L : 0L).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new LongValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override (RuntimeValue?, Error?) Factorial()
        {
            if (Value < 0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative longs", Context));
            }

            try
            {
                checked
                {
                    long factorial = 1;
                    for (long i = 2; i <= Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new LongValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Long overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value == o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) == n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString() == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value != o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) != n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of(!(b.Value && Value == 1) & !(!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString() != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value < o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value > o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value <= o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                var o = Promote(other);
                return (BooleanValue.Of(Value >= o.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public sealed override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (Value < byte.MinValue || Value > byte.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to byte without overflow", Context));
                }

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (new DecimalValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                return (new Int128Value(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                if (Value < int.MinValue || Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to int without overflow", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (BooleanValue.Of(Value != 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal))
            {
                return (new FloatValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal))
            {
                return (new DoubleValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                if (Value < uint.MinValue || Value > uint.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to uint without overflow", Context));
                }

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                if ((ulong) Value < ulong.MinValue || (ulong) Value > ulong.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to ulong without overflow", Context));
                }

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (Value < short.MinValue || Value > short.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to short without overflow", Context));
                }

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                if (Value < ushort.MinValue || Value > ushort.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to ushort without overflow", Context));
                }

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            // Immutable primitive: sharing the same instance is safe and removes per-read allocations.
            return this;
        }

        public sealed override bool IsTrue() => Value != 0;

        public sealed override string ToString() => Value.ToString();
    }
}
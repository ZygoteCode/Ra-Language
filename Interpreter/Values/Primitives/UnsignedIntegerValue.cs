using System.Threading.Tasks;
using System;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class UnsignedIntegerValue : RuntimeValue
    {
        public uint Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.UnsignedInteger;
        public sealed override bool IsCopy => true;

        public UnsignedIntegerValue(uint value)
        {
            Value = value;
        }

        public static UnsignedIntegerValue FromLiteral(string literal)
        {
            return new UnsignedIntegerValue(ParseLiteralToUInt(literal));
        }

        public static UnsignedIntegerValue? TryParseLiteral(string literal)
        {
            try
            {
                return new UnsignedIntegerValue(ParseLiteralToUInt(literal));
            }
            catch
            {
                return null;
            }
        }

        public static UnsignedIntegerValue FromBigInteger(System.Numerics.BigInteger value)
        {
            if (value < uint.MinValue || value > uint.MaxValue)
            {
                throw new OverflowException("Integer literal out of int range");
            }

            return new UnsignedIntegerValue((uint)value);
        }

        private static uint ParseLiteralToUInt(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
            {
                throw new FormatException("Unsigned integer cannot be negative");
            }

            if (s.StartsWith("0x", StringComparison.Ordinal))
            {
                return ParseWithBase(s.Substring(2), 16);
            }

            if (s.StartsWith("0b", StringComparison.Ordinal))
            {
                return ParseWithBase(s.Substring(2), 2);
            }

            if (s.StartsWith("0o", StringComparison.Ordinal))
            {
                return ParseWithBase(s.Substring(2), 8);
            }

            return uint.Parse(s);
        }

        private static uint ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
            {
                return 0u;
            }

            checked
            {
                uint result = 0u;

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
                        throw new FormatException("Invalid uint literal");
                    }

                    result = (result * (uint)numberBase) + (uint)d;
                }

                return result;
            }
        }

        private LongValue PromoteToLong() => new LongValue(Value);
        private FloatValue PromoteToFloat() => new FloatValue(Value);
        private DoubleValue PromoteToDouble() => new DoubleValue(Value);
        private NumberValue PromoteToNumber() => new NumberValue(BigNumber.Parse(Value.ToString()));

        private static long AsLong(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (uint)((Int128Value)other).Value,
                RuntimeValueType.Byte => (uint)((ByteValue)other).Value,
                _ => throw new InvalidOperationException("Cannot convert runtime value to long")
            };
        }

        private static float AsFloat(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Float => ((FloatValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (uint)((Int128Value)other).Value,
                RuntimeValueType.Byte => (uint)((ByteValue)other).Value,
                _ => throw new InvalidOperationException("Cannot convert runtime value to float")
            };
        }

        private static double AsDouble(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Float => ((FloatValue)other).Value,
                RuntimeValueType.Double => ((DoubleValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (uint)((Int128Value)other).Value,
                RuntimeValueType.Byte => (uint)((ByteValue)other).Value,
                _ => throw new InvalidOperationException("Cannot convert runtime value to double")
            };
        }

        public sealed override ValueResult AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                try
                {
                    checked
                    {
                        return (new UnsignedIntegerValue(Value + o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long result = (long)Value + AsLong(other);
                return (new LongValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float result = AsFloat(this) + AsFloat(other);
                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double result = AsDouble(this) + AsDouble(other);
                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value + u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new ByteValue((byte)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                if (Value < o.Value)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Unsigned subtraction underflow", Context));
                }

                return (new UnsignedIntegerValue(Value - o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long result = (long)Value - AsLong(other);
                return (new LongValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float result = AsFloat(this) - AsFloat(other);
                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double result = AsDouble(this) - AsDouble(other);
                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new ByteValue((byte)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.SubbedBy(other);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                try
                {
                    checked
                    {
                        return (new UnsignedIntegerValue(Value * o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long result = (long)Value * AsLong(other);
                return (new LongValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float result = AsFloat(this) * AsFloat(other);
                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double result = AsDouble(this) * AsDouble(other);
                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new ByteValue((byte)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                if (o.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new UnsignedIntegerValue(Value / o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long divisor = AsLong(other);
                if (divisor == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                long result = (long)Value / divisor;
                return (new LongValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float divisor = AsFloat(other);
                if (divisor == 0f)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new FloatValue(AsFloat(this) / divisor).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double divisor = AsDouble(other);
                if (divisor == 0.0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new DoubleValue(AsDouble(this) / divisor).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new ByteValue((byte)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                try
                {
                    checked
                    {
                        uint result = 1;
                        uint baseVal = Value;
                        uint exp = o.Value;

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

                        return (new UnsignedIntegerValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long exp = AsLong(other);
                if (exp < 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));
                }

                return (new DoubleValue(Math.Pow(Value, exp)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new FloatValue(MathF.Pow(Value, AsFloat(other))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new DoubleValue(Math.Pow(Value, AsDouble(other))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value ^ u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value ^ u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value ^ u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value ^ u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value ^ u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (new DecimalValue((decimal) Math.Pow(Value, (double) ((DecimalValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (new ByteValue((byte)Math.Pow(Value, (double)((DecimalValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.PowedBy(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                if (o.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new UnsignedIntegerValue(Value % o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                long divisor = AsLong(other);
                if (divisor == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new LongValue((long)Value % divisor).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float divisor = AsFloat(other);
                if (divisor == 0f)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new FloatValue(AsFloat(this) % divisor).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double divisor = AsDouble(other);
                if (divisor == 0.0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new DoubleValue(AsDouble(this) % divisor).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue(Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new ByteValue((byte)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.ModuledBy(other);
        }

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue(Value << (int)o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new LongValue((long)Value << (int)AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value << u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value << u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue(Value >> (int)o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new LongValue((long)Value >> (int)AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value >> u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value >> u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue(Value & o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new LongValue((long)Value & AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value & u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value & u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue(Value | o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new LongValue((long)Value | AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new ShortValue((short)(Value | u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new UnsignedShortValue((ushort)(Value | u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseOredBy(other);
        }

        public sealed override ValueResult BitwiseXoredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var o = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue(Value ^ o.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseXoredBy(other);
        }

        public sealed override ValueResult BitwiseUnsignedRightShiftedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 32, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new UnsignedIntegerValue(Value >> n).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseRotateLeftedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 32, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new UnsignedIntegerValue(System.Numerics.BitOperations.RotateLeft(Value, n)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseRotateRightedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 32, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new UnsignedIntegerValue(System.Numerics.BitOperations.RotateRight(Value, n)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult Notted()
        {
            return (new UnsignedIntegerValue(Value == 0 ? 1u : 0u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseNotted()
        {
            return (new UnsignedIntegerValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult Factorial()
        {
            try
            {
                checked
                {
                    uint factorial = 1u;
                    for (uint i = 2u; i <= Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new UnsignedIntegerValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned integer overflow", Context));
            }
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value == ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value == ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value == ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value == ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value == ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value == ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value == AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) == AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) == AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (BooleanValue.Of(PromoteToNumber().GetComparisonEq(other).Item1?.IsTrue() == true).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of((b.Value && Value == 1u) || (!b.Value && Value == 0u)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString() == s.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value == ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value == ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value != ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value != ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value != ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value != ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value != ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value != ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value != AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) != AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) != AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (BooleanValue.Of(PromoteToNumber().GetComparisonNe(other).Item1?.IsTrue() == true).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of(!(b.Value && Value == 1u) & !(!b.Value && Value == 0u)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString() != s.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value != ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value != ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value < ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value < ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value < ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value < ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value < ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value < ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value < AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) < AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) < AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value < ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value < ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public sealed override ValueResult GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value > ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value > ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value > ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value > ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value > ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value > ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value > AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) > AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) > AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value > ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value > ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public sealed override ValueResult GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value <= ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value <= ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value <= ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value <= ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value <= ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value <= ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value <= AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) <= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) <= AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value <= ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value <= ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public sealed override ValueResult GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Int128)
            {
                return (BooleanValue.Of(Value >= ((Int128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (BooleanValue.Of(Value >= ((UnsignedInt128Value)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (BooleanValue.Of(Value >= ((UnsignedShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                return (BooleanValue.Of(Value >= ((ShortValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (BooleanValue.Of(Value >= ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (BooleanValue.Of(Value >= ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (BooleanValue.Of((long)Value >= AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (BooleanValue.Of(AsFloat(this) >= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (BooleanValue.Of(AsDouble(this) >= AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (BooleanValue.Of(Value >= ((DecimalValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value >= ((ByteValue)other).Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (Value > byte.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to byte without overflow", Context));
                }

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (new DecimalValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                return (new Int128Value((Int128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                if (Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to int without overflow", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                return (new LongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal))
            {
                return (new FloatValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal))
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

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (BooleanValue.Of(Value != 0u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            // Immutable primitive: sharing the same instance is safe and removes per-read allocations.
            return this;
        }

        public sealed override bool IsTrue() => Value != 0u;

        public sealed override string ToString() => Value.ToString();
    }
}
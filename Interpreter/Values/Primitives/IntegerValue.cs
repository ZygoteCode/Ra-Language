using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class IntegerValue : RuntimeValue
    {
        public int Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Integer;
        public sealed override bool IsCopy => true;

        public IntegerValue(int value)
        {
            Value = value;
        }

        public static IntegerValue FromBigInteger(System.Numerics.BigInteger value)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new OverflowException("Integer literal out of int range");
            }

            return new IntegerValue((int)value);
        }

        public static IntegerValue FromLiteral(string literal)
        {
            return new IntegerValue(ParseLiteralToInt(literal));
        }

        public static IntegerValue? TryParseLiteral(string literal)
        {
            try
            {
                return new IntegerValue(ParseLiteralToInt(literal));
            }
            catch
            {
                return null;
            }
        }

        private static int ParseLiteralToInt(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
            {
                checked
                {
                    return -ParseLiteralToInt(s.Substring(1));
                }
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

            return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static int ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
            {
                return 0;
            }

            checked
            {
                int result = 0;

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
                        throw new FormatException("Invalid integer literal");
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

        public sealed override ValueResult AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value + i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (new FloatValue(Value + f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (new DoubleValue(Value + d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint)Value + u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Value + u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue((Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue((Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)Value + u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue(Value + u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue((Value + u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value - i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (new FloatValue(Value - f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (new DoubleValue(Value - d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint)Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue(Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue(Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value - u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue(Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue(Value - u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.SubbedBy(other);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value * i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (new FloatValue(Value * f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (new DoubleValue(Value * d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint)Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue(Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue(Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value * u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue(Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue(Value * u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new IntegerValue(Value / i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (new FloatValue(Value / f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (new DoubleValue(Value / d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint)Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue(Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue(Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value / u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue(Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue(Value / u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value < 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed for int", Context));
                }

                try
                {
                    checked
                    {
                        int result = 1;
                        int baseVal = Value;
                        int exp = i.Value;

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

                        return (new IntegerValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (new FloatValue((float)Math.Pow(Value, (double)f.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (new DoubleValue(Math.Pow(Value, d.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint) Math.Pow(Value, u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Math.Pow(Value, u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue((int)Math.Pow(Value, u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue((int)Math.Pow(Value, u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)Math.Pow(Value, (double)u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)Math.Pow(Value, (double)u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)Math.Pow(Value, (double)u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue((int)Math.Pow(Value, u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.PowedBy(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new IntegerValue(Value % i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (new UnsignedIntegerValue((uint)Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (new UnsignedLongValue((ulong)Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (new IntegerValue(Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (new IntegerValue(Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (new Int128Value((Int128)(Value % u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (new UnsignedInt128Value((UInt128)((Int128)Value % (Int128)u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (new DecimalValue((decimal)((Int128)Value % (Int128)u.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (new IntegerValue(Value % u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.ModuledBy(other);
        }

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value << i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value >> i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value & i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value | i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseOredBy(other);
        }

        public sealed override ValueResult Notted()
        {
            return (new IntegerValue(Value == 0 ? 1 : 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseNotted()
        {
            return (new IntegerValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult Factorial()
        {
            if (Value < 0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative integers", Context));
            }

            try
            {
                checked
                {
                    int factorial = 1;
                    for (int i = 2; i <= Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new IntegerValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
            }
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value == i.Value).SetContext(Context), null);
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

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value == f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value == d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value == u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value == u.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value != i.Value).SetContext(Context), null);
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

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value != f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value != d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value != u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value != u.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value < i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value < f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value < d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value < u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value < u.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public sealed override ValueResult GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value > i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value > f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value > d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value > u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value > u.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public sealed override ValueResult GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value <= i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value <= f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value <= d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value <= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value <= u.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public sealed override ValueResult GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (BooleanValue.Of(Value >= i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString()) >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                return (BooleanValue.Of(Value >= f.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                return (BooleanValue.Of(Value >= d.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                var u = (UnsignedIntegerValue)other;
                return (BooleanValue.Of(Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                var u = (UnsignedLongValue)other;
                return (BooleanValue.Of((ulong)Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                var u = (ShortValue)other;
                return (BooleanValue.Of((short)Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var u = (UnsignedShortValue)other;
                return (BooleanValue.Of((ushort)Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                var u = (Int128Value)other;
                return (BooleanValue.Of((Int128)Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                var u = (UnsignedInt128Value)other;
                return (BooleanValue.Of((UInt128)Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                var u = (DecimalValue)other;
                return (BooleanValue.Of(Value >= u.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                var u = (ByteValue)other;
                return (BooleanValue.Of((byte)Value >= u.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
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

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            // IntegerValue is immutable; sharing the same instance is semantically identical
            // to returning a clone. Eliminates an allocation per integer variable read.
            return this;
        }

        public sealed override bool IsTrue() => Value != 0;

        public sealed override string ToString() => Value.ToString();
    }
}

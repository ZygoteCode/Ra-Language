using System;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class UnsignedIntegerValue : RuntimeValue
    {
        public uint Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.UnsignedInteger;
        public override bool IsCopy => true;

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
                _ => throw new InvalidOperationException("Cannot convert runtime value to double")
            };
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
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

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
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

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
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

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
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

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
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

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
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

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
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

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
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

            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
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

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
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

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new UnsignedIntegerValue(Value == 0 ? 1u : 0u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new UnsignedIntegerValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
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

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value == ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value == ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value == AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) == AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) == AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (new BooleanValue(PromoteToNumber().GetComparisonEq(other).Item1?.IsTrue() == true).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1u) || (!b.Value && Value == 0u)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString() == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value != ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value != ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value != AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) != AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) != AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (new BooleanValue(PromoteToNumber().GetComparisonNe(other).Item1?.IsTrue() == true).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue(!(b.Value && Value == 1u) & !(!b.Value && Value == 0u)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString() != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value < ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value < ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value < AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) < AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) < AsDouble(other)).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value > ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value > ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value > AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) > AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) > AsDouble(other)).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value <= ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value <= ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value <= AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) <= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) <= AsDouble(other)).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                return (new BooleanValue(Value >= ((UnsignedIntegerValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                return (new BooleanValue(Value >= ((UnsignedLongValue)other).Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue((long)Value >= AsLong(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new BooleanValue(AsFloat(this) >= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new BooleanValue(AsDouble(this) >= AsDouble(other)).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "uint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "unsignedinteger", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "ui32", StringComparison.OrdinalIgnoreCase))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i32", StringComparison.OrdinalIgnoreCase))
            {
                if (Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to int without overflow", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i64", StringComparison.OrdinalIgnoreCase))
            {
                return (new LongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "f32", StringComparison.OrdinalIgnoreCase))
            {
                return (new FloatValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "f64", StringComparison.OrdinalIgnoreCase))
            {
                return (new DoubleValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.OrdinalIgnoreCase))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.OrdinalIgnoreCase))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "bool", StringComparison.OrdinalIgnoreCase))
            {
                return (new BooleanValue(Value != 0u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new UnsignedIntegerValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0u;

        public override string ToString() => Value.ToString();
    }
}
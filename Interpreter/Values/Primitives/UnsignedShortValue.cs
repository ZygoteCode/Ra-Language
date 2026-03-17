using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class UnsignedShortValue : RuntimeValue
    {
        public ushort Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.UnsignedShort;
        public override bool IsCopy => true;

        public UnsignedShortValue(ushort value)
        {
            Value = value;
        }

        public static UnsignedShortValue FromLiteral(string literal)
        {
            return new UnsignedShortValue(ParseLiteralToUShort(literal));
        }

        public static UnsignedShortValue? TryParseLiteral(string literal)
        {
            try
            {
                return new UnsignedShortValue(ParseLiteralToUShort(literal));
            }
            catch
            {
                return null;
            }
        }

        private static ushort ParseLiteralToUShort(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
                throw new FormatException("Unsigned short cannot be negative");

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return checked((ushort)Convert.ToInt32(s.Substring(2), 16));

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return checked((ushort)Convert.ToInt32(s.Substring(2), 2));

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return checked((ushort)Convert.ToInt32(s.Substring(2), 8));

            return ushort.Parse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
        }

        private IntegerValue PromoteToInteger() => new IntegerValue(Value);
        private LongValue PromoteToLong() => new LongValue(Value);
        private FloatValue PromoteToFloat() => new FloatValue(Value);
        private DoubleValue PromoteToDouble() => new DoubleValue(Value);
        private NumberValue PromoteToNumber() => new NumberValue(BigNumber.Parse(Value.ToString()));

        private static long AsLong(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value > long.MaxValue
                    ? throw new OverflowException()
                    : (long)((UnsignedLongValue)other).Value,
                _ => throw new InvalidOperationException()
            };
        }

        private static float AsFloat(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value,
                RuntimeValueType.Float => ((FloatValue)other).Value,
                _ => throw new InvalidOperationException()
            };
        }

        private static double AsDouble(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value,
                RuntimeValueType.Float => ((FloatValue)other).Value,
                RuntimeValueType.Double => ((DoubleValue)other).Value,
                _ => throw new InvalidOperationException()
            };
        }

        private RuntimeValue PromoteIntegralResult(long value)
        {
            if (value >= 0 && value <= ushort.MaxValue)
                return new UnsignedShortValue((ushort)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value >= short.MinValue && value <= short.MaxValue)
                return new ShortValue((short)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value >= int.MinValue && value <= int.MaxValue)
                return new IntegerValue((int)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            return new LongValue(value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        private RuntimeValue PromoteUnsignedResult(ulong value)
        {
            if (value <= ushort.MaxValue)
                return new UnsignedShortValue((ushort)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value <= int.MaxValue)
                return new IntegerValue((int)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value <= long.MaxValue)
                return new LongValue((long)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            return new DoubleValue(value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                try
                {
                    checked
                    {
                        return (PromoteUnsignedResult((ulong)Value + ((UnsignedShortValue)other).Value), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult((long)Value + AsLong(other)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(AsDouble(this) + AsDouble(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (PromoteIntegralResult((long)Value - ((UnsignedShortValue)other).Value), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult((long)Value - AsLong(other)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(AsDouble(this) - AsDouble(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                try
                {
                    checked
                    {
                        return (PromoteUnsignedResult((ulong)Value * ((UnsignedShortValue)other).Value), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                try
                {
                    checked
                    {
                        return (PromoteIntegralResult((long)Value * AsLong(other)), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(AsDouble(this) * AsDouble(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var rhs = ((UnsignedShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (PromoteIntegralResult(Value / rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                long rhs = AsLong(other);
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (PromoteIntegralResult((long)Value / rhs), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float rhs = ((FloatValue)other).Value;
                if (rhs == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new FloatValue(Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double rhs = ((DoubleValue)other).Value;
                if (rhs == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DoubleValue(Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                int exp = ((UnsignedShortValue)other).Value;
                try
                {
                    checked
                    {
                        ulong result = 1;
                        ulong baseVal = Value;

                        while (exp > 0)
                        {
                            if ((exp & 1) == 1)
                                result *= baseVal;

                            exp >>= 1;
                            if (exp > 0)
                                baseVal *= baseVal;
                        }

                        return (PromoteUnsignedResult(result), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(Math.Pow(Value, ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                long exp = AsLong(other);
                if (exp < 0)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));

                double result = Math.Pow(Value, exp);
                if (double.IsNaN(result) || double.IsInfinity(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Power overflow", Context));

                return (PromoteIntegralResult((long)Math.Truncate(result)), null);
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(MathF.Pow(Value, ((FloatValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Math.Pow(Value, ((DoubleValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().PowedBy(other);

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var rhs = ((UnsignedShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (PromoteIntegralResult(Value % rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
            {
                long rhs = AsLong(other);
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (PromoteIntegralResult((long)Value % rhs), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                float rhs = ((FloatValue)other).Value;
                if (rhs == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new FloatValue(Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                double rhs = ((DoubleValue)other).Value;
                if (rhs == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DoubleValue(Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().ModuledBy(other);

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (PromoteUnsignedResult((ulong)Value << ((UnsignedShortValue)other).Value), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (PromoteIntegralResult((long)Value << (int)AsLong(other)), null);

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (PromoteUnsignedResult((ulong)Value >> ((UnsignedShortValue)other).Value), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (PromoteIntegralResult((long)Value >> (int)AsLong(other)), null);

            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new UnsignedShortValue((ushort)(Value & ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (PromoteIntegralResult((long)Value & AsLong(other)), null);

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new UnsignedShortValue((ushort)(Value | ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (PromoteIntegralResult((long)Value | AsLong(other)), null);

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new UnsignedShortValue(Value == 0 ? (ushort)1 : (ushort)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new UnsignedShortValue((ushort)~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            try
            {
                checked
                {
                    ulong result = 1UL;
                    for (ulong i = 2UL; i <= Value; i++)
                        result *= i;

                    return (PromoteUnsignedResult(result), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Unsigned short overflow", Context));
            }
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value == ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((long)Value == AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (new BooleanValue(PromoteToNumber().GetComparisonEq(other).Item1?.IsTrue() == true).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Boolean)
                return (new BooleanValue((((BooleanValue)other).Value && Value == 1) || (!((BooleanValue)other).Value && Value == 0)).SetContext(Context), null);

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
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value < ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((long)Value < AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value > ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((long)Value > AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value <= ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((long)Value <= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value <= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value <= ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value >= ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((long)Value >= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value >= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value >= ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "ushort", StringComparison.Ordinal) ||
                string.Equals(tn, "ui16", StringComparison.Ordinal) ||
                string.Equals(tn, "uint16", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedshort", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                if (Value > (ushort)short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                return (new IntegerValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger", StringComparison.Ordinal) ||
                string.Equals(tn, "ui32", StringComparison.Ordinal))
            {
                return (new UnsignedIntegerValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                return (new LongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedlong", StringComparison.Ordinal) ||
                string.Equals(tn, "ui64", StringComparison.Ordinal))
            {
                return (new UnsignedLongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
                return (new BooleanValue(Value != 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new UnsignedShortValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString();
    }
}
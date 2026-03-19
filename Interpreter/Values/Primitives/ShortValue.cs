using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ShortValue : RuntimeValue
    {
        public short Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.Short;
        public override bool IsCopy => true;

        public ShortValue(short value)
        {
            Value = value;
        }

        public static ShortValue FromLiteral(string literal)
        {
            return new ShortValue(ParseLiteralToShort(literal));
        }

        public static ShortValue? TryParseLiteral(string literal)
        {
            try
            {
                return new ShortValue(ParseLiteralToShort(literal));
            }
            catch
            {
                return null;
            }
        }

        private static short ParseLiteralToShort(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return checked((short)Convert.ToInt32(s.Substring(2), 16));

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return checked((short)Convert.ToInt32(s.Substring(2), 2));

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return checked((short)Convert.ToInt32(s.Substring(2), 8));

            return short.Parse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
        }

        private IntegerValue PromoteToInteger() => new IntegerValue(Value);

        private static short AsLong(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => (short)((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => (short)((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => (short)((LongValue)other).Value,
                RuntimeValueType.UnsignedLong => (short)((UnsignedLongValue)other).Value,
                RuntimeValueType.UnsignedShort => (short)((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (short)((Int128Value)other).Value,
                RuntimeValueType.UnsignedInt128 => (short)((UnsignedInt128Value)other).Value,
                RuntimeValueType.Decimal => (short)((DecimalValue)other).Value,
                RuntimeValueType.Byte => (short)((ByteValue)other).Value,
                _ => throw new InvalidOperationException()
            };
        }

        private RuntimeValue PromoteResult(long value)
        {
            if (value >= short.MinValue && value <= short.MaxValue)
                return new ShortValue((short)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (value >= int.MinValue && value <= int.MaxValue)
                return new IntegerValue((int)value).SetContext(Context).SetPos(PositionStart, PositionEnd);

            return new LongValue(value).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                try
                {
                    checked
                    {
                        return (PromoteResult((long)Value + ((ShortValue)other).Value), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal
                || other.Type == RuntimeValueType.Byte)
            {
                return (new LongValue((long)Value + AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new FloatValue(Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new DoubleValue(Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (new UnsignedShortValue((ushort)(Value + ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                return (new Int128Value((Int128)(Value + ((Int128Value)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().AddedTo(other);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                try
                {
                    checked
                    {
                        return (PromoteResult((long)Value - ((ShortValue)other).Value), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal
                || other.Type == RuntimeValueType.Byte)
            {
                return (new LongValue((long)Value - AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new FloatValue(Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new DoubleValue(Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (new UnsignedShortValue((ushort)(Value - ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                return (new Int128Value((Int128)(Value - ((Int128Value)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().SubbedBy(other);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                try
                {
                    checked
                    {
                        return (PromoteResult((long)Value * ((ShortValue)other).Value), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Decimal
                || other.Type == RuntimeValueType.Byte)
            {
                return (new LongValue((long)Value * AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new FloatValue(Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new DoubleValue(Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (new UnsignedShortValue((ushort)(Value * ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                return (new Int128Value((Int128)(Value * ((Int128Value)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().MultedBy(other);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                var rhs = ((ShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new ShortValue((short)(Value / rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
            {
                long rhs = AsLong(other);
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new LongValue((long)Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                decimal rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DecimalValue(Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().DivedBy(other);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                var exp = ((ShortValue)other).Value;
                if (exp < 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));

                try
                {
                    checked
                    {
                        long result = 1;
                        long baseVal = Value;
                        int e = exp;

                        while (e > 0)
                        {
                            if ((e & 1) == 1) result *= baseVal;
                            e >>= 1;
                            if (e > 0) baseVal *= baseVal;
                        }

                        return (PromoteResult(result), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Short overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
            {
                long exp = AsLong(other);
                if (exp < 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed", Context));
                return (new DoubleValue(Math.Pow(Value, exp)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                return (new FloatValue(MathF.Pow(Value, ((FloatValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                return (new DoubleValue(Math.Pow(Value, ((DoubleValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                return (new DecimalValue((decimal) Math.Pow(Value, (double) ((DecimalValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().PowedBy(other);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                var rhs = ((ShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new ShortValue((short)(Value % rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
            {
                long rhs = AsLong(other);
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new LongValue((long)Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                decimal rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DecimalValue(Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToInteger().ModuledBy(other);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                int shift = ((ShortValue)other).Value;
                return (new ShortValue((short)(Value << shift)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128)
            {
                return (new LongValue((long)Value << (int)AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
            {
                int shift = ((ShortValue)other).Value;
                return (new ShortValue((short)(Value >> shift)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
            {
                return (new LongValue((long)Value >> (int)AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
                return (new ShortValue((short)(Value & ((ShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128)
                return (new LongValue((long)Value & AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
                return (new ShortValue((short)(Value | ((ShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
                return (new LongValue((long)Value | AsLong(other)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new ShortValue(Value == 0 ? (short)1 : (short)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new ShortValue((short)~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            if (Value < 0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative shorts", Context));
            }

            try
            {
                checked
                {
                    long result = 1;
                    for (int i = 2; i <= Value; i++)
                    {
                        result *= i;
                    }
                    return (PromoteResult(result), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Short overflow", Context));
            }
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Byte)
                return (new BooleanValue((byte)Value == ((ByteValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value == ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedInt128)
                return (new BooleanValue((UInt128)Value == ((UnsignedInt128Value)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedLong)
                return (new BooleanValue((ulong) Value == ((UnsignedLongValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short)
                return (new BooleanValue(Value == ((ShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new BooleanValue(Value == ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Int128)
                return (new BooleanValue(Value == ((Int128Value)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long)
                return (new BooleanValue((long)Value == AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (new BooleanValue(PromoteToInteger().GetComparisonEq(other).Item1?.IsTrue() == true).SetContext(Context), null);

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
            if (other.Type == RuntimeValueType.Byte)
                return (new BooleanValue((byte)Value < ((ByteValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value < ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short)
                return (new BooleanValue(Value < ((ShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128)
                return (new BooleanValue((long)Value < AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
                return (new BooleanValue(Value > ((ShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
                return (new BooleanValue((long)Value > AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value > ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
                return (new BooleanValue(Value <= ((ShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
                return (new BooleanValue((long)Value <= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value <= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value <= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value <= ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Short)
                return (new BooleanValue(Value >= ((ShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Byte)
                return (new BooleanValue((long)Value >= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (new BooleanValue((float)Value >= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (new BooleanValue((double)Value >= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (new BooleanValue((decimal)Value >= ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal) ||
                string.Equals(tn, "f128", StringComparison.Ordinal))
            {
                return (new DecimalValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedinteger128", StringComparison.Ordinal) ||
                string.Equals(tn, "ui128", StringComparison.Ordinal))
            {
                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal) ||
                string.Equals(tn, "integer128", StringComparison.Ordinal) ||
                string.Equals(tn, "i128", StringComparison.Ordinal))
            {
                return (new Int128Value(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
                if (Value < 0)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative short to uint", Context));

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
                if (Value < 0)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative short to ulong", Context));

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
            return new ShortValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString();
    }
}
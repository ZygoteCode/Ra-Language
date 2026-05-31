using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;
using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class UnsignedShortValue : RuntimeValue
    {
        public ushort Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.UnsignedShort;
        public sealed override bool IsCopy => true;

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
                RuntimeValueType.Byte => ((ByteValue)other).Value,
                RuntimeValueType.Decimal => (ushort)((DecimalValue)other).Value,
                RuntimeValueType.UnsignedInt128 => (ushort)((UnsignedInt128Value)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Int128 => (ushort)((Int128Value)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value > long.MaxValue
                    ? throw new OverflowException()
                    : (long)((UnsignedLongValue)other).Value,
                _ => throw new InvalidOperationException()
            };
        }

        private static double AsDouble(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Byte => ((ByteValue)other).Value,
                RuntimeValueType.Decimal => (double) ((DecimalValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value,
                RuntimeValueType.Int128 => (double)((Int128Value)other).Value,
                RuntimeValueType.UnsignedInt128 => (double)((UnsignedInt128Value)other).Value,
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

        public sealed override ValueResult AddedTo(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue(Value + ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            return base.AddedTo(other);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                return (PromoteIntegralResult((long)Value - ((UnsignedShortValue)other).Value), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue(Value - ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            return base.SubbedBy(other);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue(Value * ((DecimalValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var rhs = ((UnsignedShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (PromoteIntegralResult(Value / rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                decimal rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DecimalValue(Value / rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
                return (new DecimalValue((decimal) Math.Pow(Value, (double) ((DecimalValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().PowedBy(other);

            return base.PowedBy(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                var rhs = ((UnsignedShortValue)other).Value;
                if (rhs == 0) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (PromoteIntegralResult(Value % rhs), null);
            }

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
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

            if (other.Type == RuntimeValueType.Decimal)
            {
                decimal rhs = ((DecimalValue)other).Value;
                if (rhs == 0m) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                return (new DecimalValue(Value % rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().ModuledBy(other);

            return base.ModuledBy(other);
        }

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (PromoteUnsignedResult((ulong)Value << ((UnsignedShortValue)other).Value), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.UnsignedLong)
                return (PromoteIntegralResult((long)Value << (int)AsLong(other)), null);

            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (PromoteUnsignedResult((ulong)Value >> ((UnsignedShortValue)other).Value), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (PromoteIntegralResult((long)Value >> (int)AsLong(other)), null);

            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new UnsignedShortValue((ushort)(Value & ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (PromoteIntegralResult((long)Value & AsLong(other)), null);

            return base.BitwiseAndedBy(other);
        }

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new UnsignedShortValue((ushort)(Value | ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (PromoteIntegralResult((long)Value | AsLong(other)), null);

            return base.BitwiseOredBy(other);
        }

        public sealed override ValueResult BitwiseXoredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (new UnsignedShortValue((ushort)(Value ^ ((UnsignedShortValue)other).Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (PromoteIntegralResult((long)Value ^ AsLong(other)), null);

            return base.BitwiseXoredBy(other);
        }

        public sealed override ValueResult BitwiseUnsignedRightShiftedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 16, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new UnsignedShortValue((ushort)(Value >> n)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseRotateLeftedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 16, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            ushort rotated = n == 0 ? Value : (ushort)((Value << n) | (Value >> (16 - n)));
            return (new UnsignedShortValue(rotated).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseRotateRightedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 16, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            ushort rotated = n == 0 ? Value : (ushort)((Value >> n) | (Value << (16 - n)));
            return (new UnsignedShortValue(rotated).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult Notted()
        {
            return (new UnsignedShortValue(Value == 0 ? (ushort)1 : (ushort)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult BitwiseNotted()
        {
            return (new UnsignedShortValue((ushort)~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override ValueResult Factorial()
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

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (BooleanValue.Of(Value == ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (BooleanValue.Of((long)Value == AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (BooleanValue.Of((float)Value == ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (BooleanValue.Of((double)Value == ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (BooleanValue.Of((decimal)Value == ((DecimalValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (BooleanValue.Of(PromoteToNumber().GetComparisonEq(other).Item1?.IsTrue() == true).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Boolean)
                return (BooleanValue.Of((((BooleanValue)other).Value && Value == 1) || (!((BooleanValue)other).Value && Value == 0)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.String)
                return (BooleanValue.Of(Value.ToString() == ((StringValue)other).Value).SetContext(Context), null);

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b) return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (BooleanValue.Of(Value < ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (BooleanValue.Of((long)Value < AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (BooleanValue.Of((float)Value < ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (BooleanValue.Of((double)Value < ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (BooleanValue.Of((decimal)Value < ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLt(other);
        }

        public sealed override ValueResult GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (BooleanValue.Of(Value > ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (BooleanValue.Of((long)Value > AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (BooleanValue.Of((float)Value > ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (BooleanValue.Of((double)Value > ((DoubleValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGt(other);
        }

        public sealed override ValueResult GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (BooleanValue.Of(Value <= ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (BooleanValue.Of((long)Value <= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (BooleanValue.Of((float)Value <= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (BooleanValue.Of((double)Value <= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (BooleanValue.Of((decimal)Value <= ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLte(other);
        }

        public sealed override ValueResult GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.UnsignedShort)
                return (BooleanValue.Of(Value >= ((UnsignedShortValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.Long
                || other.Type == RuntimeValueType.UnsignedLong || other.Type == RuntimeValueType.Int128
                || other.Type == RuntimeValueType.UnsignedInt128 || other.Type == RuntimeValueType.Byte)
                return (BooleanValue.Of((long)Value >= AsLong(other)).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Float)
                return (BooleanValue.Of((float)Value >= ((FloatValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Double)
                return (BooleanValue.Of((double)Value >= ((DoubleValue)other).Value).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Decimal)
                return (BooleanValue.Of((decimal)Value >= ((DecimalValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGte(other);
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if ((byte)Value > byte.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to byte without overflow", Context));

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (new FloatValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                if ((UInt128)Value > UInt128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to uint128 without overflow", Context));

                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                if ((Int128)Value > Int128.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to int128 without overflow", Context));

                return (new Int128Value((Int128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (Value > (ushort)short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                return (new IntegerValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                return (new UnsignedIntegerValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                return (new LongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                return (new UnsignedLongValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
                return (BooleanValue.Of(Value != 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
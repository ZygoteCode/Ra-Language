using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class DecimalValue : RuntimeValue
    {
        public decimal Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Decimal;
        public sealed override bool IsCopy => true;

        public DecimalValue(decimal value)
        {
            Value = value;
        }

        public static DecimalValue FromLiteral(string literal)
        {
            return new DecimalValue(ParseLiteralToDecimal(literal));
        }

        public static DecimalValue? TryParseLiteral(string literal)
        {
            try
            {
                return new DecimalValue(ParseLiteralToDecimal(literal));
            }
            catch
            {
                return null;
            }
        }

        private static decimal ParseLiteralToDecimal(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();
            return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static bool TryGetDecimal(RuntimeValue value, out decimal result)
        {
            result = 0m;

            switch (value.Type)
            {
                case RuntimeValueType.Byte:
                    result = ((ByteValue)value).Value;
                    return true;

                case RuntimeValueType.Decimal:
                    result = ((DecimalValue)value).Value;
                    return true;

                case RuntimeValueType.Short:
                    result = ((ShortValue)value).Value;
                    return true;

                case RuntimeValueType.UnsignedShort:
                    result = ((UnsignedShortValue)value).Value;
                    return true;

                case RuntimeValueType.Integer:
                    result = ((IntegerValue)value).Value;
                    return true;

                case RuntimeValueType.UnsignedInteger:
                    result = ((UnsignedIntegerValue)value).Value;
                    return true;

                case RuntimeValueType.Long:
                    result = ((LongValue)value).Value;
                    return true;

                case RuntimeValueType.UnsignedLong:
                    result = ((UnsignedLongValue)value).Value;
                    return true;

                case RuntimeValueType.Int128:
                    {
                        var s = ((Int128Value)value).Value.ToString();
                        return decimal.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                    }

                case RuntimeValueType.UnsignedInt128:
                    {
                        var s = ((UnsignedInt128Value)value).Value.ToString();
                        return decimal.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                    }

                case RuntimeValueType.Float:
                    {
                        var f = ((FloatValue)value).Value;
                        try
                        {
                            result = (decimal)f;
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    }

                case RuntimeValueType.Double:
                    {
                        var d = ((DoubleValue)value).Value;
                        if (double.IsNaN(d) || double.IsInfinity(d))
                            return false;

                        try
                        {
                            result = (decimal)d;
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    }

                case RuntimeValueType.Number:
                    {
                        var s = ((NumberValue)value).Value.ToString();
                        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
                    }

                case RuntimeValueType.Boolean:
                    result = ((BooleanValue)value).Value ? 1m : 0m;
                    return true;

                case RuntimeValueType.String:
                    return decimal.TryParse(((StringValue)value).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

                default:
                    return false;
            }
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture)));
        }

        private RuntimeValue ReturnDecimal(decimal value)
        {
            return new DecimalValue(value)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        private RuntimeValue ReturnDouble(double value)
        {
            return new DoubleValue(value)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        private static bool IsWhole(decimal d) => d == decimal.Truncate(d);

        private static RuntimeValue PowerDecimal(decimal left, decimal right, RuntimeValue ctxSource)
        {
            if (!IsWhole(right))
            {
                double dd = Math.Pow((double)left, (double)right);
                return new DoubleValue(dd).SetContext(ctxSource.Context).SetPos(ctxSource.PositionStart, ctxSource.PositionEnd);
            }

            if (right < 0m)
            {
                double dd = Math.Pow((double)left, (double)right);
                return new DoubleValue(dd).SetContext(ctxSource.Context).SetPos(ctxSource.PositionStart, ctxSource.PositionEnd);
            }

            try
            {
                checked
                {
                    decimal result = 1m;
                    decimal baseVal = left;
                    decimal exp = right;

                    while (exp > 0m)
                    {
                        if ((exp % 2m) == 1m)
                            result *= baseVal;

                        exp = decimal.Truncate(exp / 2m);
                        if (exp > 0m)
                            baseVal *= baseVal;
                    }

                    return new DecimalValue(result)
                        .SetContext(ctxSource.Context)
                        .SetPos(ctxSource.PositionStart, ctxSource.PositionEnd);
                }
            }
            catch
            {
                double dd = Math.Pow((double)left, (double)right);
                return new DoubleValue(dd).SetContext(ctxSource.Context).SetPos(ctxSource.PositionStart, ctxSource.PositionEnd);
            }
        }

        public sealed override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.AddedTo(other);

            try
            {
                checked
                {
                    return (ReturnDecimal(Value + rhs), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.SubbedBy(other);

            try
            {
                checked
                {
                    return (ReturnDecimal(Value - rhs), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.MultedBy(other);

            try
            {
                checked
                {
                    return (ReturnDecimal(Value * rhs), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.DivedBy(other);

            if (rhs == 0m)
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));

            try
            {
                checked
                {
                    return (ReturnDecimal(Value / rhs), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.ModuledBy(other);

            if (rhs == 0m)
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

            try
            {
                checked
                {
                    return (ReturnDecimal(Value % rhs), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
                return base.PowedBy(other);

            return (PowerDecimal(Value, rhs, this), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    var lhsBig = BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture));
                    return (new BooleanValue(lhsBig == ((NumberValue)other).Value).SetContext(Context), null);
                }

                return base.GetComparisonEq(other);
            }

            return (new BooleanValue(Value == rhs).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);

            return base.GetComparisonNe(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    var lhsBig = BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture));
                    return (new BooleanValue(lhsBig < ((NumberValue)other).Value).SetContext(Context), null);
                }

                return base.GetComparisonLt(other);
            }

            return (new BooleanValue(Value < rhs).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (!TryGetDecimal(other, out var rhs))
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    var lhsBig = BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture));
                    return (new BooleanValue(lhsBig > ((NumberValue)other).Value).SetContext(Context), null);
                }

                return base.GetComparisonGt(other);
            }

            return (new BooleanValue(Value > rhs).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            var gt = GetComparisonGt(other).Item1;
            if (gt is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);

            return base.GetComparisonLte(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            var lt = GetComparisonLt(other).Item1;
            if (lt is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);

            return base.GetComparisonGte(other);
        }

        public sealed override (RuntimeValue?, Error?) Notted()
        {
            return (new DecimalValue(Value == 0m ? 1m : 0m).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override (RuntimeValue?, Error?) Factorial()
        {
            if (Value < 0m)
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative decimals", Context));

            if (!IsWhole(Value))
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is only defined for whole decimals", Context));

            try
            {
                checked
                {
                    decimal factorial = 1m;
                    for (int i = 2; i <= (int)Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new DecimalValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Decimal overflow", Context));
            }
        }

        public sealed override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < byte.MinValue || Value > byte.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to byte without overflow", Context));

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < short.MinValue || Value > short.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to short without overflow", Context));

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < 0m || Value > ushort.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to ushort without overflow", Context));

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < int.MinValue || Value > int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to int without overflow", Context));

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < 0m || Value > uint.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to uint without overflow", Context));

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < long.MinValue || Value > long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to long without overflow", Context));

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < 0m || Value > ulong.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to ulong without overflow", Context));

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                if (!IsWhole(Value))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer decimal to int128", Context));

                if (!Int128.TryParse(Value.ToString(CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i128))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to int128 without overflow", Context));

                return (new Int128Value(i128).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                if (!IsWhole(Value) || Value < 0m)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to uint128", Context));

                if (!UInt128.TryParse(Value.ToString(CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u128))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to uint128 without overflow", Context));

                return (new UnsignedInt128Value(u128).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal))
            {
                if ((float)Value < float.MinValue || (float)Value > float.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to float without overflow", Context));

                return (new FloatValue((float)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal))
            {
                if ((double) Value < double.MinValue || (double) Value > double.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to double without overflow", Context));

                return (new DoubleValue((double)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString(CultureInfo.InvariantCulture)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(Value != 0m).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            return new DecimalValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public sealed override bool IsTrue() => Value != 0m;

        public sealed override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }
}
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;
using System;
using System.Globalization;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ByteValue : RuntimeValue
    {
        public byte Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Byte;
        public sealed override bool IsCopy => true;

        public ByteValue(byte value)
        {
            Value = value;
        }

        public static ByteValue FromLiteral(string literal)
        {
            return new ByteValue(ParseLiteralToByte(literal));
        }

        public static ByteValue? TryParseLiteral(string literal)
        {
            try
            {
                return new ByteValue(ParseLiteralToByte(literal));
            }
            catch
            {
                return null;
            }
        }

        private static byte ParseLiteralToByte(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
                throw new FormatException("Byte cannot be negative");

            if (s.StartsWith("0x", StringComparison.Ordinal))
                return Convert.ToByte(s.Substring(2), 16);

            if (s.StartsWith("0b", StringComparison.Ordinal))
                return Convert.ToByte(s.Substring(2), 2);

            if (s.StartsWith("0o", StringComparison.Ordinal))
                return Convert.ToByte(s.Substring(2), 8);

            return byte.Parse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsWholeFloat(float f) => MathF.Abs(f - MathF.Truncate(f)) <= 0.000001f;
        private static bool IsWholeDouble(double d) => Math.Abs(d - Math.Truncate(d)) <= 0.000001d;

        private static bool TryGetIntegral(RuntimeValue value, out long result)
        {
            result = 0;

            switch (value.Type)
            {
                case RuntimeValueType.Byte:
                    result = ((ByteValue)value).Value;
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
                    {
                        var ul = ((UnsignedLongValue)value).Value;
                        if (ul > long.MaxValue) return false;
                        result = (long)ul;
                        return true;
                    }
                case RuntimeValueType.Int128:
                    {
                        var i = ((Int128Value)value).Value;
                        if (i < long.MinValue || i > long.MaxValue) return false;
                        result = (long)i;
                        return true;
                    }
                case RuntimeValueType.UnsignedInt128:
                    {
                        var ui = ((UnsignedInt128Value)value).Value;
                        if (ui > (UInt128)long.MaxValue) return false;
                        result = (long)ui;
                        return true;
                    }
                default:
                    return false;
            }
        }

        private static bool TryGetDecimal(RuntimeValue value, out decimal result)
        {
            result = 0m;

            switch (value.Type)
            {
                case RuntimeValueType.Byte:
                    result = ((ByteValue)value).Value;
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
                    return decimal.TryParse(((Int128Value)value).Value.ToString(), out result);
                case RuntimeValueType.UnsignedInt128:
                    return decimal.TryParse(((UnsignedInt128Value)value).Value.ToString(), out result);
                case RuntimeValueType.Float:
                    try { result = (decimal)((FloatValue)value).Value; return true; } catch { return false; }
                case RuntimeValueType.Double:
                    {
                        var d = ((DoubleValue)value).Value;
                        if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                        try { result = (decimal)d; return true; } catch { return false; }
                    }
                case RuntimeValueType.Decimal:
                    result = ((DecimalValue)value).Value;
                    return true;
                default:
                    return false;
            }
        }

        private NumberValue PromoteToNumber() => new NumberValue(BigNumber.Parse(Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        private RuntimeValue PromoteIntegral(long v)
        {
            if (v >= 0 && v <= byte.MaxValue) return new ByteValue((byte)v).SetContext(Context).SetPos(PositionStart, PositionEnd);
            if (v >= short.MinValue && v <= short.MaxValue) return new ShortValue((short)v).SetContext(Context).SetPos(PositionStart, PositionEnd);
            if (v >= int.MinValue && v <= int.MaxValue) return new IntegerValue((int)v).SetContext(Context).SetPos(PositionStart, PositionEnd);
            return new LongValue(v).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        private RuntimeValue PromoteDecimal(decimal v)
        {
            return new DecimalValue(v).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public sealed override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                try
                {
                    checked { return (PromoteDecimal(Value + rhs), null); }
                }
                catch { return (new DoubleValue((double)Value + (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null); }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value + ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value + ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().AddedTo(other);

            return base.AddedTo(other);
        }

        public sealed override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                try
                {
                    checked { return (PromoteDecimal(Value - rhs), null); }
                }
                catch { return (new DoubleValue((double)Value - (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null); }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value - ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value - ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().SubbedBy(other);

            return base.SubbedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                try
                {
                    checked { return (PromoteDecimal(Value * rhs), null); }
                }
                catch { return (new DoubleValue((double)Value * (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null); }
            }

            if (other.Type == RuntimeValueType.Float)
                return (new FloatValue(Value * ((FloatValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Double)
                return (new DoubleValue(Value * ((DoubleValue)other).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().MultedBy(other);

            return base.MultedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                if (rhs == 0m)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));

                try
                {
                    checked { return (PromoteDecimal(Value / rhs), null); }
                }
                catch { return (new DoubleValue((double)Value / (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null); }
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = ((FloatValue)other).Value;
                if (f == 0f) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new FloatValue(Value / f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = ((DoubleValue)other).Value;
                if (d == 0d) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                return (new DoubleValue(Value / d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
                return PromoteToNumber().DivedBy(other);

            return base.DivedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                if (rhs < 0m || rhs != decimal.Truncate(rhs))
                    return (new DoubleValue(Math.Pow((double)Value, (double)rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

                try
                {
                    checked
                    {
                        decimal result = 1m;
                        decimal baseVal = Value;
                        decimal exp = rhs;

                        while (exp > 0m)
                        {
                            if ((exp % 2m) == 1m)
                                result *= baseVal;

                            exp = decimal.Truncate(exp / 2m);
                            if (exp > 0m)
                                baseVal *= baseVal;
                        }

                        return (PromoteDecimal(result), null);
                    }
                }
                catch
                {
                    return (new DoubleValue(Math.Pow((double)Value, (double)rhs)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.PowedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
            {
                if (rhs == 0m)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                try
                {
                    checked { return (PromoteDecimal(Value % rhs), null); }
                }
                catch { return (new DoubleValue((double)Value % (double)rhs).SetContext(Context).SetPos(PositionStart, PositionEnd), null); }
            }

            return base.ModuledBy(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
                return (BooleanValue.Of(Value == rhs).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture)) == ((NumberValue)other).Value).SetContext(Context), null);

            return base.GetComparisonEq(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b) return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
                return (BooleanValue.Of(Value < rhs).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture)) < ((NumberValue)other).Value).SetContext(Context), null);

            return base.GetComparisonLt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (TryGetDecimal(other, out var rhs))
                return (BooleanValue.Of(Value > rhs).SetContext(Context), null);

            if (other.Type == RuntimeValueType.Number)
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString(CultureInfo.InvariantCulture)) > ((NumberValue)other).Value).SetContext(Context), null);

            return base.GetComparisonGt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            var gt = GetComparisonGt(other).Item1;
            if (gt is BooleanValue b) return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            var lt = GetComparisonLt(other).Item1;
            if (lt is BooleanValue b) return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public sealed override (RuntimeValue?, Error?) Notted()
        {
            return (new ByteValue(Value == 0 ? (byte)1 : (byte)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override (RuntimeValue?, Error?) Factorial()
        {
            try
            {
                checked
                {
                    decimal fact = 1m;
                    for (int i = 2; i <= Value; i++)
                        fact *= i;

                    return (PromoteDecimal(fact), null);
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

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (Value < byte.MinValue || Value > byte.MaxValue || Value != decimal.Truncate(Value))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast decimal to byte without overflow", Context));

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal) ||
                string.Equals(tn, "f128", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
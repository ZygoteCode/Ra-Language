using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class DoubleValue : RuntimeValue
    {
        public double Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.Double;
        public override bool IsCopy => true;

        public DoubleValue(double value)
        {
            Value = value;
        }

        public static DoubleValue FromLiteral(string literal)
        {
            return new DoubleValue(ParseLiteralToDouble(literal));
        }

        public static DoubleValue? TryParseLiteral(string literal)
        {
            try
            {
                return new DoubleValue(ParseLiteralToDouble(literal));
            }
            catch
            {
                return null;
            }
        }

        private static double ParseLiteralToDouble(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static double AsDouble(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.Double => ((DoubleValue)other).Value,
                RuntimeValueType.Float => ((FloatValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (double)((Int128Value)other).Value,
                _ => throw new InvalidOperationException("Cannot convert runtime value to double")
            };
        }

        private static bool Finite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double result = Value + AsDouble(other);
                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double result = Value - AsDouble(other);
                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double result = Value * AsDouble(other);
                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double divisor = AsDouble(other);
                if (divisor == 0.0)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));

                double result = Value / divisor;
                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double exponent = AsDouble(other);
                double result = Math.Pow(Value, exponent);

                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                double divisor = AsDouble(other);
                if (divisor == 0.0)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                double result = Value % divisor;
                if (!Finite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value == AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) == n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1.0) || (!b.Value && Value == 0.0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString("R", CultureInfo.InvariantCulture) == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value != AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) != n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue(!(b.Value && Value == 1.0) & !(!b.Value && Value == 0.0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString("R", CultureInfo.InvariantCulture) != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value < AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value > AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value <= AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Double ||
                other.Type == RuntimeValueType.Float ||
                other.Type == RuntimeValueType.Long ||
                other.Type == RuntimeValueType.Integer ||
                other.Type == RuntimeValueType.UnsignedInteger ||
                other.Type == RuntimeValueType.UnsignedLong ||
                other.Type == RuntimeValueType.Short ||
                other.Type == RuntimeValueType.UnsignedShort ||
                other.Type == RuntimeValueType.Int128)
            {
                return (new BooleanValue(Value >= AsDouble(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new DoubleValue(Value == 0.0 ? 1.0 : 0.0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            if (Value < 0.0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative doubles", Context));
            }

            if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is only defined for whole doubles", Context));
            }

            try
            {
                checked
                {
                    double result = 1.0;
                    for (int i = 2; i <= (int)Value; i++)
                    {
                        result *= i;
                    }

                    if (!Finite(result))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));

                    return (new DoubleValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Double overflow", Context));
            }
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "int128", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i128", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "integer128", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    (Int128) Value < Int128.MinValue || (Int128) Value > Int128.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to int128", Context));
                }

                return (new Int128Value((Int128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "f64", StringComparison.OrdinalIgnoreCase))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "f32", StringComparison.OrdinalIgnoreCase))
            {
                if (Value < float.MinValue || Value > float.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast double to float without overflow", Context));
                }

                return (new FloatValue((float)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i32", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < int.MinValue || Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to int", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i64", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < long.MinValue || Value > long.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to long", Context));
                }

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.OrdinalIgnoreCase))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.OrdinalIgnoreCase))
            {
                return (new StringValue(Value.ToString("R", CultureInfo.InvariantCulture)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "bool", StringComparison.OrdinalIgnoreCase))
            {
                return (new BooleanValue(Value != 0.0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "unsignedinteger", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "ui32", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < uint.MinValue || Value > uint.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to uint", Context));
                }

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "unsignedlong", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "ui64", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < ulong.MinValue || Value > ulong.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to ulong", Context));
                }

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "i16", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "int16", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "short", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < short.MinValue || Value > short.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to short", Context));
                }

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ui16", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "uint16", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "ushort", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "unsignedshort", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(Value - Math.Truncate(Value)) > 0.000001d ||
                    Value < ushort.MinValue || Value > ushort.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to ushort", Context));
                }

                return (new UnsignedShortValue((ushort)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new DoubleValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0.0;

        public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
    }
}
using System.Threading.Tasks;
using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class FloatValue : RuntimeValue
    {
        public float Value { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Float;
        public sealed override bool IsCopy => true;

        public FloatValue(float value)
        {
            Value = value;
        }

        public static FloatValue FromLiteral(string literal)
        {
            return new FloatValue(ParseLiteralToFloat(literal));
        }

        public static FloatValue? TryParseLiteral(string literal)
        {
            try
            {
                return new FloatValue(ParseLiteralToFloat(literal));
            }
            catch
            {
                return null;
            }
        }

        private static float ParseLiteralToFloat(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();
            return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static float AsFloat(RuntimeValue other)
        {
            return other.Type switch
            {
                RuntimeValueType.Float => ((FloatValue)other).Value,
                RuntimeValueType.Integer => ((IntegerValue)other).Value,
                RuntimeValueType.Long => ((LongValue)other).Value,
                RuntimeValueType.Double => (float)((DoubleValue)other).Value,
                RuntimeValueType.UnsignedInteger => ((UnsignedIntegerValue)other).Value,
                RuntimeValueType.UnsignedLong => ((UnsignedLongValue)other).Value,
                RuntimeValueType.Short => ((ShortValue)other).Value,
                RuntimeValueType.UnsignedShort => ((UnsignedShortValue)other).Value,
                RuntimeValueType.Int128 => (float)((Int128Value)other).Value,
                RuntimeValueType.UnsignedInt128 => (float)((UnsignedInt128Value)other).Value,
                RuntimeValueType.Decimal => (float)((DecimalValue)other).Value,
                RuntimeValueType.Byte => ((ByteValue)other).Value,
                _ => throw new InvalidOperationException("Cannot convert runtime value to float")
            };
        }

        private static bool EnsureFinite(float value)
        {
            return !(float.IsNaN(value) || float.IsInfinity(value));
        }

        public sealed override ValueResult AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float result = Value + AsFloat(other);
                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float result = Value - AsFloat(other);
                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            return base.SubbedBy(other);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float result = Value * AsFloat(other);
                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float divisor = AsFloat(other);
                if (divisor == 0f)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                float result = Value / divisor;
                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float exponent = AsFloat(other);
                float result = MathF.Pow(Value, exponent);

                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            return base.PowedBy(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                float divisor = AsFloat(other);
                if (divisor == 0f)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                float result = Value % divisor;
                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            return base.ModuledBy(other);
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value == AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) == n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of((b.Value && Value == 1f) || (!b.Value && Value == 0f)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString("R", CultureInfo.InvariantCulture) == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value != AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) != n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of(!(b.Value && Value == 1f) & !(!b.Value && Value == 0f)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(Value.ToString("R", CultureInfo.InvariantCulture) != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value < AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public sealed override ValueResult GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value > AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public sealed override ValueResult GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value <= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public sealed override ValueResult GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer
                || other.Type == RuntimeValueType.Long || other.Type == RuntimeValueType.Double
                || other.Type == RuntimeValueType.UnsignedInteger || other.Type == RuntimeValueType.UnsignedLong
                || other.Type == RuntimeValueType.Short || other.Type == RuntimeValueType.UnsignedShort
                || other.Type == RuntimeValueType.Int128 || other.Type == RuntimeValueType.UnsignedInt128
                || other.Type == RuntimeValueType.Decimal || other.Type == RuntimeValueType.Byte)
            {
                return (BooleanValue.Of(Value >= AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public sealed override ValueResult Notted()
        {
            return (new FloatValue(Value == 0f ? 1f : 0f).SetContext(Context), null);
        }

        public sealed override ValueResult Factorial()
        {
            if (Value < 0f)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative floats", Context));
            }

            if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is only defined for whole floats", Context));
            }

            try
            {
                float result = 1f;
                for (int i = 2; i <= (int)Value; i++)
                {
                    result *= i;
                }

                if (!EnsureFinite(result))
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));

                return (new FloatValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Float overflow", Context));
            }
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < byte.MinValue || Value > byte.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to byte", Context));
                }

                return (new ByteValue((byte)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (new DecimalValue((decimal) Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    (UInt128)Value < UInt128.MinValue || (UInt128)Value > UInt128.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to uint128", Context));
                }

                return (new UnsignedInt128Value((UInt128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    (Int128) Value < Int128.MinValue || (Int128) Value > Int128.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to int128", Context));
                }

                return (new Int128Value((Int128)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < int.MinValue || Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to int", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < long.MinValue || Value > long.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to long", Context));
                }

                return (new LongValue((long)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString("R", CultureInfo.InvariantCulture)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (BooleanValue.Of(Value != 0f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal))
            {
                return (new DoubleValue(Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < uint.MinValue || Value > uint.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to uint", Context));
                }

                return (new UnsignedIntegerValue((uint)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < ulong.MinValue || Value > ulong.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to ulong", Context));
                }

                return (new UnsignedLongValue((ulong)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < short.MinValue || Value > short.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to short", Context));
                }

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < short.MinValue || Value > short.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to short", Context));
                }

                return (new ShortValue((short)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            // Immutable primitive: sharing the same instance is safe and removes per-read allocations.
            return this;
        }

        public sealed override bool IsTrue() => Value != 0f;

        public sealed override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
    }
}
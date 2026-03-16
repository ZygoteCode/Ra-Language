using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class FloatValue : RuntimeValue
    {
        public float Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.Float;
        public override bool IsCopy => true;

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
                _ => throw new InvalidOperationException("Cannot convert runtime value to float")
            };
        }

        private static bool EnsureFinite(float value)
        {
            return !(float.IsNaN(value) || float.IsInfinity(value));
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
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

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value == AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) == n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1f) || (!b.Value && Value == 0f)).SetContext(Context), null);
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
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value != AsFloat(other)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString("R", CultureInfo.InvariantCulture)) != n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue(!(b.Value && Value == 1f) & !(!b.Value && Value == 0f)).SetContext(Context), null);
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
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value < AsFloat(other)).SetContext(Context), null);
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
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value > AsFloat(other)).SetContext(Context), null);
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
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value <= AsFloat(other)).SetContext(Context), null);
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
            if (other.Type == RuntimeValueType.Float || other.Type == RuntimeValueType.Integer || other.Type == RuntimeValueType.Long)
            {
                return (new BooleanValue(Value >= AsFloat(other)).SetContext(Context), null);
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
            return (new FloatValue(Value == 0f ? 1f : 0f).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
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

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "float", StringComparison.OrdinalIgnoreCase))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "integer", StringComparison.OrdinalIgnoreCase))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < int.MinValue || Value > int.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to int", Context));
                }

                return (new IntegerValue((int)Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.OrdinalIgnoreCase))
            {
                if (MathF.Abs(Value - MathF.Truncate(Value)) > 0.000001f ||
                    Value < long.MinValue || Value > long.MaxValue)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to long", Context));
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
                return (new BooleanValue(Value != 0f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new FloatValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0f;

        public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
    }
}
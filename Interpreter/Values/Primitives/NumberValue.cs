using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;
using System.Globalization;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class NumberValue : RuntimeValue
    {
        public BigNumber Value { get; }
        public static NumberValue One => new NumberValue(1);
        public static NumberValue Zero => new NumberValue(0);
        public override RuntimeValueType Type => RuntimeValueType.Number;
        public override bool IsCopy => true;

        public NumberValue(BigNumber value)
        {
            Value = value;
        }

        public static NumberValue MathPI => new NumberValue(BigNumber.Parse(Math.PI.ToString("R", CultureInfo.InvariantCulture)));

        private static NumberValue Promote(IntegerValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue((Value - n.Value)).SetContext(Context), null);
            }


            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue((Value * n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                if (n.Value.IsZero()) return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Division by zero", Context));

                try
                {
                    return (new NumberValue(Value / n.Value).SetContext(Context), null);
                }
                catch (DivideByZeroException)
                {
                    return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Division by zero", Context));
                }
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                if (n.Value.IsZero())
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));

                try
                {
                    return (new NumberValue(Value / n.Value).SetContext(Context), null);
                }
                catch (DivideByZeroException)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(Value.Pow(n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new NumberValue(Value.Pow(n.Value)).SetContext(Context), null);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value.ToString() == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(!(b.Value && Value == 1) & !(!b.Value && Value == 0)).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value.ToString() != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }


            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new NumberValue(Value.IsZero() ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new NumberValue(BigNumber.BitwiseNot(Value)).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.BitwiseAnd(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.BitwiseOr(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.LeftShift(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.RightShift(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                if (n.Value.ToBigInteger().IsZero) return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Modulo by zero", Context));
                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.Value == Value).SetContext(Context), null);
            }

            return (new BooleanValue(false).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.Value != Value).SetContext(Context), null);
            }

            return (new BooleanValue(true).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            BigNumber factorial = 1;

            for (int i = 1; i <= Value; i++)
            {
                factorial *= i;
            }

            return (new NumberValue(factorial).SetContext(Context), null);
        }

        public override RuntimeValue Copy()
        {
            return new NumberValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to int", Context));
                }

                return (IntegerValue.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.Ordinal) ||
                string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(!Value.IsZero()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override bool IsTrue() => Value == 1;

        public override string ToString() => Value.ToString();
    }
}
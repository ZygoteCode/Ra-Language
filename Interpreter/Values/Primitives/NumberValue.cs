namespace RaLanguage.Interpreter.Values.Primitives
{
    using RaLanguage.Errors;
    using RaLanguage.Errors.Types;
    using System.Globalization;

    public class NumberValue : RuntimeValue
    {
        public BigNumber Value { get; }

        public NumberValue(BigNumber value)
        {
            Value = value;
        }

        public static NumberValue Null => new NumberValue(BigNumber.Zero);
        public static NumberValue False => new NumberValue(BigNumber.Zero);
        public static NumberValue True => new NumberValue(BigNumber.One);
        public static NumberValue MathPI => new NumberValue(BigNumber.Parse(Math.PI.ToString("R", CultureInfo.InvariantCulture)));

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((Value + n.Value)).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((Value - n.Value)).SetContext(Context), null);
            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((Value * n.Value)).SetContext(Context), null);
            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other is NumberValue n)
            {
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
            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value.Pow(n.Value)).SetContext(Context), null);
            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value == n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value != n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value < n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value > n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value <= n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value >= n.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((!Value.IsZero() && !n.Value.IsZero()) ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((!Value.IsZero() || !n.Value.IsZero()) ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
            return base.OredBy(other);
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
            if (other is NumberValue n) return (new NumberValue(BigNumber.BitwiseAnd(Value, n.Value)).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(BigNumber.BitwiseOr(Value, n.Value)).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(BigNumber.LeftShift(Value, n.Value)).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(BigNumber.RightShift(Value, n.Value)).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other is NumberValue n)
            {
                if (n.Value.ToBigInteger().IsZero) return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Modulo by zero", Context));
                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }
            return base.AddedTo(other);
        }

        public override RuntimeValue Copy()
        {
            return new NumberValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => !Value.IsZero();

        public override string ToString() => Value.ToString();
    }
}
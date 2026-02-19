namespace RaLanguage.Interpreter.Values.Primitives
{
    using RaLanguage.Errors;
    using RaLanguage.Errors.Types;
    using System.Globalization;

    public class NumberValue : RuntimeValue
    {
        public double Value { get; }

        public NumberValue(double value)
        {
            Value = value;
        }

        public static NumberValue Null => new NumberValue(0);
        public static NumberValue False => new NumberValue(0);
        public static NumberValue True => new NumberValue(1);
        public static NumberValue MathPI => new NumberValue(Math.PI);

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value + n.Value).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value - n.Value).SetContext(Context), null);
            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value * n.Value).SetContext(Context), null);
            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other is NumberValue n)
            {
                if (n.Value == 0) return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Division by zero", Context));
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }
            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Math.Pow(Value, n.Value)).SetContext(Context), null);
            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value == n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value != n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value < n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value > n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value <= n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue(Value >= n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((Value != 0 && n.Value != 0) ? 1 : 0).SetContext(Context), null);
            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other is NumberValue n) return (new NumberValue((Value != 0 || n.Value != 0) ? 1 : 0).SetContext(Context), null);
            return base.OredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new NumberValue(Value == 0 ? 1 : 0).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new NumberValue(~(int)Value).SetContext(Context), null);
        }

        public override RuntimeValue Copy()
        {
            return new NumberValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }
}
namespace RaLanguage.Interpreter.Values.Primitives
{
    using RaLanguage.Errors;
    using RaLanguage.Errors.Types;
    using System.Globalization;

    public class Number : RuntimeValue
    {
        public double Value { get; }

        public Number(double value)
        {
            Value = value;
        }

        // Static Helpers for consistency
        public static Number Null => new Number(0); // Assuming NULL is 0 like in Python snippet
        public static Number False => new Number(0);
        public static Number True => new Number(1);
        public static Number MathPI => new Number(Math.PI);

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value + n.Value).SetContext(Context), null);
            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value - n.Value).SetContext(Context), null);
            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value * n.Value).SetContext(Context), null);
            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other is Number n)
            {
                if (n.Value == 0) return (null, new RuntimeError(n.PositionStart, n.PositionEnd, "Division by zero", Context));
                return (new Number(Value / n.Value).SetContext(Context), null);
            }
            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Math.Pow(Value, n.Value)).SetContext(Context), null);
            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value == n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value != n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value < n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value > n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value <= n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other is Number n) return (new Number(Value >= n.Value ? 1 : 0).SetContext(Context), null);
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other is Number n) return (new Number((Value != 0 && n.Value != 0) ? 1 : 0).SetContext(Context), null);
            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other is Number n) return (new Number((Value != 0 || n.Value != 0) ? 1 : 0).SetContext(Context), null);
            return base.OredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new Number(Value == 0 ? 1 : 0).SetContext(Context), null);
        }

        public override RuntimeValue Copy()
        {
            return new Number(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }
}
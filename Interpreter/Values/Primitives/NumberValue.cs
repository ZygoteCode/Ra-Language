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

        private static NumberValue Promote(LongValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(FloatValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(DoubleValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(UnsignedIntegerValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(UnsignedLongValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(ShortValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(UnsignedShortValue value)
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(lhs + rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(Value + rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(lhs - rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(Value - rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(lhs * rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(Value * rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
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

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(lhs / rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(Value / rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new NumberValue(Value.Pow(n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                NumberValue n = Promote((FloatValue)other);
                return (new NumberValue(Value.Pow(n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)rhs).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
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
            else if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value.ToString() == s.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs == rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value == rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
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
            else if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value.ToString() != s.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs != rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value != rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (new BooleanValue(Value != n.Value).SetContext(Context), null);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs < rhs).SetContext(Context), null);
            }
            
            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value < rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs > rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value > rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new BooleanValue(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs <= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value <= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(lhs >= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (new BooleanValue(Value >= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (new BooleanValue(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }


            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
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

            if (string.Equals(tn, "float", StringComparison.Ordinal) ||
                string.Equals(tn, "f32", StringComparison.Ordinal))
            {
                if (!float.TryParse(Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to float", Context));
                }

                return (new FloatValue(f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal) ||
                string.Equals(tn, "f64", StringComparison.Ordinal))
            {
                if (!double.TryParse(Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to double", Context));
                }

                return (new DoubleValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "unsignedinteger", StringComparison.Ordinal) ||
                string.Equals(tn, "uint", StringComparison.Ordinal) ||
                string.Equals(tn, "ui32", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to uint", Context));
                }

                return (UnsignedIntegerValue.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "unsignedlong", StringComparison.Ordinal) ||
                string.Equals(tn, "ulong", StringComparison.Ordinal) ||
                string.Equals(tn, "ui64", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to ulong", Context));
                }

                return (UnsignedLongValue.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal) ||
                string.Equals(tn, "int16", StringComparison.Ordinal) ||
                string.Equals(tn, "i16", StringComparison.Ordinal))
            {
                if (!short.TryParse(Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to short", Context));
                }

                return (new ShortValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal) ||
                string.Equals(tn, "uint16", StringComparison.Ordinal) ||
                string.Equals(tn, "ui16", StringComparison.Ordinal) ||
                string.Equals(tn, "unsignedshort", StringComparison.Ordinal))
            {
                if (!ushort.TryParse(Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to short", Context));
                }

                return (new UnsignedShortValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override bool IsTrue() => Value == 1;

        public override string ToString() => Value.ToString();
    }
}
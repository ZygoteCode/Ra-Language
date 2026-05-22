using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;
using System.Globalization;
using System.Numerics;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class NumberValue : RuntimeValue
    {
        public BigNumber Value { get; }
        public static readonly NumberValue One = new NumberValue(1);
        public static readonly NumberValue Zero = new NumberValue(0);
        public sealed override RuntimeValueType Type => RuntimeValueType.Number;
        public sealed override bool IsCopy => true;

        // Intern pool for small integer-valued NumberValues, modeled on CPython's
        // _Py_GetGlobalObject small-int cache. Most loop counters (`for i in 0..N`)
        // sit inside this range, so reads of the loop variable hit a cached instance
        // instead of allocating per iteration.
        private const int SmallIntMin = -128;
        private const int SmallIntMax = 1024;
        private static readonly NumberValue[] s_smallInts = BuildSmallIntCache();

        private static NumberValue[] BuildSmallIntCache()
        {
            int length = SmallIntMax - SmallIntMin + 1;
            var arr = new NumberValue[length];
            for (int v = SmallIntMin; v <= SmallIntMax; v++)
                arr[v - SmallIntMin] = new NumberValue(new BigNumber(new BigInteger(v), BigInteger.Zero));
            return arr;
        }

        public NumberValue(BigNumber value)
        {
            Value = value;
        }

        // Returns a cached instance for small integer values, falling back to a fresh
        // allocation for anything outside the intern range or with a non-zero scale.
        public static NumberValue OfBigNumber(BigNumber value)
        {
            if (value.Scale.IsZero
                && value.Unscaled >= SmallIntMin
                && value.Unscaled <= SmallIntMax)
            {
                return s_smallInts[(int)value.Unscaled - SmallIntMin];
            }
            return new NumberValue(value);
        }

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

        private static NumberValue Promote(Int128Value value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(UnsignedInt128Value value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(DecimalValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(ByteValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        public sealed override ValueResult AddedTo(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (new NumberValue(Value + n.Value).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (new NumberValue(Value - n.Value).SetContext(Context), null);
            }

            return base.SubbedBy(other);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (new NumberValue(Value * n.Value).SetContext(Context), null);
            }

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (new NumberValue(Value / n.Value).SetContext(Context), null);
            }

            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }
            
            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((ByteValue)other);
                return (new NumberValue(BigNumber.Parse(Math.Pow((double)Value, (double)n.Value).ToString())).SetContext(Context), null);
            }

            return base.PowedBy(other);
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (BooleanValue.Of((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                // Number vs string is never equal (no string<->number coercion).
                return (BooleanValue.Of(false).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs == rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value == rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value == n.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (BooleanValue.Of(!(b.Value && Value == 1) & !(!b.Value && Value == 0)).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                // Number vs string is always unequal.
                return (BooleanValue.Of(true).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs != rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value != rhs).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value != n.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs < rhs).SetContext(Context), null);
            }
            
            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value < rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public sealed override ValueResult GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value < n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs > rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value > rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public sealed override ValueResult GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs <= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value <= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public sealed override ValueResult GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Integer)
            {
                NumberValue n = Promote((IntegerValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Long)
            {
                NumberValue n = Promote((LongValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Float)
            {
                var f = (FloatValue)other;
                var lhs = BigNumber.Parse(Value.ToString());
                var rhs = BigNumber.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(lhs >= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Double)
            {
                var d = (DoubleValue)other;
                var rhs = BigNumber.Parse(d.Value.ToString("R", CultureInfo.InvariantCulture));
                return (BooleanValue.Of(Value >= rhs).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInteger)
            {
                NumberValue n = Promote((UnsignedIntegerValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedLong)
            {
                NumberValue n = Promote((UnsignedLongValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Short)
            {
                NumberValue n = Promote((ShortValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedShort)
            {
                NumberValue n = Promote((UnsignedShortValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                return (BooleanValue.Of(Value >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public sealed override ValueResult Notted()
        {
            return (new NumberValue(Value.IsZero() ? BigNumber.One : BigNumber.Zero).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseNotted()
        {
            return (new NumberValue(BigNumber.BitwiseNot(Value)).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.BitwiseAnd(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.BitwiseOr(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.LeftShift(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.RightShift(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
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

            if (other.Type == RuntimeValueType.Int128)
            {
                NumberValue n = Promote((Int128Value)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.UnsignedInt128)
            {
                NumberValue n = Promote((UnsignedInt128Value)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Decimal)
            {
                NumberValue n = Promote((DecimalValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Byte)
            {
                NumberValue n = Promote((ByteValue)other);
                if (n.Value.ToBigInteger().IsZero)
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));

                return (new NumberValue(BigNumber.Mod(Value, n.Value)).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public sealed override ValueResult GetComparisonStrictEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(n.Value == Value).SetContext(Context), null);
            }

            return (BooleanValue.Of(false).SetContext(Context), null);
        }

        public sealed override ValueResult GetComparisonStrictNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (BooleanValue.Of(n.Value != Value).SetContext(Context), null);
            }

            return (BooleanValue.Of(true).SetContext(Context), null);
        }

        public sealed override ValueResult Factorial()
        {
            BigNumber factorial = 1;

            for (int i = 1; i <= Value; i++)
            {
                factorial *= i;
            }

            return (new NumberValue(factorial).SetContext(Context), null);
        }

        public sealed override RuntimeValue Copy()
        {
            // Immutable primitive: sharing the same instance is safe and removes per-read allocations.
            return this;
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                return (new ByteValue(byte.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                return (new UnsignedInt128Value(UInt128.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                return (new DecimalValue(decimal.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to int128", Context));
                }

                return (Int128Value.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
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

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (BooleanValue.Of(!Value.IsZero()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal))
            {
                if (!float.TryParse(Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to float", Context));
                }

                return (new FloatValue(f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal) )
            {
                if (!double.TryParse(Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to double", Context));
                }

                return (new DoubleValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to uint", Context));
                }

                return (UnsignedIntegerValue.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                var bi = Value.ToBigInteger();
                var roundTrip = BigNumber.Parse(bi.ToString());

                if (Value != roundTrip)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to ulong", Context));
                }

                return (UnsignedLongValue.FromBigInteger(bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (!short.TryParse(Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to short", Context));
                }

                return (new ShortValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                if (!ushort.TryParse(Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to short", Context));
                }

                return (new UnsignedShortValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override bool IsTrue() => !Value.IsZero();

        public sealed override string ToString() => Value.ToString();
    }
}
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class NumberValue : RuntimeValue
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
        //
        // M27.4 widened the upper bound to 8192. Static cost: ~290 KB of NumberValue
        // refs eagerly allocated once at startup (cheap in .NET 10's compacting GC
        // — class instances + their inline BigNumber struct). Runtime benefit:
        // hot loops that range up to ~8 K no longer allocate per iteration in the
        // OP_ADD_INTO_SLOT / Binary fast paths — they hit the intern cache.
        //
        // We cannot safely mutate-in-place a NumberValue's BigNumber field even
        // when the slot owning it has refcount 1: NumberValue is freely aliased
        // by `let b = a` and by closure capture, neither of which is statically
        // tractable from the IR compiler. Widening the cache is the
        // semantics-preserving compromise the doc's "escape analysis" entry
        // gestured at.
        private const int SmallIntMin = -1024;
        private const int SmallIntMax = 8192;
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

        // Fast producer for an int64-valued number — the hot result type of every
        // VM integer-valued add/sub/mul. The intern-range test runs on the native
        // `long` (a register compare) instead of `OfBigNumber`'s BigInteger
        // comparisons, which under NativeAOT are out-of-line framework calls; the
        // cache-hit path also skips materialising the BigNumber struct entirely.
        // Semantically identical to OfBigNumber(new BigNumber(value, 0)).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NumberValue OfInt64(long value)
        {
            if (value >= SmallIntMin && value <= SmallIntMax)
                return s_smallInts[(int)value - SmallIntMin];
            return new NumberValue(new BigNumber(new BigInteger(value), BigInteger.Zero));
        }

        // Integer-typed operands promote to an exact BigNumber directly.
        // Building the BigInteger from the native integer skips the
        // ToString()->Parse() round-trip (a string alloc + decimal scan) the
        // old path paid on every mixed `number op <int-family>` operation.
        private static NumberValue Promote(IntegerValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(LongValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(FloatValue value)
        {
            // Float/double/decimal keep the textual conversion: it captures the
            // exact decimal expansion (scale) the binary value rounds to, which
            // a direct BigInteger build cannot reproduce.
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(DoubleValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(UnsignedIntegerValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(UnsignedLongValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(ShortValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(UnsignedShortValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
        }

        private static NumberValue Promote(Int128Value value)
        {
            return new NumberValue(new BigNumber((BigInteger)value.Value, BigInteger.Zero));
        }

        private static NumberValue Promote(UnsignedInt128Value value)
        {
            return new NumberValue(new BigNumber((BigInteger)value.Value, BigInteger.Zero));
        }

        private static NumberValue Promote(DecimalValue value)
        {
            return new NumberValue(BigNumber.Parse(value.Value.ToString()));
        }

        private static NumberValue Promote(ByteValue value)
        {
            return new NumberValue(new BigNumber(new BigInteger(value.Value), BigInteger.Zero));
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
                var lhs = Value;
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
            // Accept any numeric count, not just NumberValue. width=0 selects
            // the arbitrary-precision path: count is taken as-is (capped at
            // Int32.MaxValue inside BigNumber.LeftShift).
            var err = ShiftCount.TryGet(other, width: 0, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new NumberValue(BigNumber.LeftShift(Value,
                new BigNumber((System.Numerics.BigInteger)n, System.Numerics.BigInteger.Zero))).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            var err = ShiftCount.TryGet(other, width: 0, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            return (new NumberValue(BigNumber.RightShift(Value,
                new BigNumber((System.Numerics.BigInteger)n, System.Numerics.BigInteger.Zero))).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseUnsignedRightShiftedBy(RuntimeValue other)
        {
            // Logical right shift on an arbitrary-precision integer is only
            // defined for non-negative operands — there is no fixed width to
            // "zero-fill" into, so a negative magnitude has no canonical
            // unsigned bit pattern. We surface a precise diagnostic so callers
            // know to use a fixed-width type (e.g. `long`, `int`) instead.
            var err = ShiftCount.TryGet(other, width: 0, PositionStart, PositionEnd, Context, out int n);
            if (err != null) return (null, err);
            var bi = Value.ToBigInteger();
            if (bi.Sign < 0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd,
                    "logical right shift (`>>>`) is undefined on a negative arbitrary-precision 'number'",
                    Context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "operand has no canonical unsigned bit pattern",
                    help: "cast to a fixed-width integer (`long`, `int`, `int128`, …) before applying `>>>`"));
            }
            // For non-negative values, logical and arithmetic right shifts
            // agree. Delegate to BigNumber.RightShift so the cap-at-Int32.MaxValue
            // policy stays centralised.
            return (new NumberValue(BigNumber.RightShift(Value,
                new BigNumber((System.Numerics.BigInteger)n, System.Numerics.BigInteger.Zero))).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseRotateLeftedBy(RuntimeValue other)
        {
            return (null, new RuntimeError(PositionStart, PositionEnd,
                "rotate-left (`<<<<`) is undefined on arbitrary-precision 'number'",
                Context,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no fixed bit-width to rotate within",
                help: "cast the value to a fixed-width integer (`long`, `int`, `int128`, `byte`, …) before applying `<<<<`"));
        }

        public sealed override ValueResult BitwiseRotateRightedBy(RuntimeValue other)
        {
            return (null, new RuntimeError(PositionStart, PositionEnd,
                "rotate-right (`>>>>`) is undefined on arbitrary-precision 'number'",
                Context,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no fixed bit-width to rotate within",
                help: "cast the value to a fixed-width integer (`long`, `int`, `int128`, `byte`, …) before applying `>>>>`"));
        }

        public sealed override ValueResult BitwiseXoredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new NumberValue(BigNumber.BitwiseXor(Value, n.Value)).SetContext(Context), null);
            }

            return base.BitwiseXoredBy(other);
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
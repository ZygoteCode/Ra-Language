using System.Globalization;
using System.Numerics;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class IntegerValue : RuntimeValue
    {
        public BigInteger Value { get; }
        public override RuntimeValueType Type => RuntimeValueType.Integer;
        public override bool IsCopy => true;

        public IntegerValue(BigInteger value)
        {
            Value = value;
        }

        public static IntegerValue FromBigInteger(BigInteger value) => new IntegerValue(value);

        public static IntegerValue FromLiteral(string literal)
        {
            return new IntegerValue(ParseLiteralToBigInteger(literal));
        }

        public static IntegerValue? TryParseLiteral(string literal)
        {
            try
            {
                return new IntegerValue(ParseLiteralToBigInteger(literal));
            }
            catch
            {
                return null;
            }
        }

        private static BigInteger ParseLiteralToBigInteger(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
            {
                return BigInteger.Negate(ParseLiteralToBigInteger(s[1..]));
            }

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ParseBase(s[2..], 16);
            }

            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return ParseBase(s[2..], 2);
            }

            if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                return ParseBase(s[2..], 8);
            }

            return BigInteger.Parse(s, CultureInfo.InvariantCulture);
        }

        private static BigInteger ParseBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
                return BigInteger.Zero;

            BigInteger result = BigInteger.Zero;
            foreach (char c in digits)
            {
                int d = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => 10 + (c - 'a'),
                    >= 'A' and <= 'F' => 10 + (c - 'A'),
                    _ => -1
                };

                if (d < 0 || d >= numberBase)
                    throw new FormatException("Invalid integer literal");

                result = (result * numberBase) + d;
            }

            return result;
        }

        private NumberValue PromoteToNumber()
        {
            return new NumberValue(BigNumber.Parse(Value.ToString()));
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value + i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().AddedTo(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().AddedTo(other).Item2);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value - i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().SubbedBy(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().SubbedBy(other).Item2);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value * i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().MultedBy(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().MultedBy(other).Item2);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                if (i.Value.IsZero)
                    return (null, new RuntimeError(i.PositionStart, i.PositionEnd, "Division by zero", Context));

                return (new IntegerValue(Value / i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().DivedBy(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().DivedBy(other).Item2);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value < 0 || i.Value > int.MaxValue)
                {
                    return (PromoteToNumber().PowedBy(PromoteToNumber()).Item1?.SetPos(PositionStart, PositionEnd),
                            new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid integer exponent", Context));
                }

                return (new IntegerValue(BigInteger.Pow(Value, (int)i.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().PowedBy(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().PowedBy(other).Item2);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                if (i.Value.IsZero)
                    return (null, new RuntimeError(i.PositionStart, i.PositionEnd, "Modulo by zero", Context));

                return (new IntegerValue(Value % i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return (PromoteToNumber().ModuledBy(other).Item1?.SetPos(PositionStart, PositionEnd), PromoteToNumber().ModuledBy(other).Item2);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value << (int)i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value >> (int)i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value & i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value | i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new IntegerValue(Value.IsZero ? BigInteger.One : BigInteger.Zero).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new IntegerValue(~Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) Factorial()
        {
            if (Value < 0)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Factorial is not defined for negative integers", Context));
            }

            BigInteger factorial = BigInteger.One;
            for (BigInteger i = BigInteger.One; i <= Value; i++)
            {
                factorial *= i;
            }

            return (new IntegerValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value == i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) == n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue((b.Value && Value == 1) || (!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString() == s.Value).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value != i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) != n.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (new BooleanValue(!(b.Value && Value == 1) & !(!b.Value && Value == 0)).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new BooleanValue(Value.ToString() != s.Value).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value < i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) < n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value > i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) > n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value <= i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) <= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new BooleanValue(Value >= i.Value).SetContext(Context), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (new BooleanValue(BigNumber.Parse(Value.ToString()) >= n.Value).SetContext(Context), null);
            }

            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "i32", StringComparison.OrdinalIgnoreCase))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "number", StringComparison.OrdinalIgnoreCase))
            {
                return (new NumberValue(BigNumber.Parse(Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.OrdinalIgnoreCase))
            {
                return (new StringValue(Value.ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tn, "bool", StringComparison.OrdinalIgnoreCase))
            {
                return (new BooleanValue(!Value.IsZero).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new IntegerValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => !Value.IsZero;

        public override string ToString() => Value.ToString();
    }
}

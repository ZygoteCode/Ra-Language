using System;
using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class IntegerValue : RuntimeValue
    {
        public int Value { get; }

        public override RuntimeValueType Type => RuntimeValueType.Integer;
        public override bool IsCopy => true;

        public IntegerValue(int value)
        {
            Value = value;
        }

        public static IntegerValue FromBigInteger(System.Numerics.BigInteger value)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new OverflowException("Integer literal out of int range");
            }

            return new IntegerValue((int)value);
        }

        public static IntegerValue FromLiteral(string literal)
        {
            return new IntegerValue(ParseLiteralToInt(literal));
        }

        public static IntegerValue? TryParseLiteral(string literal)
        {
            try
            {
                return new IntegerValue(ParseLiteralToInt(literal));
            }
            catch
            {
                return null;
            }
        }

        private static int ParseLiteralToInt(string literal)
        {
            var s = (literal ?? "0").Replace("_", "").Trim();

            if (s.StartsWith("-"))
            {
                checked
                {
                    return -ParseLiteralToInt(s.Substring(1));
                }
            }

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 16);
            }

            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 2);
            }

            if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                return ParseWithBase(s.Substring(2), 8);
            }

            return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static int ParseWithBase(string digits, int numberBase)
        {
            if (string.IsNullOrWhiteSpace(digits))
            {
                return 0;
            }

            checked
            {
                int result = 0;

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
                    {
                        throw new FormatException("Invalid integer literal");
                    }

                    result = (result * numberBase) + d;
                }

                return result;
            }
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
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value + i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().AddedTo(other);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value - i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().SubbedBy(other);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                try
                {
                    checked
                    {
                        return (new IntegerValue(Value * i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().MultedBy(other);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Division by zero", Context));
                }

                return (new IntegerValue(Value / i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().DivedBy(other);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value < 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Negative exponent not allowed for int", Context));
                }

                try
                {
                    checked
                    {
                        int result = 1;
                        int baseVal = Value;
                        int exp = i.Value;

                        while (exp > 0)
                        {
                            if ((exp & 1) == 1)
                            {
                                result *= baseVal;
                            }

                            exp >>= 1;
                            if (exp > 0)
                            {
                                baseVal *= baseVal;
                            }
                        }

                        return (new IntegerValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                }
                catch
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
                }
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().PowedBy(other);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;

                if (i.Value == 0)
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Modulo by zero", Context));
                }

                return (new IntegerValue(Value % i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Number)
            {
                return PromoteToNumber().ModuledBy(other);
            }

            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value << i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Integer)
            {
                var i = (IntegerValue)other;
                return (new IntegerValue(Value >> i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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
            return (new IntegerValue(Value == 0 ? 1 : 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
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

            try
            {
                checked
                {
                    int factorial = 1;
                    for (int i = 2; i <= Value; i++)
                    {
                        factorial *= i;
                    }

                    return (new IntegerValue(factorial).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch
            {
                return (null, new RuntimeError(PositionStart, PositionEnd, "Integer overflow", Context));
            }
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
                string.Equals(tn, "integer", StringComparison.OrdinalIgnoreCase))
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
                return (new BooleanValue(Value != 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public override RuntimeValue Copy()
        {
            return new IntegerValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue() => Value != 0;

        public override string ToString() => Value.ToString();
    }
}

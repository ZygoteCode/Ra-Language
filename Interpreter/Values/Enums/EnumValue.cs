using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class EnumValue : RuntimeValue
    {
        public string EnumName { get; }
        public string MemberName { get; }
        public Int128 UnderlyingValue { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Enum;
        public sealed override bool IsCopy => true;

        public EnumValue(string enumName, string memberName, Int128 underlyingValue)
        {
            EnumName = enumName;
            MemberName = memberName;
            UnderlyingValue = underlyingValue;
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Enum)
            {
                var e = (EnumValue)other;
                return (new BooleanValue(EnumName == e.EnumName && UnderlyingValue == e.UnderlyingValue).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b)
                return (new BooleanValue(!b.Value).SetContext(Context), null);

            return base.GetComparisonNe(other);
        }

        public sealed override (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, EnumName, StringComparison.Ordinal))
            {
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i32", StringComparison.Ordinal))
            {
                if (UnderlyingValue < int.MinValue || UnderlyingValue > int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast enum to int without overflow", Context));

                return (new IntegerValue((int)UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (UnderlyingValue < long.MinValue || UnderlyingValue > long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast enum to long without overflow", Context));

                return (new LongValue((long)UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal) ||
                string.Equals(tn, "i128", StringComparison.Ordinal) ||
                string.Equals(tn, "integer128", StringComparison.Ordinal))
            {
                return (new Int128Value(UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            return new EnumValue(EnumName, MemberName, UnderlyingValue)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override bool IsTrue() => UnderlyingValue != 0;

        public sealed override string ToString() => $"{EnumName}.{MemberName}";
    }
}
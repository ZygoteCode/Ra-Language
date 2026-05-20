using System;
using System.Collections.Generic;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Tagged-union runtime value. Backs both classic integer enums
    // (`enum Color { Red, Green = 5 }`) and ADT-style enums with payload
    // (`enum Token { Identifier(string), Number(int), Eof }`).
    //
    // For zero-arity variants on integer-style enums, UnderlyingValue carries
    // the resolved discriminant so legacy `EnumValue as int` casts still work.
    // Payload-carrying variants leave UnderlyingValue at the variant index.
    public sealed class EnumValue : RuntimeValue
    {
        public string EnumName { get; }
        public string MemberName { get; }
        public int VariantIndex { get; }
        public Int128 UnderlyingValue { get; }
        public IReadOnlyList<RuntimeValue> Payload { get; }
        public bool HasPayload => Payload != null && Payload.Count > 0;

        private static readonly RuntimeValue[] s_emptyPayload = System.Array.Empty<RuntimeValue>();

        public sealed override RuntimeValueType Type => RuntimeValueType.Enum;
        public sealed override bool IsCopy => true;

        public EnumValue(string enumName, string memberName, int variantIndex, Int128 underlyingValue, IReadOnlyList<RuntimeValue>? payload = null)
        {
            EnumName = enumName;
            MemberName = memberName;
            VariantIndex = variantIndex;
            UnderlyingValue = underlyingValue;
            Payload = payload ?? s_emptyPayload;
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Enum)
            {
                var e = (EnumValue)other;
                if (!string.Equals(EnumName, e.EnumName, StringComparison.Ordinal))
                    return (BooleanValue.False.SetContext(Context), null);
                if (VariantIndex != e.VariantIndex)
                    return (BooleanValue.False.SetContext(Context), null);
                if (Payload.Count != e.Payload.Count)
                    return (BooleanValue.False.SetContext(Context), null);
                for (int i = 0; i < Payload.Count; i++)
                {
                    var (eq, err) = Payload[i].GetComparisonEq(e.Payload[i]);
                    if (err != null) return (null, err);
                    if (eq is BooleanValue bv && !bv.Value)
                        return (BooleanValue.False.SetContext(Context), null);
                    if (!(eq is BooleanValue))
                        return (BooleanValue.False.SetContext(Context), null);
                }
                return (BooleanValue.True.SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b)
                return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonStrictEq(RuntimeValue other)
        {
            if (other.Type != RuntimeValueType.Enum)
                return (BooleanValue.False.SetContext(Context), null);
            return GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonStrictNe(RuntimeValue other)
        {
            var (eq, err) = GetComparisonStrictEq(other);
            if (err != null) return (null, err);
            if (eq is BooleanValue bv) return (BooleanValue.Of(!bv.Value).SetContext(Context), null);
            return (BooleanValue.True.SetContext(Context), null);
        }

        public sealed override ValueResult CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, EnumName, StringComparison.Ordinal))
                return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            if (string.Equals(tn, "string", StringComparison.Ordinal))
                return (new StringValue(ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            // Numeric casts only valid for zero-arity / classic integer-style variants.
            if (HasPayload)
            {
                return (null, new RuntimeError(PositionStart, PositionEnd,
                    $"cannot cast payload-carrying variant '{EnumName}.{MemberName}' to '{tn}'",
                    Context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: "variant has data and is not a plain integer enum",
                    help: "destructure the payload via 'match' instead of casting"));
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
            {
                if (UnderlyingValue < int.MinValue || UnderlyingValue > int.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast enum to int without overflow", Context));
                return (new IntegerValue((int)UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            if (string.Equals(tn, "long", StringComparison.Ordinal))
            {
                if (UnderlyingValue < long.MinValue || UnderlyingValue > long.MaxValue)
                    return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast enum to long without overflow", Context));
                return (new LongValue((long)UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            if (string.Equals(tn, "int128", StringComparison.Ordinal))
                return (new Int128Value(UnderlyingValue).SetContext(Context).SetPos(PositionStart, PositionEnd), null);

            return base.CastTo(targetType);
        }

        public sealed override RuntimeValue Copy()
        {
            return new EnumValue(EnumName, MemberName, VariantIndex, UnderlyingValue, Payload)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override bool IsTrue()
        {
            // Payload-carrying variants are truthy when present; classic
            // variants follow integer truthiness for backward compatibility.
            if (HasPayload) return true;
            return UnderlyingValue != 0;
        }

        public sealed override string ToString()
        {
            if (!HasPayload) return $"{EnumName}.{MemberName}";
            var sb = new StringBuilder();
            sb.Append(EnumName).Append('.').Append(MemberName).Append('(');
            for (int i = 0; i < Payload.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(RaLanguage.Utilities.StringConversionUtility.ConvertToString(Payload[i]));
            }
            sb.Append(')');
            return sb.ToString();
        }

        public override int GetHashCode()
        {
            int h = HashCode.Combine(EnumName, MemberName, VariantIndex);
            foreach (var p in Payload) h = HashCode.Combine(h, p?.GetHashCode() ?? 0);
            return h;
        }
    }
}

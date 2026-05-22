using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes;
using System.Globalization;
using RaLanguage.Utilities;

namespace RaLanguage.Types
{
    public class TypeChecker
    {
        public static RuntimeValue? GetNewType(TypeDescriptor? declaredType, RuntimeValue value, Context context, AstNode node)
        {
            if (declaredType != null)
            {
                var symbol = context.SymbolTable.Get(declaredType.Name);

                if (symbol is ClassTypeValue)
                {
                    if (value.Type != RuntimeValueType.ClassInstance) return null;
                    if (!string.Equals(((ClassInstanceValue)value).Definition.ClassName, declaredType.Name, StringComparison.Ordinal)) return null;
                    return value;
                }

                if (symbol is StructTypeValue)
                {
                    if (value.Type != RuntimeValueType.StructInstance) return null;
                    if (!string.Equals(((StructInstanceValue)value).Definition.StructName, declaredType.Name, StringComparison.Ordinal)) return null;
                    return value;
                }

                if (symbol is EnumTypeValue)
                {
                    if (value.Type != RuntimeValueType.Enum) return null;
                    if (!string.Equals(((EnumValue)value).EnumName, declaredType.Name, StringComparison.Ordinal)) return null;
                    return value;
                }
            }

            if (declaredType?.Name == null) return value;

            var kind = declaredType.PrimitiveKind;
            if (kind == PrimitiveTypeKind.None) return value;

            if (kind == PrimitiveTypeKind.String)
            {
                var strValue = StringConversionUtility.ConvertToString(value);
                return new StringValue(strValue).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
            }

            if (!TryGetRawValue(value, out object? rawValue))
                return value;

            // Int128 / UInt128: the boxed `rawValue` is rarely already an Int128;
            // for NumberValue / double-backed literals it's a `double`, and a raw
            // `(Int128)object` cast on the wrong unbox throws InvalidCastException
            // — which previously aborted the whole interpreter thread silently.
            // Parse through BigInteger so any numeric source widens cleanly, then
            // narrow into Int128 / UInt128 via their ctors.
            RuntimeValue? newValue;
            try
            {
                newValue = kind switch
                {
                    PrimitiveTypeKind.Int => new IntegerValue(Convert.ToInt32(rawValue)),
                    PrimitiveTypeKind.Number => new NumberValue(BigNumber.Parse(rawValue.ToString())),
                    PrimitiveTypeKind.Long => new LongValue(Convert.ToInt64(rawValue)),
                    PrimitiveTypeKind.Float => new FloatValue(Convert.ToSingle(rawValue)),
                    PrimitiveTypeKind.Double => new DoubleValue(Convert.ToDouble(rawValue)),
                    PrimitiveTypeKind.UInt => new UnsignedIntegerValue(Convert.ToUInt32(rawValue)),
                    PrimitiveTypeKind.ULong => new UnsignedLongValue(Convert.ToUInt64(rawValue)),
                    PrimitiveTypeKind.Short => new ShortValue(Convert.ToInt16(rawValue)),
                    PrimitiveTypeKind.UShort => new UnsignedShortValue(Convert.ToUInt16(rawValue)),
                    PrimitiveTypeKind.Int128 => new Int128Value(ToInt128(rawValue)),
                    PrimitiveTypeKind.UInt128 => new UnsignedInt128Value(ToUInt128(rawValue)),
                    PrimitiveTypeKind.Decimal => new DecimalValue(Convert.ToDecimal(rawValue)),
                    PrimitiveTypeKind.Byte => new ByteValue(Convert.ToByte(rawValue)),
                    _ => null
                };
            }
            catch (Exception)
            {
                return value;
            }

            return newValue?.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
        }

        private static bool TryGetRawValue(RuntimeValue value, out object? result)
        {
            result = value switch
            {
                IntegerValue v => v.Value,
                LongValue v => v.Value,
                FloatValue v => v.Value,
                DoubleValue v => v.Value,
                UnsignedIntegerValue v => v.Value,
                UnsignedLongValue v => v.Value,
                ShortValue v => v.Value,
                UnsignedShortValue v => v.Value,
                ByteValue v => v.Value,
                DecimalValue v => v.Value,
                Int128Value v => v.Value,
                UnsignedInt128Value v => v.Value,
                NumberValue v => ParseNumber(v.Value.ToString()),
                _ => null
            };

            return result != null;
        }

        private static object? ParseNumber(string raw)
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }

        private static Int128 ToInt128(object raw)
        {
            return raw switch
            {
                Int128 v => v,
                UInt128 v => (Int128)v,
                long v => (Int128)v,
                int v => (Int128)v,
                short v => (Int128)v,
                byte v => (Int128)v,
                ulong v => (Int128)v,
                uint v => (Int128)v,
                ushort v => (Int128)v,
                double v => (Int128)v,
                float v => (Int128)v,
                decimal v => (Int128)v,
                System.Numerics.BigInteger b => (Int128)b,
                _ => Int128.Parse(raw.ToString() ?? "0", CultureInfo.InvariantCulture),
            };
        }

        private static UInt128 ToUInt128(object raw)
        {
            return raw switch
            {
                UInt128 v => v,
                Int128 v => (UInt128)v,
                long v => (UInt128)v,
                int v => (UInt128)v,
                short v => (UInt128)v,
                byte v => (UInt128)v,
                ulong v => (UInt128)v,
                uint v => (UInt128)v,
                ushort v => (UInt128)v,
                double v => (UInt128)v,
                float v => (UInt128)v,
                decimal v => (UInt128)v,
                System.Numerics.BigInteger b => (UInt128)b,
                _ => UInt128.Parse(raw.ToString() ?? "0", CultureInfo.InvariantCulture),
            };
        }
    }
}
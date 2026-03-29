using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes;
using System.Globalization;

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

            string targetType = declaredType.Name.ToString().ToLowerInvariant();
            if (!TryGetRawValue(value, out object? rawValue))
                return value;

            RuntimeValue? newValue = targetType switch
            {
                "int" => new IntegerValue(Convert.ToInt32(rawValue)),
                "number" => new NumberValue(BigNumber.Parse(rawValue.ToString())),
                "long" => new LongValue(Convert.ToInt64(rawValue)),
                "float" => new FloatValue(Convert.ToSingle(rawValue)),
                "double"=> new DoubleValue(Convert.ToDouble(rawValue)),
                "uint" => new UnsignedIntegerValue(Convert.ToUInt32(rawValue)),
                "ulong" => new UnsignedLongValue(Convert.ToUInt64(rawValue)),
                "short" => new ShortValue(Convert.ToInt16(rawValue)),
                "ushort" => new UnsignedShortValue(Convert.ToUInt16(rawValue)),
                "int128"=> new Int128Value((Int128)rawValue),
                "uint128" => new UnsignedInt128Value((UInt128)rawValue),
                "decimal" => new DecimalValue(Convert.ToDecimal(rawValue)),
                "byte" => new ByteValue(Convert.ToByte(rawValue)),
                _ => null
            };

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
    }
}
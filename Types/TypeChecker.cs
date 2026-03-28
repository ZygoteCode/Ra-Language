using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
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
                var declName = declaredType.Name;

                var symbol = context.SymbolTable.Get(declName);

                if (symbol is ClassTypeValue)
                {
                    if (value.Type != RuntimeValueType.ClassInstance)
                        return null;

                    if (!string.Equals(((ClassInstanceValue)value).Definition.ClassName, declaredType.Name, StringComparison.Ordinal))
                        return null;

                    return value;
                }

                if (symbol is StructTypeValue)
                {
                    if (value.Type != RuntimeValueType.StructInstance)
                        return null;

                    var inst = (StructInstanceValue)value;
                    if (!string.Equals(inst.Definition.StructName, declName, StringComparison.Ordinal))
                        return null;

                    return value;
                }

                if (symbol is EnumTypeValue)
                {
                    if (value.Type != RuntimeValueType.Enum)
                        return null;

                    var ev = (EnumValue)value;
                    if (!string.Equals(ev.EnumName, declName, StringComparison.Ordinal))
                        return null;

                    return value;
                }
            }

            if (declaredType?.Name == null) return value;

            string targetType = declaredType.Name.ToString().ToLowerInvariant();
            if (!TryGetRawValue(value, out object? rawValue))
                return value;

            RuntimeValue? newValue = targetType switch
            {
                "int" or "i32" or "integer" => new IntegerValue(Convert.ToInt32(rawValue)),
                "number" => new NumberValue(BigNumber.Parse(rawValue.ToString())),
                "long" or "i64" => new LongValue(Convert.ToInt64(rawValue)),
                "float" or "f32" => new FloatValue(Convert.ToSingle(rawValue)),
                "double" or "f64" => new DoubleValue(Convert.ToDouble(rawValue)),
                "uint" or "ui32" or "unsignedinteger" => new UnsignedIntegerValue(Convert.ToUInt32(rawValue)),
                "ulong" or "ui64" or "unsignedlong" => new UnsignedLongValue(Convert.ToUInt64(rawValue)),
                "short" or "i16" or "int16" => new ShortValue(Convert.ToInt16(rawValue)),
                "ushort" or "ui16" or "uint16" => new UnsignedShortValue(Convert.ToUInt16(rawValue)),
                "int128" or "i128" or "integer128" => new Int128Value((Int128)rawValue),
                "uint128" or "ui128" or "unsignedinteger128" => new UnsignedInt128Value((UInt128)rawValue),
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
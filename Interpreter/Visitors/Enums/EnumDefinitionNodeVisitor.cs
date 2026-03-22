using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Enums;

namespace RaLanguage.Interpreter.Visitors.Enums
{
    public class EnumDefinitionNodeVisitor : NodeVisitor<EnumDefinitionNode>
    {
        protected override RuntimeResult VisitNode(EnumDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string enumName = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(enumName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid enum name", context));

            if (context.SymbolTable.Get(enumName) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{enumName}' is already defined", context));

            var members = new Dictionary<string, Int128>(StringComparer.Ordinal);
            Int128 lastValue = -1;

            foreach (var (memberTok, valueNode) in node.Members)
            {
                string memberName = memberTok.Value?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(memberName))
                    return res.Failure(new RuntimeError(memberTok.PositionStart, memberTok.PositionEnd, "Invalid enum member name", context));

                if (members.ContainsKey(memberName))
                    return res.Failure(new RuntimeError(memberTok.PositionStart, memberTok.PositionEnd, $"Duplicate enum member '{memberName}'", context));

                Int128 value;

                if (valueNode != null)
                {
                    var val = res.Register(interpreter.Visit(valueNode, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    var tempValue = ExtractEnumInt128(val, valueNode, context);

                    if (tempValue.Item2 != null)
                    {
                        return res.Failure(tempValue.Item2);
                    }

                    value = tempValue.Item1.Value;
                }
                else
                {
                    value = lastValue + 1;
                }

                members[memberName] = value;
                lastValue = value;
            }

            var enumTypeValue = new EnumTypeValue(enumName, members)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(enumName, enumTypeValue);
            return res.Success(enumTypeValue);
        }

        private (Int128?, Error?) ExtractEnumInt128(RuntimeValue value, AstNode sourceNode, Context context)
        {
            switch (value.Type)
            {
                case RuntimeValueType.Byte:
                    return (((ByteValue)value).Value, null);
                case RuntimeValueType.Short:
                    return (((ShortValue)value).Value, null);
                case RuntimeValueType.UnsignedShort:
                    return (((UnsignedShortValue)value).Value, null);
                case RuntimeValueType.Integer:
                    return (((IntegerValue)value).Value, null);
                case RuntimeValueType.UnsignedInteger:
                    return (((UnsignedIntegerValue)value).Value, null);
                case RuntimeValueType.Long:
                    return (((LongValue)value).Value, null);
                case RuntimeValueType.UnsignedLong:
                    return ((Int128)((UnsignedLongValue)value).Value, null);
                case RuntimeValueType.Int128:
                    return (((Int128Value)value).Value, null);
                case RuntimeValueType.UnsignedInt128:
                    {
                        var u = ((UnsignedInt128Value)value).Value;
                        if (u > (UInt128)Int128.MaxValue)
                            return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value is too large", context));

                        return ((Int128)u, null);
                    }
                case RuntimeValueType.Float:
                    {
                        float f = ((FloatValue)value).Value;
                        if (MathF.Abs(f - MathF.Truncate(f)) > 0.000001f)
                            return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value must be an integer", context));

                        return ((Int128)f, null);
                    }
                case RuntimeValueType.Double:
                    {
                        double d = ((DoubleValue)value).Value;
                        if (Math.Abs(d - Math.Truncate(d)) > 0.000001d)
                            return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value must be an integer", context));

                        return ((Int128)d, null);
                    }
                case RuntimeValueType.Decimal:
                    {
                        decimal d = ((DecimalValue)value).Value;
                        if (d != decimal.Truncate(d))
                            return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value must be an integer", context));

                        return ((Int128)d, null);
                    }
                case RuntimeValueType.Number:
                    {
                        var s = ((NumberValue)value).Value.ToString();
                        if (!Int128.TryParse(s, out var i))
                            return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value must be an integer", context));

                        return (i, null);
                    }
                default:
                    return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, "Enum value must be numeric", context));
            }
        }
    }
}
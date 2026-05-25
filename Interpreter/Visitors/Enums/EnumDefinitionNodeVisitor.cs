using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Enums;

namespace RaLanguage.Interpreter.Visitors.Enums
{
    public class EnumDefinitionNodeVisitor : NodeVisitor<EnumDefinitionNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(EnumDefinitionNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(EnumDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string enumName = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(enumName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid enum name", context));

            if (context.SymbolTable.Get(enumName) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{enumName}' is already defined", context));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var variants = new List<EnumVariantInfo>(node.Variants.Count);
            Int128 lastValue = -1;

            for (int i = 0; i < node.Variants.Count; i++)
            {
                var spec = node.Variants[i];
                string memberName = spec.Name;
                if (string.IsNullOrWhiteSpace(memberName))
                    return res.Failure(new RuntimeError(spec.MemberTok.PositionStart, spec.MemberTok.PositionEnd, "Invalid enum member name", context));

                if (!seen.Add(memberName))
                    return res.Failure(new RuntimeError(spec.MemberTok.PositionStart, spec.MemberTok.PositionEnd, $"Duplicate enum variant '{memberName}'", context));

                Int128 value;
                if (spec.ValueNode != null)
                {
                    var val = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(spec.ValueNode, context, interpreter));
                    if (res.ShouldReturn()) return res;
                    var (parsed, err) = ExtractEnumInt128(val!, spec.ValueNode, context);
                    if (err != null) return res.Failure(err);
                    value = parsed!.Value;
                }
                else
                {
                    value = lastValue + 1;
                }

                lastValue = value;

                IReadOnlyList<RaLanguage.Types.TypeDescriptor>? payloadTypes = spec.PayloadTypes;
                variants.Add(new EnumVariantInfo(memberName, i, value, payloadTypes));
            }

            var enumTypeValue = new EnumTypeValue(enumName, variants, node.GenericTypeParams, node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(enumName, enumTypeValue);

            if (node.HasAnnotations)
            {
                var target = new MetadataTarget(AnnotationTargetKind.Enum, null, enumName);
                var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(enumTypeValue);
        }

        private static (Int128?, Error?) ExtractEnumInt128(RuntimeValue value, AstNode sourceNode, Context context)
        {
            switch (value.Type)
            {
                case RuntimeValueType.Byte:           return (((ByteValue)value).Value, null);
                case RuntimeValueType.Short:          return (((ShortValue)value).Value, null);
                case RuntimeValueType.UnsignedShort:  return (((UnsignedShortValue)value).Value, null);
                case RuntimeValueType.Integer:        return (((IntegerValue)value).Value, null);
                case RuntimeValueType.UnsignedInteger:return (((UnsignedIntegerValue)value).Value, null);
                case RuntimeValueType.Long:           return (((LongValue)value).Value, null);
                case RuntimeValueType.UnsignedLong:   return ((Int128)((UnsignedLongValue)value).Value, null);
                case RuntimeValueType.Int128:         return (((Int128Value)value).Value, null);
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

using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class TypeofNodeVisitor : NodeVisitor<TypeofNode>
    {
        protected sealed override RuntimeResult VisitNode(TypeofNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var value = res.Register(interpreter.Visit(node.Node, context));
            if (res.Error != null) return res;

            string type = value.Type switch
            {
                RuntimeValueType.Number => "number",
                RuntimeValueType.String => "string",
                RuntimeValueType.List => "list",
                RuntimeValueType.Function => "function",
                RuntimeValueType.Null => "null",
                RuntimeValueType.Boolean => "bool",
                RuntimeValueType.Set => "set",
                RuntimeValueType.Map => "map",
                RuntimeValueType.Tuple => "tuple",
                RuntimeValueType.Integer => "int",
                RuntimeValueType.Long => "long",
                RuntimeValueType.Double => "double",
                RuntimeValueType.Float => "float",
                RuntimeValueType.UnsignedInteger => "uint",
                RuntimeValueType.UnsignedLong => "ulong",
                RuntimeValueType.Short => "short",
                RuntimeValueType.UnsignedShort => "ushort",
                RuntimeValueType.Int128 => "int128",
                RuntimeValueType.UnsignedInt128 => "uint128",
                RuntimeValueType.Decimal => "decimal",
                RuntimeValueType.Byte => "byte",
                RuntimeValueType.EnumType => "enum",
                RuntimeValueType.Enum => ((EnumValue)value).EnumName,
                RuntimeValueType.StructInstance => ((StructInstanceValue)value).Definition.StructName,
                RuntimeValueType.StructType => "struct",
                RuntimeValueType.ClassType => "class",
                RuntimeValueType.ClassInstance => ((ClassInstanceValue)value).Definition.ClassName,
                RuntimeValueType.Super => "super",
                RuntimeValueType.InterfaceType => "interface",
                RuntimeValueType.TraitType => "trait",
                _ => ""
            };

            return res.Success(new StringValue(type).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}
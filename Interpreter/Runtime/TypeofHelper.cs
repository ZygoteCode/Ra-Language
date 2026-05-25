using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Parser.Nodes.Special;
using System.Linq;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of TypeofNodeVisitor. Maps a RuntimeValue's Type to its
    // canonical type-name string. Native VM opcode OP_TYPEOF calls Apply()
    // with the pre-evaluated value; AST visitor does the same.
    public static class TypeofHelper
    {
        public static RuntimeResult Apply(TypeofNode node, Context context, RuntimeValue value)
        {
            var res = new RuntimeResult();
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
                RuntimeValueType.Tuple => DescribeTuple(value),
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
                RuntimeValueType.ClassInstance => DescribeClassInstance((ClassInstanceValue)value),
                RuntimeValueType.Super => "super",
                RuntimeValueType.InterfaceType => "interface",
                RuntimeValueType.TraitType => "trait",
                RuntimeValueType.ModuleWrapper => "module",
                RuntimeValueType.Namespace => "namespace",
                RuntimeValueType.GenericTypeBinding => ((GenericTypeValue)value).BoundType?.ToString() ?? "type",
                _ => ""
            };
            return res.Success(new StringValue(type).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private static string DescribeTuple(RuntimeValue value)
        {
            if (value is TupleValue t)
            {
                var parts = t.Elements.Select(e => RaLanguage.Types.TypeSystem.GetDescriptorFromRuntimeValue(e).ToString());
                return $"({string.Join(", ", parts)})";
            }
            return "tuple";
        }

        private static string DescribeClassInstance(ClassInstanceValue inst)
        {
            var name = inst.Definition.ClassName;
            if (inst.GenericBindings != null && inst.Definition.GenericTypeParams.Count > 0)
            {
                var parts = inst.Definition.GenericTypeParams
                    .Select(p => inst.GenericBindings.TryGetValue(p, out var b) ? b.ToString() : p);
                return $"{name}<{string.Join(", ", parts)}>";
            }
            return name;
        }
    }
}

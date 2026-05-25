using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Enums;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of EnumAccessNodeVisitor — `EnumType.Variant`. Apply
    // takes the already-evaluated enum-type value (from
    // VarAccess / MemberAccess chain) and returns the variant.
    public static class EnumAccessHelper
    {
        public static RuntimeResult Apply(EnumAccessNode node, Context context, RuntimeValue enumValue)
        {
            var res = new RuntimeResult();
            if (enumValue.Type != RuntimeValueType.EnumType)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "Left side of '.' must be an enum type", context));

            var enumType = (EnumTypeValue)enumValue;
            string memberName = node.MemberTok.Value?.ToString() ?? "";
            if (!enumType.HasMember(memberName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"Enum '{enumType.EnumName}' has no member '{memberName}'", context));

            return res.Success(enumType.GetMember(memberName));
        }
    }
}

using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Enums;

namespace RaLanguage.Interpreter.Visitors.Enums
{
    public class EnumAccessNodeVisitor : NodeVisitor<EnumAccessNode>
    {
        protected sealed override RuntimeResult VisitNode(EnumAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var enumValue = res.Register(interpreter.Visit(node.EnumNode, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            if (enumValue.Type != RuntimeValueType.EnumType)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    "Left side of '.' must be an enum type",
                    context));
            }

            var enumType = (EnumTypeValue)enumValue;
            string memberName = node.MemberTok.Value?.ToString() ?? "";

            if (!enumType.HasMember(memberName))
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    $"Enum '{enumType.EnumName}' has no member '{memberName}'",
                    context));
            }

            return res.Success(enumType.GetMember(memberName));
        }
    }
}
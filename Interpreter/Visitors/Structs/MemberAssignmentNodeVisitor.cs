using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Visitors.Members
{
    public class MemberAssignmentNodeVisitor : NodeVisitor<MemberAssignmentNode>
    {
        protected sealed override RuntimeResult VisitNode(MemberAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var baseTarget = res.Register(interpreter.Visit(node.TargetNode.TargetNode, context));
            if (res.ShouldReturn()) return res;

            string memberName = node.TargetNode.MemberTok.Value?.ToString() ?? "";

            var value = res.Register(interpreter.Visit(node.ValueNode, context));
            if (res.ShouldReturn()) return res;

            if (baseTarget.Type == RuntimeValueType.StructInstance)
            {
                var instance = (StructInstanceValue)baseTarget;

                if (!instance.HasField(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Struct '{instance.Definition.StructName}' has no field '{memberName}'", context));

                if (!instance.IsFieldPublic(memberName) && !IsInsideSameStruct(context, instance.Definition.StructName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Field '{memberName}' is not public", context));

                instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Left side of assignment must be a struct field", context));
        }

        private bool IsInsideSameStruct(Context context, string structName)
        {
            var selfEntry = context.SymbolTable.GetEntry("self");
            if (selfEntry == null) return false;

            if (selfEntry.Value.Type != RuntimeValueType.StructInstance) return false;

            var self = (StructInstanceValue)selfEntry.Value;
            return string.Equals(self.Definition.StructName, structName, StringComparison.Ordinal);
        }
    }
}
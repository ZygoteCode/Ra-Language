using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

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

                if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.StructName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Field '{memberName}' is not public", context));

                instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (baseTarget.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)baseTarget;

                if (!instance.HasField(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{instance.Definition.ClassName}' has no field '{memberName}'", context));

                if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.ClassName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Field '{memberName}' is not public", context));

                var fieldType = instance.GetFieldType(memberName);
                if (fieldType != null && !TypeSystem.IsAssignable(context, fieldType, value))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch for field '{memberName}'", context));

                instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Left side of assignment must be a struct field", context));
        }

        private bool IsInsideSameType(Context context, string typeName)
        {
            var selfEntry = context.SymbolTable.GetEntry("self");
            if (selfEntry == null) return false;

            if (selfEntry.Value.Type == RuntimeValueType.StructInstance)
                return string.Equals(((StructInstanceValue)selfEntry.Value).Definition.StructName, typeName, StringComparison.Ordinal);

            if (selfEntry.Value.Type == RuntimeValueType.ClassInstance)
                return string.Equals(((ClassInstanceValue)selfEntry.Value).Definition.ClassName, typeName, StringComparison.Ordinal);

            return false;
        }
    }
}
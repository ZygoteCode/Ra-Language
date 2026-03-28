using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Visitors.Members
{
    public class MemberAccessNodeVisitor : NodeVisitor<MemberAccessNode>
    {
        protected sealed override RuntimeResult VisitNode(MemberAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var target = res.Register(interpreter.Visit(node.TargetNode, context));
            if (res.ShouldReturn()) return res;

            string memberName = node.MemberTok.Value?.ToString() ?? "";

            if (target.Type == RuntimeValueType.EnumType)
            {
                var enumType = (EnumTypeValue)target;
                if (!enumType.HasMember(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Enum '{enumType.EnumName}' has no member '{memberName}'", context));

                return res.Success(enumType.GetMember(memberName));
            }

            if (target.Type == RuntimeValueType.StructInstance)
            {
                var instance = (StructInstanceValue)target;

                if (instance.HasField(memberName))
                {
                    if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Field '{memberName}' is not public", context));

                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var method = instance.Definition.GetMethod(memberName);
                if (method != null)
                {
                    if (!method.IsPublic && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Method '{memberName}' is not public", context));

                    return res.Success(new BoundStructMethodValue(instance.Definition, instance, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Struct '{instance.Definition.StructName}' has no member '{memberName}'", context));
            }

            if (target.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)target;

                if (instance.HasField(memberName))
                {
                    if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.ClassName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Field '{memberName}' is not public", context));

                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var method = instance.Definition.GetMethod(memberName);
                if (method != null)
                {
                    if (!method.IsPublic && !IsInsideSameType(context, instance.Definition.ClassName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Method '{memberName}' is not public", context));

                    return res.Success(new BoundClassMethodValue(instance.Definition, instance, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{instance.Definition.ClassName}' has no member '{memberName}'", context));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Member access is only valid on structs or enum types", context));
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
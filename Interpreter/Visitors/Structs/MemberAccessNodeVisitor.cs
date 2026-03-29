using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
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
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var native = instance.Definition.ResolveInstanceMethods(memberName);
                if (native.Count > 0)
                    return res.Success(new BoundClassMethodGroupValue(instance.Definition, instance, native).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var ext = context.Extensions.Resolve(instance, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{instance.Definition.ClassName}' has no member '{memberName}'", context));
            }

            if (target.Type == RuntimeValueType.Super)
            {
                var sup = (SuperProxyValue)target;
                if (sup.BaseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "No base class available", context));

                if (sup.Instance.HasField(memberName))
                    return res.Success(sup.Instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var candidates = sup.BaseClass.ResolveCandidates(memberName);
                if (candidates.Count > 0)
                    return res.Success(new BoundMethodGroupValue(memberName, sup.Instance, sup.BaseClass, candidates).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Base class '{sup.BaseClass.ClassName}' has no member '{memberName}'", context));
            }

            if (target.Type == RuntimeValueType.ClassType)
            {
                var classType = (ClassTypeValue)target;

                if (classType.HasStaticField(memberName))
                {
                    return res.Success(
                        classType.StaticFields[memberName]
                            .SetContext(context)
                            .SetPos(node.PositionStart, node.PositionEnd));
                }

                if (classType.TryGetStaticMethodOwner(memberName, out var owner, out var method) && method != null)
                {
                    return res.Success(new BoundClassMethodValue(owner, null, method, isStatic: true)
                        .SetContext(context)
                        .SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{classType.ClassName}' has no static member '{memberName}'", context));
            }

            if (target.Type == RuntimeValueType.Enum || target.Type == RuntimeValueType.EnumType ||
                target.Type == RuntimeValueType.String || target.Type == RuntimeValueType.Number ||
                target.Type == RuntimeValueType.Integer || target.Type == RuntimeValueType.Long ||
                target.Type == RuntimeValueType.Float || target.Type == RuntimeValueType.Double ||
                target.Type == RuntimeValueType.UnsignedInteger || target.Type == RuntimeValueType.UnsignedLong ||
                target.Type == RuntimeValueType.Short || target.Type == RuntimeValueType.UnsignedShort ||
                target.Type == RuntimeValueType.Int128 || target.Type == RuntimeValueType.UnsignedInt128 ||
                target.Type == RuntimeValueType.Decimal || target.Type == RuntimeValueType.Byte ||
                target.Type == RuntimeValueType.List || target.Type == RuntimeValueType.Set ||
                target.Type == RuntimeValueType.Map || target.Type == RuntimeValueType.Tuple ||
                target.Type == RuntimeValueType.Boolean || target.Type == RuntimeValueType.Null)
            {
                var ext = context.Extensions.Resolve(target, memberName);

                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
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
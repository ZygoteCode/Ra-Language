using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Visitors.Members
{
    public class MemberAccessNodeVisitor : NodeVisitor<MemberAccessNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(MemberAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var target = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.TargetNode, context, interpreter));
            if (res.ShouldReturn()) return res;

            string memberName = node.MemberTok.Value?.ToString() ?? "";

            if (target.Type == RuntimeValueType.EnumType)
            {
                var enumType = (EnumTypeValue)target;
                if (!enumType.HasMember(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"enum '{enumType.EnumName}' has no member '{memberName}'",
                        context,
                        code: DiagnosticCode.RuntimeUndefinedSymbol,
                        primaryLabel: $"'{memberName}' is not a variant",
                        help: $"available variants: {string.Join(", ", enumType.VariantsByName.Keys)}"));

                return res.Success(enumType.GetMember(memberName));
            }

            // Predicate methods — delegate to the shared resolver so the
            // AST-walk path stays in lock-step with the IR/VM path.
            if (target.Type == RuntimeValueType.Predicate)
                return MemberAccessHelper.Apply(node, context, target);

            if (target.Type == RuntimeValueType.StructInstance || target.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)target;

                if (instance.HasField(memberName))
                {
                    if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"field '{memberName}' of struct '{instance.Definition.StructName}' is private",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "accessed from outside the declaring struct",
                            help: "mark the field with 'pub' to expose it, or access it only from within the struct's own methods"));

                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var method = instance.Definition.GetMethod(memberName);
                if (method != null)
                {
                    if (!method.IsPublic && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"method '{memberName}' of struct '{instance.Definition.StructName}' is private",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "called from outside the declaring struct",
                            help: "mark the method with 'pub' to expose it"));

                    return res.Success(new BoundStructMethodValue(instance.Definition, instance, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (Runtime.ExtensionDispatch.TryGetField(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtField))
                    return sExtField;

                if (Runtime.ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtEv))
                    return sExtEv;

                if (Runtime.ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtProp))
                    return sExtProp;

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"struct '{instance.Definition.StructName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the struct definition / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)target;

                if (instance.HasField(memberName))
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var native = instance.Definition.ResolveInstanceMethods(memberName);
                if (native.Count > 0)
                    return res.Success(new BoundClassMethodGroupValue(instance.Definition, instance, native).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                if (Runtime.ExtensionDispatch.TryGetField(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtField))
                    return cExtField;

                if (Runtime.ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtEv))
                    return cExtEv;

                if (Runtime.ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtProp))
                    return cExtProp;

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{instance.Definition.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the class / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.Super)
            {
                var sup = (SuperProxyValue)target;
                if (sup.BaseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        "'super' cannot resolve a base class",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "no base class is in scope here",
                        help: "'super' is only meaningful inside methods of a class that extends another via ':'"));

                if (sup.Instance.HasField(memberName))
                    return res.Success(sup.Instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var candidates = sup.BaseClass.ResolveCandidates(memberName);
                if (candidates.Count > 0)
                    return res.Success(new BoundMethodGroupValue(memberName, sup.Instance, sup.BaseClass, candidates).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"base class '{sup.BaseClass.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such inherited field or method",
                    help: "verify the name and visibility of the inherited member"));
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

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{classType.ClassName}' has no static member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such static field or method",
                    help: $"check the spelling, or declare '{memberName}' with 'static' inside class '{classType.ClassName}'"));
            }

            if (target.Type == RuntimeValueType.Namespace)
            {
                var ns = (NamespaceValue)target;
                var entry = ns.Members.GetLocalEntry(memberName);

                if (entry == null || !entry.IsPublic)
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart, node.PositionEnd,
                        $"Namespace '{(ns.IsRoot ? "<global>" : ns.QualifiedName)}' has no public member '{memberName}'",
                        context));
                }

                return res.Success(entry.Value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (target.Type == RuntimeValueType.ModuleWrapper)
            {
                var moduleWrapper = (ModuleWrapperValue)target;
                var ext = moduleWrapper.Module.Extensions.ResolveMethodEntries(target, memberName);

                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Success(moduleWrapper.Module.SymbolTable.Get(memberName));
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
                if (Runtime.ExtensionDispatch.TryGetProperty(target, memberName, context, node.PositionStart, node.PositionEnd, out var pExtProp))
                    return pExtProp;

                var ext = context.Extensions.ResolveMethodEntries(target, memberName);

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
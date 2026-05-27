using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of MemberAssignmentNodeVisitor. The visitor evaluates
    // owner + value sub-expressions and then calls Apply(); the VM's
    // OP_SET_MEMBER pre-evaluates both into slots and calls Apply()
    // directly.
    public static class MemberAssignmentHelper
    {
        public static RuntimeResult Apply(
            MemberAssignmentNode node, Context context, RuntimeValue owner, RuntimeValue value)
        {
            var res = new RuntimeResult();
            string memberName = node.TargetNode.MemberTok.Value?.ToString() ?? "";

            if (owner.Type == RuntimeValueType.StructInstance || owner.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)owner;

                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.StructName);
                    var setTask = PropertyAccessOps.Set(instance, propDesc, value, context,
                        node.PositionStart, node.PositionEnd, isInside, context.IsInConstructor);
                    return setTask.IsCompletedSuccessfully ? setTask.Result : SyncAwait.Get(setTask);
                }

                if (ExtensionDispatch.TrySetField(instance, memberName, value, context, node.PositionStart, node.PositionEnd, out var sExtFieldSet))
                    return sExtFieldSet;

                if (ExtensionDispatch.TrySetProperty(instance, memberName, value, context, node.PositionStart, node.PositionEnd, out var sExtSet))
                    return sExtSet;

                if (!instance.HasField(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"{(owner.Type == RuntimeValueType.RecordInstance ? "Record" : "Struct")} '{instance.Definition.StructName}' has no field '{memberName}'", context));

                var fieldDeclType = instance.GetFieldDeclarationType(memberName);
                if (fieldDeclType == VariableDeclarationType.CONST)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"'{memberName}' is a const field and cannot be modified", context));
                if (fieldDeclType == VariableDeclarationType.FINAL)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"'{memberName}' is a final field and cannot be modified after initialization", context));
                if (fieldDeclType == VariableDeclarationType.LET)
                {
                    var currentValue = instance.Fields[memberName];
                    if (currentValue.Type != RuntimeValueType.Null && !currentValue.IsCopy)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"'{memberName}' is a let field and was already assigned", context));
                }

                instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (owner.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)owner;

                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    bool isInside = IsInsideClassHierarchyFor(context, instance.Definition);
                    var setTask = PropertyAccessOps.Set(instance, propDesc, value, context,
                        node.PositionStart, node.PositionEnd, isInside, context.IsInConstructor);
                    return setTask.IsCompletedSuccessfully ? setTask.Result : SyncAwait.Get(setTask);
                }

                // Extension field set — checked before native field
                // path? No: native field wins. Place after the native
                // HasField check but before the static-field fallback.
                if (!instance.HasField(memberName)
                    && ExtensionDispatch.TrySetField(instance, memberName, value, context, node.PositionStart, node.PositionEnd, out var cExtFieldSet))
                    return cExtFieldSet;

                // Extension property setters lose to native fields
                // (which take precedence at the resolution chain) but
                // win over the static-field fallback below.
                if (!instance.HasField(memberName)
                    && ExtensionDispatch.TrySetProperty(instance, memberName, value, context, node.PositionStart, node.PositionEnd, out var cExtSet))
                    return cExtSet;

                if (instance.HasField(memberName))
                {
                    var fieldDeclType = instance.GetFieldDeclarationType(memberName);
                    if (fieldDeclType == VariableDeclarationType.CONST)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"'{memberName}' is a const field and cannot be modified", context));
                    if (fieldDeclType == VariableDeclarationType.FINAL)
                    {
                        if (!context.IsInConstructor)
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"'{memberName}' is a final field and can only be assigned in the constructor", context));
                    }
                    else if (fieldDeclType == VariableDeclarationType.LET)
                    {
                        var currentValue = instance.Fields[memberName];
                        if (currentValue.Type != RuntimeValueType.Null && !currentValue.IsCopy)
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"'{memberName}' is a let field and was already assigned", context));
                    }

                    var fieldKey = MetadataTarget.BuildKey(AnnotationTargetKind.Field, instance.Definition.ClassName, memberName);
                    var (coerced, verr) = AnnotationValidator.CoerceAndValidate(fieldKey, value, $"field '{instance.Definition.ClassName}.{memberName}'", context);
                    if (verr != null) return res.Failure(verr);
                    value = coerced;

                    instance.SetMember(memberName, value);
                    return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (instance.Definition.HasStaticField(memberName))
                {
                    var fieldType = instance.Definition.GetStaticFieldType(memberName);
                    if (fieldType != null && !TypeSystem.IsAssignable(context, fieldType, value))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"Type mismatch for static field '{memberName}'", context));
                    instance.Definition.SetStaticField(memberName, value, instance.Definition.IsStaticFieldPublic(memberName), fieldType);
                    return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"Class '{instance.Definition.ClassName}' has no field or static field '{memberName}'", context));
            }

            if (owner.Type == RuntimeValueType.Super)
            {
                var sup = (SuperProxyValue)owner;
                if (sup.BaseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "No base class available", context));
                if (!sup.Instance.HasField(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Base class has no field '{memberName}'", context));
                var fieldDeclType = sup.Instance.GetFieldDeclarationType(memberName);
                if (fieldDeclType == VariableDeclarationType.CONST)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a const field and cannot be modified", context));
                if (fieldDeclType == VariableDeclarationType.FINAL)
                {
                    if (!context.IsInConstructor)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a final field and can only be assigned in the constructor", context));
                }
                else if (fieldDeclType == VariableDeclarationType.LET)
                {
                    var currentValue = sup.Instance.Fields[memberName];
                    if (currentValue.Type != RuntimeValueType.Null && !currentValue.IsCopy)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a let field and was already assigned", context));
                }
                sup.Instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (owner.Type == RuntimeValueType.ClassType)
            {
                var classType = (ClassTypeValue)owner;
                if (classType.HasStaticField(memberName))
                {
                    var fieldType = classType.GetStaticFieldType(memberName);
                    if (fieldType != null && !TypeSystem.IsAssignable(context, fieldType, value))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch for static field '{memberName}'", context));
                    classType.SetStaticField(memberName, value, classType.IsStaticFieldPublic(memberName), fieldType);
                    return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                if (ExtensionDispatch.TrySetField(classType, memberName, value, context, node.PositionStart, node.PositionEnd, out var staticExtFieldSet))
                    return staticExtFieldSet;
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{classType.ClassName}' has no static field '{memberName}'", context));
            }

            if (owner.Type == RuntimeValueType.ModuleWrapper)
            {
                var moduleWrapper = (ModuleWrapperValue)owner;
                var ext = moduleWrapper.Module.Extensions.Resolve(owner, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(owner, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                moduleWrapper.Module.SymbolTable.Set(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                "Left side of assignment must be a struct/class field", context));
        }

        private static bool IsInsideSameType(Context context, string typeName)
        {
            var selfEntry = context.SymbolTable!.GetEntry("self");
            if (selfEntry == null) return false;
            if (selfEntry.Value.Type == RuntimeValueType.StructInstance)
                return string.Equals(((StructInstanceValue)selfEntry.Value).Definition.StructName, typeName, System.StringComparison.Ordinal);
            if (selfEntry.Value.Type == RuntimeValueType.ClassInstance)
                return string.Equals(((ClassInstanceValue)selfEntry.Value).Definition.ClassName, typeName, System.StringComparison.Ordinal);
            return false;
        }

        private static bool IsInsideClassHierarchyFor(Context context, ClassTypeValue decl)
        {
            var selfEntry = context.SymbolTable!.GetEntry("self");
            if (selfEntry == null) return false;
            if (selfEntry.Value.Type != RuntimeValueType.ClassInstance) return false;
            var inst = (ClassInstanceValue)selfEntry.Value;
            return inst.Definition.InheritsFrom(decl.ClassName);
        }
    }
}

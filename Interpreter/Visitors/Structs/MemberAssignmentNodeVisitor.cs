using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Members
{
    public class MemberAssignmentNodeVisitor : NodeVisitor<MemberAssignmentNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(MemberAssignmentNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(MemberAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var owner = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.TargetNode.TargetNode, context, interpreter));
            if (res.ShouldReturn()) return res;

            string memberName = node.TargetNode.MemberTok.Value?.ToString() ?? "";

            var value = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.ValueNode, context, interpreter));
            if (res.ShouldReturn()) return res;

            if (owner.Type == RuntimeValueType.StructInstance || owner.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)owner;
                if (!instance.HasField(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"{(owner.Type == RuntimeValueType.RecordInstance ? "Record" : "Struct")} '{instance.Definition.StructName}' has no field '{memberName}'", context));

                var fieldDeclType = instance.GetFieldDeclarationType(memberName);
                if (fieldDeclType == VariableDeclarationType.CONST)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a const field and cannot be modified", context));
                else if (fieldDeclType == VariableDeclarationType.FINAL)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a final field and cannot be modified after initialization", context));
                else if (fieldDeclType == VariableDeclarationType.LET)
                {
                    var currentValue = instance.Fields[memberName];
                    if (currentValue.Type != RuntimeValueType.Null && !currentValue.IsCopy)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a let field and was already assigned", context));
                }

                instance.SetMember(memberName, value);
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (owner.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)owner;

                if (instance.HasField(memberName))
                {
                    var fieldDeclType = instance.GetFieldDeclarationType(memberName);
                    if (fieldDeclType == VariableDeclarationType.CONST)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a const field and cannot be modified", context));
                    else if (fieldDeclType == VariableDeclarationType.FINAL)
                    {
                        if (!context.IsInConstructor)
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a final field and can only be assigned in the constructor", context));
                    }
                    else if (fieldDeclType == VariableDeclarationType.LET)
                    {
                        var currentValue = instance.Fields[memberName];
                        if (currentValue.Type != RuntimeValueType.Null && !currentValue.IsCopy)
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{memberName}' is a let field and was already assigned", context));
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
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch for static field '{memberName}'", context));

                    instance.Definition.SetStaticField(memberName, value, instance.Definition.IsStaticFieldPublic(memberName), fieldType);
                    return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Class '{instance.Definition.ClassName}' has no field or static field '{memberName}'", context));
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
                else if (fieldDeclType == VariableDeclarationType.FINAL)
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

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Left side of assignment must be a struct/class field", context));
        }
    }
}
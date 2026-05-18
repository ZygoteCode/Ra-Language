using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Structs
{
    public class StructDefinitionNodeVisitor : NodeVisitor<StructDefinitionNode>
    {
        protected sealed override RuntimeResult VisitNode(StructDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var name = node.NameTok.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid struct name", context));

            if (context.SymbolTable.Get(name) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is already defined", context));

            var value = new StructTypeValue(name, node.IsPublic, node.Fields, node.Methods, node.Operators, node.GenericTypeParams, node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            foreach (var field in node.Fields)
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";
                
                if (field.DeclarationType == VariableDeclarationType.CONST && field.DefaultValueNode == null)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Const field '{fieldName}' must be initialized with a value",
                        context));
                }
            }

            context.SymbolTable.Set(
                name,
                value,
                isLet: true,
                declaredType: new TypeDescriptor(name),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            ValidateToStringMethod(node, context, ref res);
            if (res.ShouldReturn()) return res;

            var structTarget = new MetadataTarget(AnnotationTargetKind.Struct, null, name);
            if (node.HasAnnotations)
            {
                var annErr = AnnotationProcessor.Process(node.Annotations, structTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var field in node.Fields)
            {
                if (!field.HasAnnotations) continue;
                var fieldTarget = new MetadataTarget(AnnotationTargetKind.Field, name, field.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(field.Annotations, fieldTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var method in node.Methods)
            {
                if (!method.HasAnnotations) continue;
                var methodTarget = new MetadataTarget(AnnotationTargetKind.Method, name, method.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(method.Annotations, methodTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(value);
        }

        private void ValidateToStringMethod(StructDefinitionNode node, Context context, ref RuntimeResult res)
        {
            var toStringMethod = node.Methods.FirstOrDefault(m => 
                string.Equals(m.NameTok.Value?.ToString(), "to_string", StringComparison.Ordinal));

            if (toStringMethod == null)
                return;

            if (toStringMethod.ArgNameToks.Count > 0)
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart,
                    toStringMethod.PositionEnd,
                    $"Method 'to_string' must not have parameters",
                    context));
                return;
            }

            if (toStringMethod.ReturnType == null || 
                !string.Equals(toStringMethod.ReturnType.Name, "string", StringComparison.Ordinal))
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart,
                    toStringMethod.PositionEnd,
                    $"Method 'to_string' must return type 'string'",
                    context));
                return;
            }
        }
    }
}
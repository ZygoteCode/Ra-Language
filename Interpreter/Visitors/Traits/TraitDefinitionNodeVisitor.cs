using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Traits
{
    public class TraitDefinitionNodeVisitor : NodeVisitor<TraitDefinitionNode>
    {
        protected override RuntimeResult VisitNode(TraitDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var traitName = node.NameTok.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(traitName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid trait name", context));

            if (context.SymbolTable.Get(traitName) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{traitName}' is already defined", context));

            foreach (var field in node.Fields)
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";

                if (field.DeclarationType == VariableDeclarationType.CONST)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Trait field '{fieldName}' cannot be 'const'. Traits cannot have default values, so const is not meaningful",
                        context));
                }
                
                if (field.FieldType == null)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        "Trait fields must have a type declaration",
                        context));
                }
            }

            var traitValue = new TraitTypeValue(traitName, node.IsPublic, node.Methods, node.Fields, node.GenericTypeParams, node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                traitName,
                traitValue,
                isLet: true,
                declaredType: new TypeDescriptor(traitName),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            if (node.HasAnnotations)
            {
                var target = new MetadataTarget(AnnotationTargetKind.Trait, null, traitName);
                var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var method in node.Methods)
            {
                if (!method.HasAnnotations) continue;
                var t = new MetadataTarget(AnnotationTargetKind.Method, traitName, method.NameTok?.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(method.Annotations, t, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var field in node.Fields)
            {
                if (!field.HasAnnotations) continue;
                var t = new MetadataTarget(AnnotationTargetKind.Field, traitName, field.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(field.Annotations, t, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(traitValue);
        }
    }
}
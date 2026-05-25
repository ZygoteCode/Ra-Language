using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Interfaces
{
    public class InterfaceDefinitionNodeVisitor : NodeVisitor<InterfaceDefinitionNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(InterfaceDefinitionNode node, Context context, IInterpreter interpreter)
            => Apply(node, context, interpreter);

        public static RuntimeResult Apply(InterfaceDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var name = node.NameTok.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid interface name", context));

            if (context.SymbolTable.Get(name) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is already defined", context));

            foreach (var field in node.Fields)
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";
                
                if (field.DeclarationType == VariableDeclarationType.FINAL)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Interface field '{fieldName}' cannot be 'final'. Only 'const' or 'var' are allowed in interfaces",
                        context));
                }
                
                if (field.DeclarationType == VariableDeclarationType.LET)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Interface field '{fieldName}' cannot be 'let'. Only 'const' or 'var' are allowed in interfaces",
                        context));
                }
                
                if (field.DefaultValueNode != null)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        "Interface fields cannot have default values",
                        context));
                }

                if (field.FieldType == null)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        "Interface fields must have a type declaration",
                        context));
                }
            }

            var iface = new InterfaceTypeValue(name, node.Methods, node.Fields, node.GenericTypeParams, node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                name,
                iface,
                isLet: true,
                declaredType: new TypeDescriptor(name),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            if (node.HasAnnotations)
            {
                var target = new MetadataTarget(AnnotationTargetKind.Interface, null, name);
                var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var method in node.Methods)
            {
                if (!method.HasAnnotations) continue;
                var t = new MetadataTarget(AnnotationTargetKind.Method, name, method.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(method.Annotations, t, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var field in node.Fields)
            {
                if (!field.HasAnnotations) continue;
                var t = new MetadataTarget(AnnotationTargetKind.Field, name, field.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(field.Annotations, t, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(iface);
        }
    }
}
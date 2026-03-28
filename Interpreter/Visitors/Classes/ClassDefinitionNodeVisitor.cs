using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Classes
{
    public class ClassDefinitionNodeVisitor : NodeVisitor<ClassDefinitionNode>
    {
        protected override RuntimeResult VisitNode(ClassDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var name = node.NameTok.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid class name", context));

            if (context.SymbolTable.Get(name) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is already defined", context));

            var classType = new ClassTypeValue(name, node.IsPublic, node.Fields, node.Methods)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                name,
                classType,
                isLet: true,
                declaredType: new TypeDescriptor(name),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            return res.Success(classType);
        }
    }
}
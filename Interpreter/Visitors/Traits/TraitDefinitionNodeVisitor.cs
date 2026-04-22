using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
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

            var traitValue = new TraitTypeValue(traitName, node.IsPublic, node.Methods, node.Fields)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                traitName,
                traitValue,
                isLet: true,
                declaredType: new TypeDescriptor(traitName),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            return res.Success(traitValue);
        }
    }
}
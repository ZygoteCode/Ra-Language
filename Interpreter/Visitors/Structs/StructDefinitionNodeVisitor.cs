using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Structs;
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

            var value = new StructTypeValue(name, node.IsPublic, node.Fields, node.Methods)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                name,
                value,
                isLet: true,
                declaredType: new TypeDescriptor(name),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            ValidateToStringMethod(node, context, ref res);
            if (res.ShouldReturn()) return res;

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
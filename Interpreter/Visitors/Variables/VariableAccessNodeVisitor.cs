using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class VariableAccessNodeVisitor : NodeVisitor<VariableAccessNode>
    {
        protected sealed override RuntimeResult VisitNode(VariableAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var name = node.VarNameTok.Value?.ToString();

            if (string.IsNullOrEmpty(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid variable name", context));

            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{name}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such symbol in scope",
                    help: $"declare '{name}' with 'var', 'let', 'const' or 'final' before using it, or check the spelling"));

            if (entry.IsMoved)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"value of '{name}' was already moved",
                    context,
                    code: DiagnosticCode.RuntimeMovedValue,
                    primaryLabel: "used here after move",
                    help: "non-copy 'let' bindings transfer ownership on use; rebind the value or take a copy"));

            var value = entry.Value;

            if (value.Type == RuntimeValueType.StructInstance ||
                value.Type == RuntimeValueType.ClassInstance ||
                value.Type == RuntimeValueType.Enum ||
                value.Type == RuntimeValueType.EnumType)
            {
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            var valueToReturn = entry.Value.IsCopy ? entry.Value.Copy() : entry.Value;
            return res.Success(valueToReturn.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
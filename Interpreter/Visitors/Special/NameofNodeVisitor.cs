using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class NameofNodeVisitor : NodeVisitor<NameofNode>
    {
        protected override RuntimeResult VisitNode(NameofNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value.ToString();
            var value = context.SymbolTable.Get(varName);

            if (value == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Variable {varName} not defined", context));

            return res.Success(new StringValue(varName).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}
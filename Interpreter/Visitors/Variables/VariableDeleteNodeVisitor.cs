using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class VariableDeleteNodeVisitor : NodeVisitor<VariableDeleteNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(VariableDeleteNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            foreach (Token token in node.Tokens)
            {
                string varName = token.Value.ToString();
                var value = context.SymbolTable.Get(varName);
                if (value == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' variable does not exist", context));
                context.SymbolTable.Remove(varName);
            }

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
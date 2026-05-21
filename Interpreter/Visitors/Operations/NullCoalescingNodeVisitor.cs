using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class NullCoalescingNodeVisitor : NodeVisitor<NullCoalescingNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(NullCoalescingNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.Operator.Type != TokenType.NULL_COALESCE)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Expected '??' operator", context));
            }

            var left = res.Register(await interpreter.Visit(node.Left, context));
            if (res.ShouldReturn()) return res;

            var right = res.Register(await interpreter.Visit(node.Right, context));
            if (res.ShouldReturn()) return res;

            if (left.Type == RuntimeValueType.Null)
                return res.Success(right.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

            return res.Success(left.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
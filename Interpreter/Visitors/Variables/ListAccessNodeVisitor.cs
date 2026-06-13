using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class ListAccessNodeVisitor : NodeVisitor<ListAccessNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ListAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            // Multi-parameter index `obj[a, b]` → op_index(a, b) method call.
            if (node.IsMulti)
            {
                var call = RaLanguage.Interpreter.Runtime.IndexDesugar.BuildGet(node.Target, node.Indices, node.PositionStart, node.PositionEnd);
                return await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(call, context, interpreter);
            }

            var target = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Target, context, interpreter));
            if (res.ShouldReturn()) return res;

            var index = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Index, context, interpreter));
            if (res.ShouldReturn()) return res;

            ValueResult result = target.ListAccess(index);
            if (result.Item2 != null) return res.Failure(result.Item2);
            return res.Success(result.Item1!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
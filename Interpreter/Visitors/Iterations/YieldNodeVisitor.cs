using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Iterations;

namespace RaLanguage.Interpreter.Visitors.Iterations
{
    public class YieldNodeVisitor : NodeVisitor<YieldNode>
    {
        protected sealed override RuntimeResult VisitNode(YieldNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var childRes = interpreter.Visit(node.Expression, context);
            var val = res.Register(childRes, propagateLoopControl: false);
            if (childRes.Error != null) return res.Failure(childRes.Error);
            if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);
            if (childRes.LoopShouldContinue) return res.SuccessContinue();
            if (childRes.LoopShouldBreak) return res.SuccessBreak();

            return res.SuccessYield(val ?? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
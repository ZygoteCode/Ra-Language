using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class TryNodeVisitor : NodeVisitor<TryNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(TryNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(TryNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var tryRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.TryBody, context, interpreter);

            if (tryRes.Error == null)
            {
                if (node.FinallyBody != null)
                {
                    var finallyRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.FinallyBody, context, interpreter);
                    if (finallyRes.Error != null) return res.Failure(finallyRes.Error);
                    if (finallyRes.FuncReturnValue != null) return res.SuccessReturn(finallyRes.FuncReturnValue);
                    if (finallyRes.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes.LoopShouldBreak) return res.SuccessBreak();
                }

                if (tryRes.FuncReturnValue != null) return res.SuccessReturn(tryRes.FuncReturnValue);
                if (tryRes.LoopShouldContinue) return res.SuccessContinue();
                if (tryRes.LoopShouldBreak) return res.SuccessBreak();

                return res.Success(tryRes.Value);
            }

            var originalError = tryRes.Error;

            if (node.CatchBody != null)
            {
                var catchCtx = context.Copy();
                string errMsg = originalError.ToString();
                var errVal = new StringValue(errMsg).SetContext(catchCtx).SetPos(node.PositionStart, node.PositionEnd);

                if (node.CatchVarTok != null)
                {
                    catchCtx.SymbolTable.Set(node.CatchVarTok.Value.ToString(), errVal);
                }

                var catchRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.CatchBody, catchCtx, interpreter);

                if (catchRes.Error != null)
                {
                    if (node.FinallyBody != null)
                    {
                        var finallyRes2 = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.FinallyBody, context, interpreter);
                        if (finallyRes2.Error != null) return res.Failure(finallyRes2.Error);
                        if (finallyRes2.FuncReturnValue != null) return res.SuccessReturn(finallyRes2.FuncReturnValue);
                        if (finallyRes2.LoopShouldContinue) return res.SuccessContinue();
                        if (finallyRes2.LoopShouldBreak) return res.SuccessBreak();
                    }
                    return res.Failure(catchRes.Error);
                }

                if (node.FinallyBody != null)
                {
                    var finallyRes3 = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.FinallyBody, context, interpreter);
                    if (finallyRes3.Error != null) return res.Failure(finallyRes3.Error);
                    if (finallyRes3.FuncReturnValue != null) return res.SuccessReturn(finallyRes3.FuncReturnValue);
                    if (finallyRes3.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes3.LoopShouldBreak) return res.SuccessBreak();
                }

                if (catchRes.FuncReturnValue != null) return res.SuccessReturn(catchRes.FuncReturnValue);
                if (catchRes.LoopShouldContinue) return res.SuccessContinue();
                if (catchRes.LoopShouldBreak) return res.SuccessBreak();

                return res.Success(catchRes.Value);
            }
            else
            {
                if (node.FinallyBody != null)
                {
                    var finallyRes4 = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.FinallyBody, context, interpreter);
                    if (finallyRes4.Error != null) return res.Failure(finallyRes4.Error);
                    if (finallyRes4.FuncReturnValue != null) return res.SuccessReturn(finallyRes4.FuncReturnValue);
                    if (finallyRes4.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes4.LoopShouldBreak) return res.SuccessBreak();
                }

                return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }
        }
    }
}
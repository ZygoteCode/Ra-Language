using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class TryNodeVisitor : NodeVisitor<TryNode>
    {
        protected sealed override RuntimeResult VisitNode(TryNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var tryRes = interpreter.Visit(node.TryBody, context);

            if (tryRes.Error == null)
            {
                if (node.FinallyBody != null)
                {
                    var finallyRes = interpreter.Visit(node.FinallyBody, context);
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

                var catchRes = interpreter.Visit(node.CatchBody, catchCtx);

                if (catchRes.Error != null)
                {
                    if (node.FinallyBody != null)
                    {
                        var finallyRes2 = interpreter.Visit(node.FinallyBody, context);
                        if (finallyRes2.Error != null) return res.Failure(finallyRes2.Error);
                        if (finallyRes2.FuncReturnValue != null) return res.SuccessReturn(finallyRes2.FuncReturnValue);
                        if (finallyRes2.LoopShouldContinue) return res.SuccessContinue();
                        if (finallyRes2.LoopShouldBreak) return res.SuccessBreak();
                    }
                    return res.Failure(catchRes.Error);
                }

                if (node.FinallyBody != null)
                {
                    var finallyRes3 = interpreter.Visit(node.FinallyBody, context);
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
                    var finallyRes4 = interpreter.Visit(node.FinallyBody, context);
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
using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Async;

namespace RaLanguage.Interpreter.Visitors.Async
{
    public class ForAwaitNodeVisitor : NodeVisitor<ForAwaitNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ForAwaitNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var src = await interpreter.Visit(node.StreamNode, context);
            if (src.Error != null) return res.Failure(src.Error);

            if (src.Value is not AsyncStreamValue streamValue)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'for await' requires a stream, got '{src.Value?.Type}'", context));
            }

            var stream = streamValue.Core;
            var token = context.AsyncCtx?.Token ?? System.Threading.CancellationToken.None;
            var elements = new System.Collections.Generic.List<RuntimeValue>();
            var varName = node.VarNameToken.Value?.ToString() ?? "_";

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    stream.Cancel();
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "for-await cancelled", context));
                }

                var (ok, value, closed, err) = stream.PullNext(token);
                if (err != null) return res.Failure(err);
                if (closed) break;
                if (!ok) break;

                context.SymbolTable.Set(varName, value ?? NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var bodyRes = await interpreter.Visit(node.BodyNode, context);
                if (bodyRes.Error != null)
                {
                    stream.Cancel();
                    return res.Failure(bodyRes.Error);
                }

                if (bodyRes.LoopShouldBreak)
                {
                    stream.Cancel();
                    break;
                }

                if (bodyRes.LoopShouldContinue) continue;

                if (bodyRes.FuncReturnValue != null)
                {
                    stream.Cancel();
                    return res.SuccessReturn(bodyRes.FuncReturnValue);
                }

                if (!node.ShouldReturnNull && bodyRes.Value != null)
                {
                    elements.Add(bodyRes.Value);
                }
            }

            if (node.ShouldReturnNull)
                return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

            return res.Success(new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

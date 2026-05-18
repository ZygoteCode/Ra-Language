using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Parser.Nodes.Async;

namespace RaLanguage.Interpreter.Visitors.Async
{
    public class AwaitNodeVisitor : NodeVisitor<AwaitNode>
    {
        protected sealed override RuntimeResult VisitNode(AwaitNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var inner = interpreter.Visit(node.Expression, context);
            if (inner.Error != null) return res.Failure(inner.Error);
            var value = inner.Value;
            if (value == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Cannot await null", context));

            if (value is TaskValue tv)
            {
                var core = tv.Core;
                if (!core.IsCompleted)
                {
                    var token = context.AsyncCtx?.Token ?? System.Threading.CancellationToken.None;
                    try
                    {
                        core.Wait(token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Await cancelled", context));
                    }
                }

                if (core.IsCancelled) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Awaited task '{core.DebugName}' was cancelled", context));
                if (core.IsFaulted && core.Error != null) return res.Failure(core.Error);

                var result = core.Result ?? new RaLanguage.Interpreter.Values.Primitives.NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                if (tv.ElementType != null && !tv.ElementType.IsTypeParameter
                    && result.Type != RaLanguage.Interpreter.Values.RuntimeValueType.Null
                    && !RaLanguage.Types.TypeSystem.IsAssignable(context, tv.ElementType, result))
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Awaited task produced a value of type '{result.Type}' but task<{tv.ElementType}> was expected", context));
                }
                return res.Success(result.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

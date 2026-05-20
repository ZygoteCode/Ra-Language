using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Async
{
    public class SpawnNodeVisitor : NodeVisitor<SpawnNode>
    {
        protected sealed override RuntimeResult VisitNode(SpawnNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.Expression is FunctionCallNode call)
            {
                var callee = interpreter.Visit(call.NodeToCall, context);
                if (callee.Error != null) return res.Failure(callee.Error);

                var calleeValue = callee.Value;
                if (calleeValue is not BaseFunctionValue fn)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "spawn requires a function call expression", context));
                }

                var positionalArgs = new System.Collections.Generic.List<RuntimeValue>();
                var namedArgs = new System.Collections.Generic.Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
                foreach (var argNode in call.ArgNodes)
                {
                    var argRes = interpreter.Visit(argNode.Expr, context);
                    if (argRes.Error != null) return res.Failure(argRes.Error);
                    if (argNode.NameTok != null)
                    {
                        namedArgs[argNode.NameTok.Value.Value?.ToString() ?? ""] = argRes.Value!;
                    }
                    else
                    {
                        positionalArgs.Add(argRes.Value!);
                    }
                }

                var parentAsync = context.AsyncCtx;
                // The previous implementation mutated fn.Context.AsyncCtx so that the
                // dispatch-time parent lookup picked up the spawn's async scope. That
                // mutation is racy when the same function is spawned from multiple
                // fibers concurrently (the Context object is shared across the COPY
                // of the FunctionValue produced by ExtractVariableValueByName). Use
                // a thread-local override instead — read by ExecuteAsyncDispatch.
                var task = AsyncScheduler.Schedule($"spawn:{fn.Name}", parentAsync, childAsyncCtx =>
                {
                    childAsyncCtx.InsideAsyncFunction = true;
                    var prior = RaLanguage.Interpreter.Runtime.Async.AsyncContextOverride.Push(childAsyncCtx);
                    try
                    {
                        var execRes = fn.ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
                        if (execRes.Error != null) return (null, execRes.Error);
                        var produced = execRes.FuncReturnValue ?? execRes.Value;
                        if (produced is TaskValue innerTask)
                        {
                            innerTask.Core.Wait(childAsyncCtx.Token);
                            if (innerTask.Core.IsCancelled) return (null, AsyncScheduler.MakeCancellationError(node.PositionStart, node.PositionEnd, context));
                            if (innerTask.Core.IsFaulted && innerTask.Core.Error != null) return (null, innerTask.Core.Error);
                            return (innerTask.Core.Result, null);
                        }
                        return (produced, null);
                    }
                    finally
                    {
                        RaLanguage.Interpreter.Runtime.Async.AsyncContextOverride.Pop(prior);
                    }
                });
                return res.Success(new TaskValue(task).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            var inner = interpreter.Visit(node.Expression, context);
            if (inner.Error != null) return res.Failure(inner.Error);
            if (inner.Value is TaskValue alreadyTask)
            {
                return res.Success(alreadyTask.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }
            return res.Success(new TaskValue(RaTaskCore.FromCompletedValue(inner.Value)).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

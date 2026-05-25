using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Async
{
    public class SpawnNodeVisitor : NodeVisitor<SpawnNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(SpawnNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(SpawnNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.Expression is FunctionCallNode call)
            {
                var callee = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(call.NodeToCall, context, interpreter);
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
                    var argRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(argNode.Expr, context, interpreter);
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

                // Cross-fiber borrow safety. The user's audit (6.3) called out
                // that a borrow handed into a spawned fiber can outlive the
                // source, and that two fibers sharing a `&mut x` would race
                // without the borrow checker noticing. We enforce a static
                // (well — dynamic at the spawn site, but pre-schedule) rule:
                //
                //   any BorrowValue argument or captured borrow must point at
                //   a value whose RuntimeValue.IsSync is true.
                //
                // IsSync defaults to IsCopy (so immutable scalars sail through)
                // and is explicitly true for thread-safe constructs (channel,
                // task, async stream, immutable string). Mutable containers,
                // class instances, struct instances default to non-Sync.
                var crossErr = CheckSpawnSafety(fn, positionalArgs, namedArgs, node, context);
                if (crossErr != null) return res.Failure(crossErr);

                var parentAsync = context.AsyncCtx;
                // The previous implementation mutated fn.Context.AsyncCtx so that the
                // dispatch-time parent lookup picked up the spawn's async scope. That
                // mutation is racy when the same function is spawned from multiple
                // fibers concurrently (the Context object is shared across the COPY
                // of the FunctionValue produced by ExtractVariableValueByName). Use
                // a thread-local override instead — read by ExecuteAsyncDispatch.
                var task = AsyncScheduler.Schedule($"spawn:{fn.Name}", parentAsync, async childAsyncCtx =>
                {
                    childAsyncCtx.InsideAsyncFunction = true;
                    var prior = RaLanguage.Interpreter.Runtime.Async.AsyncContextOverride.Push(childAsyncCtx);
                    try
                    {
                        var execRes = await fn.ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
                        if (execRes.Error != null) return new ValueResult(null, execRes.Error);
                        var produced = execRes.FuncReturnValue ?? execRes.Value;
                        if (produced is TaskValue innerTask)
                        {
                            innerTask.Core.Wait(childAsyncCtx.Token);
                            if (innerTask.Core.IsCancelled) return new ValueResult(null, AsyncScheduler.MakeCancellationError(node.PositionStart, node.PositionEnd, context));
                            if (innerTask.Core.IsFaulted && innerTask.Core.Error != null) return new ValueResult(null, innerTask.Core.Error);
                            return new ValueResult(innerTask.Core.Result, null);
                        }
                        return new ValueResult(produced, null);
                    }
                    finally
                    {
                        RaLanguage.Interpreter.Runtime.Async.AsyncContextOverride.Pop(prior);
                    }
                });
                return res.Success(new TaskValue(task).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            var inner = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Expression, context, interpreter);
            if (inner.Error != null) return res.Failure(inner.Error);
            if (inner.Value is TaskValue alreadyTask)
            {
                return res.Success(alreadyTask.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }
            return res.Success(new TaskValue(RaTaskCore.FromCompletedValue(inner.Value)).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private static Error? CheckSpawnSafety(BaseFunctionValue fn,
            System.Collections.Generic.List<RuntimeValue> positionalArgs,
            System.Collections.Generic.Dictionary<string, RuntimeValue> namedArgs,
            SpawnNode node,
            Context context)
        {
            // Check positional / named borrow args.
            for (int i = 0; i < positionalArgs.Count; i++)
            {
                var err = CheckSpawnedReference(positionalArgs[i], $"positional argument #{i + 1}", node, context);
                if (err != null) return err;
            }
            foreach (var kv in namedArgs)
            {
                var err = CheckSpawnedReference(kv.Value, $"named argument '{kv.Key}'", node, context);
                if (err != null) return err;
            }

            // Check captured borrows on the function itself.
            if (fn.CapturedValues != null)
            {
                foreach (var kv in fn.CapturedValues)
                {
                    var err = CheckSpawnedReference(kv.Value, $"captured '&{kv.Key}'", node, context);
                    if (err != null) return err;
                }
            }
            return null;
        }

        private static Error? CheckSpawnedReference(RuntimeValue value, string label, SpawnNode node, Context context)
        {
            if (value is BorrowValue bv)
            {
                // A borrow is safe to send only if the underlying value is
                // Sync. Reading `bv.SourceEntry.Value` is fine here (no
                // dispatch through the borrow itself), and we avoid the
                // "borrow was released" case since FreezeCaptures and the
                // caller chain keep it live for the lifetime of the closure.
                var source = bv.SourceEntry.Value;
                if (source != null && !source.IsSync)
                {
                    return new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot spawn: {label} is a borrow of '{bv.SourceName}' whose value type '{source.Type}' is not Sync",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "borrow crosses fiber boundary into non-Sync target",
                        help: "move ownership in (`move x`), wrap the value in a channel/task/async-stream, or use only Sync types (immutable scalars, channels, tasks) across fibers");
                }
            }

            // A function value handed across the fiber boundary brings its
            // explicit captures with it. Any '&x' capture pointing at a
            // non-Sync source is the same race risk as passing a direct
            // BorrowValue, just one indirection deeper. Walk the captures.
            if (value is BaseFunctionValue bfn && bfn.CapturedValues != null)
            {
                foreach (var kv in bfn.CapturedValues)
                {
                    var nestedLabel = $"{label} → captured '&{kv.Key}' of closure '{bfn.Name}'";
                    var nested = CheckSpawnedReference(kv.Value, nestedLabel, node, context);
                    if (nested != null) return nested;
                }
            }
            return null;
        }
    }
}

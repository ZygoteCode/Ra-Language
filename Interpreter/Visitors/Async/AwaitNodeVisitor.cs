using System.Threading.Tasks;
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
        protected sealed override async ValueTask<RuntimeResult> VisitNode(AwaitNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(AwaitNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var inner = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Expression, context, interpreter);
            if (inner.Error != null) return res.Failure(inner.Error);

            return await AwaitValueCore(inner.Value, context, node.PositionStart, node.PositionEnd);
        }

        // M85 — reusable core. Takes an already-evaluated value (the
        // expression that the `await` keyword fronts) and produces the
        // task's resolved result. Used by both the AST visitor path
        // (above) and the dedicated `Opcode.Await` dispatch added in
        // M85 — the dedicated opcode resolves the expression itself via
        // IR + the dispatch loop, then calls into this helper instead
        // of routing through NativeDefine + the visitor's full Apply.
        // Keeps the cancellation / type-check / non-TaskValue passthrough
        // semantics centralised in one place.
        public static async ValueTask<RuntimeResult> AwaitValueCore(
            RuntimeValue? value,
            Context context,
            Lexer.Position posStart,
            Lexer.Position posEnd)
        {
            var res = new RuntimeResult();
            if (value == null) return res.Failure(new RuntimeError(posStart, posEnd, "Cannot await null", context));

            if (value is TaskValue tv)
            {
                var core = tv.Core;
                if (!core.IsCompleted)
                {
                    var token = context.AsyncCtx?.Token ?? System.Threading.CancellationToken.None;
                    try
                    {
                        if (token.CanBeCanceled)
                        {
                            using var reg = token.Register(static state => ((RaTaskCore)state!).RequestCancel(), core);
                            await core.WaitAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            await core.WaitAsync().ConfigureAwait(false);
                        }
                    }
                    catch (System.OperationCanceledException)
                    {
                        return res.Failure(new RuntimeError(posStart, posEnd, "Await cancelled", context));
                    }
                }

                if (core.IsCancelled) return res.Failure(new RuntimeError(posStart, posEnd, $"Awaited task '{core.DebugName}' was cancelled", context));
                if (core.IsFaulted && core.Error != null) return res.Failure(core.Error);

                var result = core.Result ?? new RaLanguage.Interpreter.Values.Primitives.NullValue().SetContext(context).SetPos(posStart, posEnd);

                if (tv.ElementType != null && !tv.ElementType.IsTypeParameter
                    && result.Type != RaLanguage.Interpreter.Values.RuntimeValueType.Null
                    && !RaLanguage.Types.TypeSystem.IsAssignable(context, tv.ElementType, result))
                {
                    return res.Failure(new RuntimeError(posStart, posEnd, $"Awaited task produced a value of type '{result.Type}' but task<{tv.ElementType}> was expected", context));
                }
                return res.Success(result.SetContext(context).SetPos(posStart, posEnd));
            }

            return res.Success(value.SetContext(context).SetPos(posStart, posEnd));
        }
    }
}

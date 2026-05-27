using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // Free-function delegate combinators. All entry points accept any
    // BaseFunctionValue, so they work uniformly on user functions,
    // lambdas, bound method groups, built-ins, multicast, partial, and
    // composed values. No nominal type wrapping — what's in is what's
    // out (up to the operator semantics).
    internal static class DelegateBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("partial", Partial);
            BuiltInRegistry.Register("compose", Compose);
            BuiltInRegistry.Register("combine", Combine);
            BuiltInRegistry.Register("remove_handler", RemoveHandler);
            BuiltInRegistry.Register("invoke", Invoke);
            BuiltInRegistry.Register("handler_count", HandlerCount);
        }

        private static RuntimeResult Partial(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count < 1)
                return BuiltinUtils.Fail(ctx, p1, p2, "partial expects at least 1 argument (the target callable)");
            if (!(args[0] is BaseFunctionValue target))
                return BuiltinUtils.Fail(ctx, p1, p2, "partial: first argument must be a callable");

            var bound = new List<RuntimeValue>(args.Count - 1);
            for (int i = 1; i < args.Count; i++) bound.Add(args[i]);

            var pv = new PartialFunctionValue(target, bound)
                .SetContext(ctx)
                .SetPos(p1, p2);
            return BuiltinUtils.Ok(pv, ctx, p1, p2);
        }

        private static RuntimeResult Compose(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 2)
                return BuiltinUtils.Fail(ctx, p1, p2, "compose expects exactly 2 callables (inner, outer)");
            if (!(args[0] is BaseFunctionValue inner))
                return BuiltinUtils.Fail(ctx, p1, p2, "compose: first argument must be a callable");
            if (!(args[1] is BaseFunctionValue outer))
                return BuiltinUtils.Fail(ctx, p1, p2, "compose: second argument must be a callable");

            var cv = new ComposedFunctionValue(inner, outer)
                .SetContext(ctx)
                .SetPos(p1, p2);
            return BuiltinUtils.Ok(cv, ctx, p1, p2);
        }

        private static RuntimeResult Combine(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count == 0)
                return BuiltinUtils.Ok(NullValue.Null, ctx, p1, p2);
            BaseFunctionValue? accum = null;
            for (int i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (a == null || a.Type == RuntimeValueType.Null) continue;
                if (!(a is BaseFunctionValue bv))
                    return BuiltinUtils.Fail(ctx, p1, p2, "combine: every argument must be a callable or null");
                if (accum == null)
                {
                    accum = bv;
                    continue;
                }
                var (result, err) = accum.AddedTo(bv);
                if (err != null) return new RuntimeResult().Failure(err);
                accum = (BaseFunctionValue)result!;
            }
            if (accum == null)
                return BuiltinUtils.Ok(NullValue.Null, ctx, p1, p2);
            return BuiltinUtils.Ok(accum, ctx, p1, p2);
        }

        private static RuntimeResult RemoveHandler(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 2)
                return BuiltinUtils.Fail(ctx, p1, p2, "remove_handler expects (delegate, handler)");
            if (args[0] == null || args[0].Type == RuntimeValueType.Null)
                return BuiltinUtils.Ok(NullValue.Null, ctx, p1, p2);
            if (!(args[0] is BaseFunctionValue bv))
                return BuiltinUtils.Fail(ctx, p1, p2, "remove_handler: first argument must be a callable");
            if (!(args[1] is BaseFunctionValue tgt))
                return BuiltinUtils.Fail(ctx, p1, p2, "remove_handler: second argument must be a callable");
            var (result, err) = bv.SubbedBy(tgt);
            if (err != null) return new RuntimeResult().Failure(err);
            return BuiltinUtils.Ok(result!, ctx, p1, p2);
        }

        private static RuntimeResult Invoke(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            // Synchronous shim: `invoke(d, args_list)` calls d with the
            // unpacked args list. Useful for reflection-style code that
            // already has a list of args. Async dispatch goes through the
            // normal call site.
            if (args.Count < 1 || args.Count > 2)
                return BuiltinUtils.Fail(ctx, p1, p2, "invoke expects (callable[, args_list])");
            if (!(args[0] is BaseFunctionValue bv))
                return BuiltinUtils.Fail(ctx, p1, p2, "invoke: first argument must be a callable");
            var callArgs = new List<RuntimeValue>();
            if (args.Count == 2)
            {
                if (!(args[1] is ListValue lv))
                    return BuiltinUtils.Fail(ctx, p1, p2, "invoke: second argument must be a list of args");
                callArgs.AddRange(lv.Elements);
            }
            var inner = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(bv.Execute(callArgs));
            if (inner.Error != null) return new RuntimeResult().Failure(inner.Error);
            var produced = inner.Value ?? inner.FuncReturnValue ?? NullValue.Null;
            return BuiltinUtils.Ok(produced, ctx, p1, p2);
        }

        private static RuntimeResult HandlerCount(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 1)
                return BuiltinUtils.Fail(ctx, p1, p2, "handler_count expects 1 argument");
            if (args[0] == null || args[0].Type == RuntimeValueType.Null)
                return BuiltinUtils.Ok(new IntegerValue(0), ctx, p1, p2);
            if (args[0] is MulticastDelegateValue mc)
                return BuiltinUtils.Ok(new IntegerValue(mc.Handlers.Count), ctx, p1, p2);
            if (args[0] is BaseFunctionValue)
                return BuiltinUtils.Ok(new IntegerValue(1), ctx, p1, p2);
            return BuiltinUtils.Fail(ctx, p1, p2, "handler_count: argument must be a callable or null");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Runtime.Streams;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Streams;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // Async-stream operators. They wrap an existing AsyncStreamValue's
    // AsyncStreamCore by spawning a producer task that pulls upstream,
    // applies the transform, and emits downstream. Each operator therefore
    // schedules through the existing AsyncScheduler and inherits the
    // parent's cancellation scope.
    //
    // Surface mirrors StreamBuiltins (`map`, `filter`, `take`, `drop`,
    // `flat_map`, `for_each`, `collect`, `reduce`) — symmetry is the
    // payoff: a sync pipeline transposes to an async pipeline by renaming
    // the prefix.
    //
    // Bridges: to_async(sync_stream) spawns a producer task that drives
    // the sync stream; astream_to_list materialises an async stream
    // (drops back into sync land at the cost of buffering).
    public static class AsyncStreamBuiltins
    {
        public static readonly string[] Names =
        {
            "astream_map",
            "astream_filter",
            "astream_take",
            "astream_drop",
            "astream_flat_map",
            "astream_for_each",
            "astream_collect",
            "astream_reduce",
            "astream_to_list",
            "to_async",
        };

        public static bool IsAsyncStreamBuiltin(string name)
        {
            for (int i = 0; i < Names.Length; i++)
                if (Names[i] == name) return true;
            return false;
        }

        public static ValueTask<RuntimeResult> Execute(string name, List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            try
            {
                switch (name)
                {
                    case "astream_map":      return new ValueTask<RuntimeResult>(Spawn1to1(args, ctx, p1, p2, "astream_map", Op.Map));
                    case "astream_filter":   return new ValueTask<RuntimeResult>(Spawn1to1(args, ctx, p1, p2, "astream_filter", Op.Filter));
                    case "astream_take":     return new ValueTask<RuntimeResult>(SpawnTakeDrop(args, ctx, p1, p2, take: true));
                    case "astream_drop":     return new ValueTask<RuntimeResult>(SpawnTakeDrop(args, ctx, p1, p2, take: false));
                    case "astream_flat_map": return new ValueTask<RuntimeResult>(Spawn1to1(args, ctx, p1, p2, "astream_flat_map", Op.FlatMap));
                    case "astream_for_each": return AstreamForEach(args, ctx, p1, p2);
                    case "astream_collect":  return AstreamCollect(args, ctx, p1, p2);
                    case "astream_reduce":   return AstreamReduce(args, ctx, p1, p2);
                    case "astream_to_list":  return AstreamCollect(args, ctx, p1, p2);
                    case "to_async":         return new ValueTask<RuntimeResult>(ToAsync(args, ctx, p1, p2));
                }
            }
            catch (Exception ex)
            {
                return new ValueTask<RuntimeResult>(new RuntimeResult().Failure(new RuntimeError(p1, p2, $"{name}: {ex.GetType().Name}: {ex.Message}", ctx)));
            }
            return new ValueTask<RuntimeResult>(new RuntimeResult().Failure(new RuntimeError(p1, p2, $"Unknown async-stream builtin '{name}'", ctx)));
        }

        private enum Op { Map, Filter, FlatMap }

        private static RuntimeResult Fail(Context ctx, Position p1, Position p2, string msg)
            => new RuntimeResult().Failure(new RuntimeError(p1, p2, msg, ctx));

        private static RuntimeResult Ok(RuntimeValue v, Context ctx, Position p1, Position p2)
            => new RuntimeResult().Success(v.SetContext(ctx).SetPos(p1, p2));

        private static bool TryAsync(RuntimeValue v, out AsyncStreamValue s)
        {
            if (v is AsyncStreamValue av) { s = av; return true; }
            s = null!;
            return false;
        }

        private static bool TryFn(RuntimeValue v, out BaseFunctionValue f)
        {
            if (v is BaseFunctionValue bf) { f = bf; return true; }
            f = null!;
            return false;
        }

        private static bool TryLong(RuntimeValue v, out long l)
        {
            switch (v)
            {
                case IntegerValue iv: l = iv.Value; return true;
                case LongValue lv: l = lv.Value; return true;
                case NumberValue nv: l = (long)nv.Value; return true;
                default: l = 0; return false;
            }
        }

        // ----------------------------------------------------------------
        // Operator factory. Spawns a producer task that pulls upstream,
        // applies the transform, and emits downstream. The downstream
        // (output) stream gets a fresh AsyncStreamCore parented to the
        // caller's async context for proper cancellation propagation.
        // ----------------------------------------------------------------

        private static RuntimeResult Spawn1to1(List<RuntimeValue> args, Context ctx, Position p1, Position p2, string name, Op op)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, $"{name}(stream, fn) expects 2 arguments");
            if (!TryAsync(args[0], out var src)) return Fail(ctx, p1, p2, $"{name}: first argument must be an async stream");
            if (!TryFn(args[1], out var fn)) return Fail(ctx, p1, p2, $"{name}: second argument must be a function");

            var parentAsync = ctx?.AsyncCtx;
            var outCore = new AsyncStreamCore(8, parentAsync?.CancellationScope);
            var outValue = new AsyncStreamValue(outCore);

            var producer = AsyncScheduler.Schedule($"{name}", parentAsync, async childCtx =>
            {
                var srcCore = src.Core;
                try
                {
                    while (true)
                    {
                        if (childCtx.Token.IsCancellationRequested) break;
                        var (ok, v, closed, err) = srcCore.PullNext(childCtx.Token);
                        if (err != null) return new ValueResult(null, err);
                        if (closed || !ok) break;

                        switch (op)
                        {
                            case Op.Map:
                            {
                                var fr = await fn.Execute(new List<RuntimeValue> { v! });
                                if (fr.Error != null) return new ValueResult(null, fr.Error);
                                var mapped = fr.FuncReturnValue ?? fr.Value ?? NullValue.Null;
                                if (!outCore.Emit(mapped)) return new ValueResult(null, null);
                                break;
                            }
                            case Op.Filter:
                            {
                                var fr = await fn.Execute(new List<RuntimeValue> { v! });
                                if (fr.Error != null) return new ValueResult(null, fr.Error);
                                var pv = fr.FuncReturnValue ?? fr.Value;
                                if (pv != null && pv.IsTrue() && !outCore.Emit(v!)) return new ValueResult(null, null);
                                break;
                            }
                            case Op.FlatMap:
                            {
                                var fr = await fn.Execute(new List<RuntimeValue> { v! });
                                if (fr.Error != null) return new ValueResult(null, fr.Error);
                                var produced = fr.FuncReturnValue ?? fr.Value;
                                if (produced is AsyncStreamValue subStream)
                                {
                                    var subCore = subStream.Core;
                                    while (true)
                                    {
                                        if (childCtx.Token.IsCancellationRequested) break;
                                        var (sok, sv, sclosed, serr) = subCore.PullNext(childCtx.Token);
                                        if (serr != null) return new ValueResult(null, serr);
                                        if (sclosed || !sok) break;
                                        if (!outCore.Emit(sv!)) return new ValueResult(null, null);
                                    }
                                }
                                else if (produced is StreamValue syncStream)
                                {
                                    while (true)
                                    {
                                        if (childCtx.Token.IsCancellationRequested) break;
                                        var sr = await syncStream.PullNext(ctx);
                                        if (sr.Error != null) return new ValueResult(null, sr.Error);
                                        if (sr.Done) break;
                                        if (!outCore.Emit(sr.Value!)) return new ValueResult(null, null);
                                    }
                                }
                                else if (produced is ListValue lv)
                                {
                                    foreach (var el in lv.Elements)
                                    {
                                        if (childCtx.Token.IsCancellationRequested) break;
                                        if (!outCore.Emit(el)) return new ValueResult(null, null);
                                    }
                                }
                                else
                                {
                                    return new ValueResult(null, new RuntimeError(p1, p2,
                                        $"astream_flat_map function must return an async stream, sync stream, or list, got '{produced?.Type}'", ctx));
                                }
                                break;
                            }
                        }
                    }
                    return new ValueResult(NullValue.Null, null);
                }
                finally
                {
                    outCore.Close();
                }
            });
            outCore.AttachProducer(producer);
            return Ok(outValue, ctx, p1, p2);
        }

        private static RuntimeResult SpawnTakeDrop(List<RuntimeValue> args, Context ctx, Position p1, Position p2, bool take)
        {
            string name = take ? "astream_take" : "astream_drop";
            if (args.Count != 2) return Fail(ctx, p1, p2, $"{name}(stream, n) expects 2 arguments");
            if (!TryAsync(args[0], out var src)) return Fail(ctx, p1, p2, $"{name}: first argument must be an async stream");
            if (!TryLong(args[1], out var n)) return Fail(ctx, p1, p2, $"{name}: 'n' must be an integer");
            if (n < 0) n = 0;

            var parentAsync = ctx?.AsyncCtx;
            var outCore = new AsyncStreamCore(8, parentAsync?.CancellationScope);
            var outValue = new AsyncStreamValue(outCore);

            var producer = AsyncScheduler.Schedule($"{name}", parentAsync, async childCtx =>
            {
                var srcCore = src.Core;
                long remaining = n;
                try
                {
                    while (true)
                    {
                        if (childCtx.Token.IsCancellationRequested) break;
                        var (ok, v, closed, err) = srcCore.PullNext(childCtx.Token);
                        if (err != null) return new ValueResult(null, err);
                        if (closed || !ok) break;
                        if (take)
                        {
                            if (remaining <= 0) break;
                            if (!outCore.Emit(v!)) return new ValueResult(null, null);
                            remaining--;
                            if (remaining == 0) break;
                        }
                        else
                        {
                            if (remaining > 0) { remaining--; continue; }
                            if (!outCore.Emit(v!)) return new ValueResult(null, null);
                        }
                    }
                    return new ValueResult(NullValue.Null, null);
                }
                finally
                {
                    outCore.Close();
                    await Task.Yield();
                }
            });
            outCore.AttachProducer(producer);
            return Ok(outValue, ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Terminals on async streams. Run on the *caller's* fiber — they
        // are themselves `await`able from Ra code via plain await on the
        // returned task. Drain the channel until close.
        // ----------------------------------------------------------------

        private static async ValueTask<RuntimeResult> AstreamForEach(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "astream_for_each(stream, fn) expects 2 arguments");
            if (!TryAsync(args[0], out var src)) return Fail(ctx, p1, p2, "astream_for_each: first argument must be an async stream");
            if (!TryFn(args[1], out var fn)) return Fail(ctx, p1, p2, "astream_for_each: second argument must be a function");
            var token = ctx?.AsyncCtx?.Token ?? CancellationToken.None;
            while (true)
            {
                if (token.IsCancellationRequested) { src.Core.Cancel(); break; }
                var (ok, v, closed, err) = src.Core.PullNext(token);
                if (err != null) return new RuntimeResult().Failure(err);
                if (closed || !ok) break;
                var fr = await fn.Execute(new List<RuntimeValue> { v! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
            }
            return Ok(NullValue.Null, ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> AstreamCollect(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "astream_collect(stream) expects 1 argument");
            if (!TryAsync(args[0], out var src)) return Fail(ctx, p1, p2, "astream_collect: argument must be an async stream");
            var token = ctx?.AsyncCtx?.Token ?? CancellationToken.None;
            var buf = new List<RuntimeValue>();
            while (true)
            {
                if (token.IsCancellationRequested) { src.Core.Cancel(); break; }
                var (ok, v, closed, err) = src.Core.PullNext(token);
                if (err != null) return new RuntimeResult().Failure(err);
                if (closed || !ok) break;
                buf.Add(v!);
            }
            await Task.CompletedTask;
            return Ok(new ListValue(buf), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> AstreamReduce(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 3) return Fail(ctx, p1, p2, "astream_reduce(stream, init, fn) expects 3 arguments");
            if (!TryAsync(args[0], out var src)) return Fail(ctx, p1, p2, "astream_reduce: first argument must be an async stream");
            if (!TryFn(args[2], out var fn)) return Fail(ctx, p1, p2, "astream_reduce: third argument must be a function");
            var token = ctx?.AsyncCtx?.Token ?? CancellationToken.None;
            RuntimeValue acc = args[1];
            while (true)
            {
                if (token.IsCancellationRequested) { src.Core.Cancel(); break; }
                var (ok, v, closed, err) = src.Core.PullNext(token);
                if (err != null) return new RuntimeResult().Failure(err);
                if (closed || !ok) break;
                var fr = await fn.Execute(new List<RuntimeValue> { acc, v! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
                acc = fr.FuncReturnValue ?? fr.Value ?? NullValue.Null;
            }
            return Ok(acc, ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Bridge: sync StreamValue -> AsyncStreamValue. Spawns a producer
        // task that drains the sync stream and emits into a fresh async
        // stream parented to the caller's async context.
        // ----------------------------------------------------------------

        private static RuntimeResult ToAsync(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "to_async(stream) expects 1 argument");
            if (args[0] is not StreamValue syncSrc) return Fail(ctx, p1, p2, "to_async: argument must be a sync stream");

            var parentAsync = ctx?.AsyncCtx;
            var outCore = new AsyncStreamCore(8, parentAsync?.CancellationScope);
            var outValue = new AsyncStreamValue(outCore);

            var producer = AsyncScheduler.Schedule("to_async", parentAsync, async childCtx =>
            {
                try
                {
                    while (true)
                    {
                        if (childCtx.Token.IsCancellationRequested) break;
                        var r = await syncSrc.PullNext(ctx);
                        if (r.Error != null) return new ValueResult(null, r.Error);
                        if (r.Done) break;
                        if (!outCore.Emit(r.Value!)) return new ValueResult(null, null);
                    }
                    return new ValueResult(NullValue.Null, null);
                }
                finally
                {
                    outCore.Close();
                    await Task.Yield();
                }
            });
            outCore.AttachProducer(producer);
            return Ok(outValue, ctx, p1, p2);
        }
    }
}

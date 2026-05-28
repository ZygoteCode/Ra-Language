using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Streams;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Streams;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // User-facing surface for sync streams.
    //
    // Naming: every entry point is `stream_<verb>` for symmetry with the
    // existing `list_*`, `map_*`, `set_*` families. Pipelines compose
    // through the existing `|>` operator (no new sugar required).
    //
    // Dispatch: BuiltInFunctionValue routes calls here after first checking
    // AsyncBuiltins (so name collisions can't happen) and before the
    // general BuiltInRegistry — see the patched switch in
    // BuiltInFunctionValue.Execute.
    //
    // The whole module is async-by-default because most terminals must
    // await user-defined lambdas (which themselves may be async). Sources
    // and intermediate operators that don't invoke user code complete
    // synchronously via the ValueTask short-circuit and pay no allocation.
    public static class StreamBuiltins
    {
        public static readonly string[] Names =
        {
            // sources
            "stream_from",
            "stream_range",
            "stream_iterate",
            "stream_repeat",
            "stream_once",
            "stream_empty",
            "stream_generate",
            // intermediates
            "stream_map",
            "stream_filter",
            "stream_take",
            "stream_drop",
            "stream_take_while",
            "stream_drop_while",
            "stream_flat_map",
            "stream_chunk",
            "stream_window",
            "stream_distinct",
            "stream_scan",
            "stream_zip",
            "stream_enumerate",
            "stream_concat",
            "stream_peek",
            // terminals
            "stream_collect",
            "stream_for_each",
            "stream_reduce",
            "stream_fold",
            "stream_count",
            "stream_sum",
            "stream_min",
            "stream_max",
            "stream_first",
            "stream_last",
            "stream_any",
            "stream_all",
            "stream_find",
            // lifecycle
            "stream_cancel",
            "stream_is_done",
            "stream_close",
        };

        public static bool IsStreamBuiltin(string name)
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
                    // ---- sources -------------------------------------------
                    case "stream_from":     return new ValueTask<RuntimeResult>(StreamFrom(args, ctx, p1, p2));
                    case "stream_range":    return new ValueTask<RuntimeResult>(StreamRange(args, ctx, p1, p2));
                    case "stream_iterate":  return new ValueTask<RuntimeResult>(StreamIterate(args, ctx, p1, p2));
                    case "stream_repeat":   return new ValueTask<RuntimeResult>(StreamRepeat(args, ctx, p1, p2));
                    case "stream_once":     return new ValueTask<RuntimeResult>(StreamOnce(args, ctx, p1, p2));
                    case "stream_empty":    return new ValueTask<RuntimeResult>(StreamEmpty(ctx, p1, p2));
                    case "stream_generate": return new ValueTask<RuntimeResult>(StreamGenerate(args, ctx, p1, p2));

                    // ---- intermediates -------------------------------------
                    case "stream_map":         return new ValueTask<RuntimeResult>(WrapMapOrFilter(args, ctx, p1, p2, "stream_map", FusedOpKind.Map, (s, f) => new MapStreamSource(s, f, p1, p2)));
                    case "stream_filter":      return new ValueTask<RuntimeResult>(WrapMapOrFilter(args, ctx, p1, p2, "stream_filter", FusedOpKind.Filter, (s, f) => new FilterStreamSource(s, f, p1, p2)));
                    case "stream_take_while":  return new ValueTask<RuntimeResult>(WrapMapOrFilter(args, ctx, p1, p2, "stream_take_while", FusedOpKind.TakeWhile, (s, f) => new TakeWhileStreamSource(s, f)));
                    case "stream_drop_while":  return new ValueTask<RuntimeResult>(WrapMapOrFilter(args, ctx, p1, p2, "stream_drop_while", FusedOpKind.DropWhile, (s, f) => new DropWhileStreamSource(s, f)));
                    case "stream_flat_map":    return new ValueTask<RuntimeResult>(WrapOp1(args, ctx, p1, p2, "stream_flat_map", (s, f) => new FlatMapStreamSource(s, f, p1, p2)));
                    case "stream_peek":        return new ValueTask<RuntimeResult>(WrapMapOrFilter(args, ctx, p1, p2, "stream_peek", FusedOpKind.Peek, (s, f) => new PeekStreamSource(s, f)));
                    case "stream_take":        return new ValueTask<RuntimeResult>(StreamTake(args, ctx, p1, p2));
                    case "stream_drop":        return new ValueTask<RuntimeResult>(StreamDrop(args, ctx, p1, p2));
                    case "stream_chunk":       return new ValueTask<RuntimeResult>(StreamChunk(args, ctx, p1, p2));
                    case "stream_window":      return new ValueTask<RuntimeResult>(StreamWindow(args, ctx, p1, p2));
                    case "stream_distinct":    return new ValueTask<RuntimeResult>(StreamDistinct(args, ctx, p1, p2));
                    case "stream_scan":        return new ValueTask<RuntimeResult>(StreamScan(args, ctx, p1, p2));
                    case "stream_zip":         return new ValueTask<RuntimeResult>(StreamZip(args, ctx, p1, p2));
                    case "stream_enumerate":   return new ValueTask<RuntimeResult>(StreamEnumerate(args, ctx, p1, p2));
                    case "stream_concat":      return new ValueTask<RuntimeResult>(StreamConcat(args, ctx, p1, p2));

                    // ---- terminals -----------------------------------------
                    case "stream_collect":   return StreamCollect(args, ctx, p1, p2);
                    case "stream_for_each":  return StreamForEach(args, ctx, p1, p2);
                    case "stream_reduce":
                    case "stream_fold":      return StreamReduce(args, ctx, p1, p2);
                    case "stream_count":     return StreamCount(args, ctx, p1, p2);
                    case "stream_sum":       return StreamSum(args, ctx, p1, p2);
                    case "stream_min":       return StreamExtremum(args, ctx, p1, p2, false);
                    case "stream_max":       return StreamExtremum(args, ctx, p1, p2, true);
                    case "stream_first":     return StreamFirst(args, ctx, p1, p2);
                    case "stream_last":      return StreamLast(args, ctx, p1, p2);
                    case "stream_any":       return StreamAnyAll(args, ctx, p1, p2, anyMode: true);
                    case "stream_all":       return StreamAnyAll(args, ctx, p1, p2, anyMode: false);
                    case "stream_find":      return StreamFind(args, ctx, p1, p2);

                    // ---- lifecycle -----------------------------------------
                    case "stream_cancel":  return new ValueTask<RuntimeResult>(StreamCancel(args, ctx, p1, p2));
                    case "stream_is_done": return new ValueTask<RuntimeResult>(StreamIsDone(args, ctx, p1, p2));
                    case "stream_close":   return new ValueTask<RuntimeResult>(StreamClose(args, ctx, p1, p2));
                }
            }
            catch (Exception ex)
            {
                return new ValueTask<RuntimeResult>(new RuntimeResult().Failure(new RuntimeError(p1, p2, $"{name}: {ex.GetType().Name}: {ex.Message}", ctx)));
            }
            return new ValueTask<RuntimeResult>(new RuntimeResult().Failure(new RuntimeError(p1, p2, $"Unknown stream builtin '{name}'", ctx)));
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static RuntimeResult Fail(Context ctx, Position p1, Position p2, string msg)
            => new RuntimeResult().Failure(new RuntimeError(p1, p2, msg, ctx));

        private static RuntimeResult Ok(RuntimeValue v, Context ctx, Position p1, Position p2)
            => new RuntimeResult().Success(v.SetContext(ctx).SetPos(p1, p2));

        private static bool TryStream(RuntimeValue v, out StreamValue s)
        {
            if (v is StreamValue sv) { s = sv; return true; }
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

        private static RuntimeResult WrapOp1(List<RuntimeValue> args, Context ctx, Position p1, Position p2, string name, Func<StreamValue, BaseFunctionValue, IStreamSource> factory)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, $"{name}(stream, fn) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, $"{name}: first argument must be a stream");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, $"{name}: second argument must be a function");
            return Ok(new StreamValue(factory(s, f)), ctx, p1, p2);
        }

        // Fusion-aware wrapper for the Map / Filter / TakeWhile / DropWhile /
        // Peek operators. When the lambda is fusion-eligible (capture-free,
        // see StreamFusion.IsFusionEligible), the new op is spliced into an
        // existing FusedStreamSource — or one is created — so the chain
        // collapses to a single wrapper with one virtual dispatch per pull.
        // When the lambda has captures (so inlining wouldn't pay), we fall
        // back to the dedicated per-operator wrapper. Both paths return the
        // same observable behaviour.
        private static RuntimeResult WrapMapOrFilter(
            List<RuntimeValue> args, Context ctx, Position p1, Position p2,
            string name, FusedOpKind kind,
            Func<StreamValue, BaseFunctionValue, IStreamSource> fallback)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, $"{name}(stream, fn) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, $"{name}: first argument must be a stream");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, $"{name}: second argument must be a function");

            if (StreamFusion.IsFusionEligible(f))
            {
                var fused = StreamFusion.Append(s.Source, kind, f, 0);
                return Ok(new StreamValue(fused), ctx, p1, p2);
            }
            return Ok(new StreamValue(fallback(s, f)), ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Sources
        // ----------------------------------------------------------------

        private static RuntimeResult StreamFrom(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_from(collection) expects 1 argument");
            switch (args[0])
            {
                case ListValue lv:
                    return Ok(new StreamValue(new ListStreamSource(lv.Elements)), ctx, p1, p2);
                case SetValue sv:
                    return Ok(new StreamValue(new SetStreamSource(sv.Elements)), ctx, p1, p2);
                case MapValue mv:
                    return Ok(new StreamValue(new MapCollectionStreamSource(mv.Pairs)), ctx, p1, p2);
                case TupleValue tv:
                    return Ok(new StreamValue(new ListStreamSource(new List<RuntimeValue>(tv.Elements))), ctx, p1, p2);
                case StringValue strv:
                {
                    // Each character as a one-char StringValue, mirroring how
                    // existing string iteration works in CollectionBuiltins.
                    var chars = new List<RuntimeValue>(strv.Value.Length);
                    for (int i = 0; i < strv.Value.Length; i++)
                        chars.Add(new StringValue(strv.Value[i].ToString()));
                    return Ok(new StreamValue(new ListStreamSource(chars)), ctx, p1, p2);
                }
                case StreamValue passthrough:
                    return Ok(passthrough, ctx, p1, p2);
                default:
                    return Fail(ctx, p1, p2, $"stream_from: cannot iterate value of type '{args[0]?.Type}'");
            }
        }

        private static RuntimeResult StreamRange(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count < 1 || args.Count > 3) return Fail(ctx, p1, p2, "stream_range(end) | stream_range(start, end) | stream_range(start, end, step)");
            long start = 0, end = 0, step = 1;
            if (args.Count == 1)
            {
                if (!TryLong(args[0], out end)) return Fail(ctx, p1, p2, "stream_range: 'end' must be an integer");
            }
            else
            {
                if (!TryLong(args[0], out start)) return Fail(ctx, p1, p2, "stream_range: 'start' must be an integer");
                if (!TryLong(args[1], out end)) return Fail(ctx, p1, p2, "stream_range: 'end' must be an integer");
                if (args.Count == 3 && !TryLong(args[2], out step)) return Fail(ctx, p1, p2, "stream_range: 'step' must be an integer");
            }
            if (step == 0) return Fail(ctx, p1, p2, "stream_range: 'step' must not be zero");
            return Ok(new StreamValue(new RangeStreamSource(start, end, step)), ctx, p1, p2);
        }

        private static RuntimeResult StreamIterate(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_iterate(seed, fn) expects 2 arguments");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, "stream_iterate: second argument must be a function");
            return Ok(new StreamValue(new IterateStreamSource(args[0], f, p1, p2)), ctx, p1, p2);
        }

        private static RuntimeResult StreamRepeat(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count < 1 || args.Count > 2) return Fail(ctx, p1, p2, "stream_repeat(value) | stream_repeat(value, n)");
            long n = -1;
            if (args.Count == 2)
            {
                if (!TryLong(args[1], out n)) return Fail(ctx, p1, p2, "stream_repeat: 'n' must be an integer");
                if (n < 0) n = 0;
            }
            return Ok(new StreamValue(new RepeatStreamSource(args[0], n)), ctx, p1, p2);
        }

        private static RuntimeResult StreamOnce(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_once(value) expects 1 argument");
            return Ok(new StreamValue(new OnceStreamSource(args[0])), ctx, p1, p2);
        }

        private static RuntimeResult StreamEmpty(Context ctx, Position p1, Position p2)
            => Ok(new StreamValue(EmptyStreamSource.Instance), ctx, p1, p2);

        private static RuntimeResult StreamGenerate(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_generate(fn) expects 1 argument");
            if (!TryFn(args[0], out var f)) return Fail(ctx, p1, p2, "stream_generate: argument must be a function");
            return Ok(new StreamValue(new GenerateStreamSource(f, p1, p2)), ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Intermediates (the ones not covered by WrapOp1)
        // ----------------------------------------------------------------

        private static RuntimeResult StreamTake(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_take(stream, n) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_take: first argument must be a stream");
            if (!TryLong(args[1], out var n)) return Fail(ctx, p1, p2, "stream_take: 'n' must be an integer");
            var fused = StreamFusion.Append(s.Source, FusedOpKind.Take, null, n);
            return Ok(new StreamValue(fused), ctx, p1, p2);
        }

        private static RuntimeResult StreamDrop(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_drop(stream, n) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_drop: first argument must be a stream");
            if (!TryLong(args[1], out var n)) return Fail(ctx, p1, p2, "stream_drop: 'n' must be an integer");
            var fused = StreamFusion.Append(s.Source, FusedOpKind.Drop, null, n);
            return Ok(new StreamValue(fused), ctx, p1, p2);
        }

        private static RuntimeResult StreamChunk(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_chunk(stream, n) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_chunk: first argument must be a stream");
            if (!TryLong(args[1], out var n)) return Fail(ctx, p1, p2, "stream_chunk: 'n' must be an integer");
            if (n <= 0) return Fail(ctx, p1, p2, "stream_chunk: 'n' must be > 0");
            return Ok(new StreamValue(new ChunkStreamSource(s, (int)n)), ctx, p1, p2);
        }

        private static RuntimeResult StreamWindow(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_window(stream, n) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_window: first argument must be a stream");
            if (!TryLong(args[1], out var n)) return Fail(ctx, p1, p2, "stream_window: 'n' must be an integer");
            if (n <= 0) return Fail(ctx, p1, p2, "stream_window: 'n' must be > 0");
            return Ok(new StreamValue(new WindowStreamSource(s, (int)n)), ctx, p1, p2);
        }

        private static RuntimeResult StreamDistinct(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_distinct(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_distinct: argument must be a stream");
            return Ok(new StreamValue(new DistinctStreamSource(s)), ctx, p1, p2);
        }

        private static RuntimeResult StreamScan(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 3) return Fail(ctx, p1, p2, "stream_scan(stream, init, fn) expects 3 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_scan: first argument must be a stream");
            if (!TryFn(args[2], out var f)) return Fail(ctx, p1, p2, "stream_scan: third argument must be a function");
            return Ok(new StreamValue(new ScanStreamSource(s, args[1], f)), ctx, p1, p2);
        }

        private static RuntimeResult StreamZip(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_zip(stream_a, stream_b) expects 2 arguments");
            if (!TryStream(args[0], out var a)) return Fail(ctx, p1, p2, "stream_zip: first argument must be a stream");
            if (!TryStream(args[1], out var b)) return Fail(ctx, p1, p2, "stream_zip: second argument must be a stream");
            return Ok(new StreamValue(new ZipStreamSource(a, b)), ctx, p1, p2);
        }

        private static RuntimeResult StreamEnumerate(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_enumerate(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_enumerate: argument must be a stream");
            return Ok(new StreamValue(new EnumerateStreamSource(s)), ctx, p1, p2);
        }

        private static RuntimeResult StreamConcat(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_concat(stream_a, stream_b) expects 2 arguments");
            if (!TryStream(args[0], out var a)) return Fail(ctx, p1, p2, "stream_concat: first argument must be a stream");
            if (!TryStream(args[1], out var b)) return Fail(ctx, p1, p2, "stream_concat: second argument must be a stream");
            return Ok(new StreamValue(new ConcatStreamSource(a, b)), ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Terminals
        // ----------------------------------------------------------------

        private static async ValueTask<RuntimeResult> StreamCollect(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_collect(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_collect: argument must be a stream");
            var buf = new List<RuntimeValue>();
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                buf.Add(r.Value!);
            }
            return Ok(new ListValue(buf), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamForEach(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_for_each(stream, fn) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_for_each: first argument must be a stream");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, "stream_for_each: second argument must be a function");
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                var fr = await f.Execute(new List<RuntimeValue> { r.Value! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
            }
            return Ok(NullValue.Null, ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamReduce(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 3) return Fail(ctx, p1, p2, "stream_reduce(stream, init, fn) expects 3 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_reduce: first argument must be a stream");
            if (!TryFn(args[2], out var f)) return Fail(ctx, p1, p2, "stream_reduce: third argument must be a function");
            var acc = args[1];
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                var fr = await f.Execute(new List<RuntimeValue> { acc, r.Value! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
                acc = fr.FuncReturnValue ?? fr.Value ?? NullValue.Null;
            }
            return Ok(acc, ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamCount(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_count(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_count: argument must be a stream");
            long n = 0;
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                n++;
            }
            if (n <= int.MaxValue) return Ok(IntegerValue.Of((int)n), ctx, p1, p2);
            return Ok(new LongValue(n), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamSum(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_sum(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_sum: argument must be a stream");
            RuntimeValue? acc = null;
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                if (acc == null) acc = r.Value;
                else
                {
                    var (sum, err) = acc.AddedTo(r.Value!);
                    if (err != null) return new RuntimeResult().Failure(err);
                    acc = sum;
                }
            }
            return Ok(acc ?? IntegerValue.Of(0), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamExtremum(List<RuntimeValue> args, Context ctx, Position p1, Position p2, bool isMax)
        {
            string name = isMax ? "stream_max" : "stream_min";
            if (args.Count != 1) return Fail(ctx, p1, p2, $"{name}(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, $"{name}: argument must be a stream");
            RuntimeValue? best = null;
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                if (best == null) { best = r.Value; continue; }
                var (cmp, err) = isMax ? r.Value!.GetComparisonGt(best) : r.Value!.GetComparisonLt(best);
                if (err != null) return new RuntimeResult().Failure(err);
                if (cmp != null && cmp.IsTrue()) best = r.Value;
            }
            if (best == null) return Fail(ctx, p1, p2, $"{name}: empty stream has no extremum");
            return Ok(best, ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamFirst(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_first(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_first: argument must be a stream");
            var r = await s.PullNext(ctx);
            if (r.Error != null) return new RuntimeResult().Failure(r.Error);
            s.CloseSource();
            return Ok(OptionFor(r.Done ? null : r.Value), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamLast(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_last(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_last: argument must be a stream");
            RuntimeValue? last = null;
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                last = r.Value;
            }
            return Ok(OptionFor(last), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamAnyAll(List<RuntimeValue> args, Context ctx, Position p1, Position p2, bool anyMode)
        {
            string name = anyMode ? "stream_any" : "stream_all";
            if (args.Count != 2) return Fail(ctx, p1, p2, $"{name}(stream, pred) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, $"{name}: first argument must be a stream");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, $"{name}: second argument must be a function");
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                var fr = await f.Execute(new List<RuntimeValue> { r.Value! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
                var pv = fr.FuncReturnValue ?? fr.Value;
                bool match = pv != null && pv.IsTrue();
                if (anyMode && match) { s.CloseSource(); return Ok(BooleanValue.True, ctx, p1, p2); }
                if (!anyMode && !match) { s.CloseSource(); return Ok(BooleanValue.False, ctx, p1, p2); }
            }
            return Ok(BooleanValue.Of(!anyMode), ctx, p1, p2);
        }

        private static async ValueTask<RuntimeResult> StreamFind(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 2) return Fail(ctx, p1, p2, "stream_find(stream, pred) expects 2 arguments");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_find: first argument must be a stream");
            if (!TryFn(args[1], out var f)) return Fail(ctx, p1, p2, "stream_find: second argument must be a function");
            while (true)
            {
                var r = await s.PullNext(ctx);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                if (r.Done) break;
                var fr = await f.Execute(new List<RuntimeValue> { r.Value! });
                if (fr.Error != null) return new RuntimeResult().Failure(fr.Error);
                var pv = fr.FuncReturnValue ?? fr.Value;
                if (pv != null && pv.IsTrue()) { s.CloseSource(); return Ok(OptionFor(r.Value!), ctx, p1, p2); }
            }
            return Ok(OptionFor(null), ctx, p1, p2);
        }

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------

        private static RuntimeResult StreamCancel(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_cancel(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_cancel: argument must be a stream");
            s.Cancel();
            return Ok(BooleanValue.True, ctx, p1, p2);
        }

        private static RuntimeResult StreamIsDone(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_is_done(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_is_done: argument must be a stream");
            return Ok(BooleanValue.Of(s.IsDone), ctx, p1, p2);
        }

        private static RuntimeResult StreamClose(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            if (args.Count != 1) return Fail(ctx, p1, p2, "stream_close(stream) expects 1 argument");
            if (!TryStream(args[0], out var s)) return Fail(ctx, p1, p2, "stream_close: argument must be a stream");
            s.CloseSource();
            return Ok(BooleanValue.True, ctx, p1, p2);
        }

        // Option<T> builder reusing the builtin enum registered in Program.cs.
        // stream_first/last/find return Option<T>::Some(v) on a hit and
        // Option<T>::None on absence — same convention as the rest of the stdlib.
        private static RuntimeValue OptionFor(RuntimeValue? v)
        {
            if (v == null)
                return new EnumValue("Option", "None", 1, 1);
            return new EnumValue("Option", "Some", 0, 0, new List<RuntimeValue> { v });
        }
    }
}

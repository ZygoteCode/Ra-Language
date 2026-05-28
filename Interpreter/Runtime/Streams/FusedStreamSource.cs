using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Streams
{
    // ----------------------------------------------------------------------------
    // FusedStreamSource — collapses a chain of stateless / cheap-state stream
    // operators into a single IStreamSource. The win:
    //
    //   * one IStreamSource wrapper for the whole chain instead of N
    //     (Map+Filter+Take+Drop+TakeWhile+DropWhile+Peek);
    //   * one virtual `PullNext` dispatch into the fused source instead of
    //     N (each pull cascades through every operator inline);
    //   * a tight C# `for` loop over the op list with no per-stage
    //     `ValueTask` continuation allocation when user lambdas complete
    //     synchronously.
    //
    // Operators that are NOT fused (because their state is structurally
    // incompatible with the linear-op-list model): flat_map, chunk, window,
    // distinct, scan, zip, enumerate, concat. Those keep their dedicated
    // wrappers — fusion is opportunistic, not exhaustive.
    //
    // The eligibility predicate matches the brief: a lambda is "fusion-
    // eligible" when it is inlinable AND has no captures. Captureless
    // built-ins (BuiltInFunctionValue) are always eligible; user functions
    // are eligible when their explicit CaptureList is empty. When a lambda
    // fails the predicate we still wrap it normally — fusion is a perf
    // optimisation, never a semantic change.
    // ----------------------------------------------------------------------------

    public enum FusedOpKind : byte
    {
        Map,
        Filter,
        Take,
        Drop,
        TakeWhile,
        DropWhile,
        Peek,
    }

    public struct FusedOp
    {
        public FusedOpKind Kind;
        public BaseFunctionValue? Fn;   // Map/Filter/TakeWhile/DropWhile/Peek
        public long N;                  // Take/Drop limits
    }

    public sealed class FusedStreamSource : IStreamSource
    {
        public IStreamSource UnderlyingSrc { get; }
        public FusedOp[] Ops { get; }
        // Per-op mutable state. For Take: remaining; Drop: remaining;
        // DropWhile: 1 while still dropping, 0 after the first non-match.
        // TakeWhile / Map / Filter / Peek are stateless beyond _done and
        // store 0 in their slot.
        public long[] State { get; }

        // Single-shot guard. Once a downstream operator splices into this
        // source's underlying iterator (extending the op list), we mark
        // the previous fused-source as consumed so two parallel pullers
        // do not race on the shared upstream.
        public bool IsSpliced { get; private set; }

        private bool _done;

        public FusedStreamSource(IStreamSource underlying, FusedOp[] ops, long[] state)
        {
            UnderlyingSrc = underlying;
            Ops = ops;
            State = state;
        }

        public void MarkSpliced()
        {
            IsSpliced = true;
            _done = true;
        }

        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;

            // The outer `while` is what `Filter`/`Drop`/`DropWhile` use to
            // restart the pipeline with the next upstream element when the
            // current one is dropped. Take/TakeWhile / source-done both
            // terminate via `return DoneResult` and never re-enter.
            while (true)
            {
                var r = await UnderlyingSrc.PullNext(ctx);
                if (r.Done) { _done = true; return r; }
                if (r.Error != null) { _done = true; return r; }
                var v = r.Value!;

                bool dropped = false;
                for (int i = 0; i < Ops.Length; i++)
                {
                    var op = Ops[i];
                    switch (op.Kind)
                    {
                        case FusedOpKind.Map:
                        {
                            var fr = await op.Fn!.Execute(StreamFnArg.Of(v));
                            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                            v = fr.FuncReturnValue ?? fr.Value ?? NullValue.Null;
                            break;
                        }
                        case FusedOpKind.Filter:
                        {
                            var fr = await op.Fn!.Execute(StreamFnArg.Of(v));
                            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                            var pv = fr.FuncReturnValue ?? fr.Value;
                            if (pv == null || !pv.IsTrue()) { dropped = true; goto next_upstream; }
                            break;
                        }
                        case FusedOpKind.Take:
                        {
                            // State[i] = remaining quota. 0 means stop.
                            if (State[i] <= 0)
                            {
                                _done = true;
                                UnderlyingSrc.Close();
                                return StreamPullResult.DoneResult;
                            }
                            State[i]--;
                            break;
                        }
                        case FusedOpKind.Drop:
                        {
                            // State[i] = remaining drops. Once 0 the op
                            // becomes a pass-through.
                            if (State[i] > 0)
                            {
                                State[i]--;
                                dropped = true;
                                goto next_upstream;
                            }
                            break;
                        }
                        case FusedOpKind.TakeWhile:
                        {
                            var fr = await op.Fn!.Execute(StreamFnArg.Of(v));
                            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                            var pv = fr.FuncReturnValue ?? fr.Value;
                            if (pv == null || !pv.IsTrue())
                            {
                                _done = true;
                                UnderlyingSrc.Close();
                                return StreamPullResult.DoneResult;
                            }
                            break;
                        }
                        case FusedOpKind.DropWhile:
                        {
                            // State[i] = 1 while still dropping.
                            if (State[i] != 0)
                            {
                                var fr = await op.Fn!.Execute(StreamFnArg.Of(v));
                                if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                                var pv = fr.FuncReturnValue ?? fr.Value;
                                if (pv != null && pv.IsTrue())
                                {
                                    dropped = true;
                                    goto next_upstream;
                                }
                                State[i] = 0; // first non-match — open the gate
                            }
                            break;
                        }
                        case FusedOpKind.Peek:
                        {
                            var fr = await op.Fn!.Execute(StreamFnArg.Of(v));
                            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                            break;
                        }
                    }
                }

                if (!dropped) return StreamPullResult.OfValue(v);

                next_upstream:;
            }
        }

        public void Close()
        {
            _done = true;
            UnderlyingSrc.Close();
        }
    }

    // Pooled single-element argument list. The fused inner loop calls user
    // lambdas at peak rate; allocating a fresh List<RuntimeValue>(1) per
    // call is the dominant per-element overhead. Reusing a thread-local
    // single-slot list cuts allocs ~by the number of fused ops × elements
    // when the lambdas are sync (the steady-state path).
    //
    // Single-element only; multi-arg ops (scan, reduce) build their own
    // lists since the fused source does not include them.
    internal static class StreamFnArg
    {
        [ThreadStatic] private static List<RuntimeValue>? t_buf;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<RuntimeValue> Of(RuntimeValue v)
        {
            var b = t_buf;
            if (b == null) { b = new List<RuntimeValue>(1); t_buf = b; }
            if (b.Count == 0) b.Add(v); else b[0] = v;
            return b;
        }
    }

    public static class StreamFusion
    {
        // Lambda eligibility for fusion. The brief calls out "inlinable +
        // no captures"; we lift "no captures" to mean either:
        //   * a BuiltInFunctionValue (captures don't exist for builtins), OR
        //   * a user fn whose explicit CaptureList is null/empty AND whose
        //     materialised CapturedValues map is null/empty.
        // The implicit lexical closure case (CaptureList null, BindingContext
        // set, capture set unknown) is conservatively treated as ineligible
        // — falling back to the non-fused per-op wrapper still produces
        // correct results.
        public static bool IsFusionEligible(BaseFunctionValue fn)
        {
            if (fn is BuiltInFunctionValue) return true;
            bool noExplicit = fn.CaptureList == null || fn.CaptureList.Count == 0;
            bool noFrozen = fn.CapturedValues == null || fn.CapturedValues.Count == 0;
            return noExplicit && noFrozen;
        }

        // Take/Drop carry an integer literal; no lambda eligibility check
        // applies. Always fusible — collapsing them into the fused source
        // saves the dedicated TakeStreamSource / DropStreamSource wrapper.
        public static bool IsCounterFusionEligible() => true;

        private static long InitialState(FusedOpKind kind, long n)
        {
            switch (kind)
            {
                case FusedOpKind.Take:      return n < 0 ? 0 : n;
                case FusedOpKind.Drop:      return n < 0 ? 0 : n;
                case FusedOpKind.DropWhile: return 1;
                default:                    return 0;
            }
        }

        // Splice a new op onto an upstream stream. When the upstream is a
        // FusedStreamSource that hasn't been spliced yet, extend its op list.
        // Otherwise, allocate a fresh single-op fused source wrapping it.
        public static IStreamSource Append(IStreamSource upstream, FusedOpKind kind, BaseFunctionValue? fn, long n)
        {
            if (upstream is FusedStreamSource fused && !fused.IsSpliced)
            {
                int len = fused.Ops.Length;
                var newOps = new FusedOp[len + 1];
                Array.Copy(fused.Ops, newOps, len);
                newOps[len] = new FusedOp { Kind = kind, Fn = fn, N = n };
                var newState = new long[len + 1];
                Array.Copy(fused.State, newState, len);
                newState[len] = InitialState(kind, n);
                fused.MarkSpliced();
                return new FusedStreamSource(fused.UnderlyingSrc, newOps, newState);
            }
            return new FusedStreamSource(upstream,
                new[] { new FusedOp { Kind = kind, Fn = fn, N = n } },
                new[] { InitialState(kind, n) });
        }
    }
}

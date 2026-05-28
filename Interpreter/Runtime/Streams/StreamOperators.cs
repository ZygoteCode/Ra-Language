using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Streams;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime.Streams
{
    // ---------------------------------------------------------------------------
    // Operator wrappers. Each is a concrete IStreamSource that owns an
    // upstream StreamValue and forwards PullNext through its own transform.
    // No buffering, no per-element allocation beyond the transformed value.
    //
    // Operators check their own state first (so a `take(10)` over an infinite
    // stream stops asking the source the moment it has produced 10) and call
    // upstream.PullNext exactly when needed.
    //
    // Error propagation: an error from the upstream or from a user lambda is
    // stored as the result of the next pull; subsequent pulls are Done.
    // ---------------------------------------------------------------------------

    internal static class StreamOpHelpers
    {
        // Reusable single-element arg list — the operator hot path calls user
        // lambdas of arity 1 (map/filter/take_while/drop_while/peek/iterate/
        // flat_map). Allocating a fresh List per pull doubles allocations on
        // the chain. Per-instance one-slot list is reused.
        // Note: callers must not store the list past the await; we always
        // build & immediately await.
        public static List<RuntimeValue> One(RuntimeValue v)
            => new List<RuntimeValue>(1) { v };

        public static List<RuntimeValue> Two(RuntimeValue a, RuntimeValue b)
            => new List<RuntimeValue>(2) { a, b };
    }

    public sealed class MapStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _fn;
        private bool _done;
        private readonly Position _pStart;
        private readonly Position _pEnd;
        public MapStreamSource(StreamValue src, BaseFunctionValue fn, Position pStart, Position pEnd)
        { _src = src; _fn = fn; _pStart = pStart; _pEnd = pEnd; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            var fr = await _fn.Execute(StreamOpHelpers.One(r.Value!));
            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
            var v = fr.FuncReturnValue ?? fr.Value;
            return StreamPullResult.OfValue(v ?? NullValue.Null);
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class FilterStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _pred;
        private bool _done;
        private readonly Position _pStart;
        private readonly Position _pEnd;
        public FilterStreamSource(StreamValue src, BaseFunctionValue pred, Position pStart, Position pEnd)
        { _src = src; _pred = pred; _pStart = pStart; _pEnd = pEnd; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            while (!_done)
            {
                var r = await _src.PullNext(ctx);
                if (r.Done) { _done = true; return r; }
                var fr = await _pred.Execute(StreamOpHelpers.One(r.Value!));
                if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                var v = fr.FuncReturnValue ?? fr.Value;
                if (v != null && v.IsTrue()) return StreamPullResult.OfValue(r.Value!);
            }
            return StreamPullResult.DoneResult;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class TakeStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private long _remaining;
        private bool _done;
        public TakeStreamSource(StreamValue src, long n) { _src = src; _remaining = n < 0 ? 0 : n; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done || _remaining <= 0) { _done = true; _src.CloseSource(); return StreamPullResult.DoneResult; }
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            _remaining--;
            if (_remaining == 0) { _done = true; _src.CloseSource(); }
            return r;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class DropStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private long _remaining;
        private bool _done;
        public DropStreamSource(StreamValue src, long n) { _src = src; _remaining = n < 0 ? 0 : n; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            while (_remaining > 0)
            {
                var skip = await _src.PullNext(ctx);
                if (skip.Done) { _done = true; return skip; }
                _remaining--;
            }
            var r = await _src.PullNext(ctx);
            if (r.Done) _done = true;
            return r;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class TakeWhileStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _pred;
        private bool _done;
        public TakeWhileStreamSource(StreamValue src, BaseFunctionValue pred) { _src = src; _pred = pred; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            var fr = await _pred.Execute(StreamOpHelpers.One(r.Value!));
            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
            var pv = fr.FuncReturnValue ?? fr.Value;
            if (pv == null || !pv.IsTrue()) { _done = true; _src.CloseSource(); return StreamPullResult.DoneResult; }
            return r;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class DropWhileStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _pred;
        private bool _done;
        private bool _dropping = true;
        public DropWhileStreamSource(StreamValue src, BaseFunctionValue pred) { _src = src; _pred = pred; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            while (_dropping)
            {
                var r = await _src.PullNext(ctx);
                if (r.Done) { _done = true; return r; }
                var fr = await _pred.Execute(StreamOpHelpers.One(r.Value!));
                if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                var pv = fr.FuncReturnValue ?? fr.Value;
                if (pv == null || !pv.IsTrue()) { _dropping = false; return StreamPullResult.OfValue(r.Value!); }
            }
            var next = await _src.PullNext(ctx);
            if (next.Done) _done = true;
            return next;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class FlatMapStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _fn;
        private StreamValue? _inner;
        private bool _done;
        private readonly Position _pStart;
        private readonly Position _pEnd;
        public FlatMapStreamSource(StreamValue src, BaseFunctionValue fn, Position pStart, Position pEnd)
        { _src = src; _fn = fn; _pStart = pStart; _pEnd = pEnd; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            while (!_done)
            {
                if (_inner != null)
                {
                    var ir = await _inner.PullNext(ctx);
                    if (!ir.Done) return ir;
                    _inner = null;
                }
                var r = await _src.PullNext(ctx);
                if (r.Done) { _done = true; return r; }
                var fr = await _fn.Execute(StreamOpHelpers.One(r.Value!));
                if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
                var v = fr.FuncReturnValue ?? fr.Value;
                if (v is StreamValue sv) { _inner = sv; continue; }
                if (v is ListValue lv) { _inner = new StreamValue(new ListStreamSource(lv.Elements)); continue; }
                _done = true;
                return StreamPullResult.OfError(new RuntimeError(_pStart, _pEnd,
                    $"stream_flat_map function must return a Stream or List, got '{v?.Type}'", ctx));
            }
            return StreamPullResult.DoneResult;
        }
        public void Close()
        {
            _done = true;
            _inner?.CloseSource();
            _src.CloseSource();
        }
    }

    public sealed class ChunkStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly int _size;
        private bool _done;
        public ChunkStreamSource(StreamValue src, int size) { _src = src; _size = size <= 0 ? 1 : size; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var buf = new List<RuntimeValue>(_size);
            for (int i = 0; i < _size; i++)
            {
                var r = await _src.PullNext(ctx);
                if (r.Done)
                {
                    _done = true;
                    if (buf.Count == 0) return StreamPullResult.DoneResult;
                    return StreamPullResult.OfValue(new ListValue(buf));
                }
                buf.Add(r.Value!);
            }
            return StreamPullResult.OfValue(new ListValue(buf));
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class WindowStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly int _size;
        private readonly Queue<RuntimeValue> _buf;
        private bool _done;
        private bool _primed;
        public WindowStreamSource(StreamValue src, int size)
        {
            _src = src;
            _size = size <= 0 ? 1 : size;
            _buf = new Queue<RuntimeValue>(_size);
        }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            if (!_primed)
            {
                while (_buf.Count < _size)
                {
                    var r = await _src.PullNext(ctx);
                    if (r.Done) { _done = true; return StreamPullResult.DoneResult; }
                    _buf.Enqueue(r.Value!);
                }
                _primed = true;
                return StreamPullResult.OfValue(new ListValue(new List<RuntimeValue>(_buf)));
            }
            var next = await _src.PullNext(ctx);
            if (next.Done) { _done = true; return next; }
            _buf.Dequeue();
            _buf.Enqueue(next.Value!);
            return StreamPullResult.OfValue(new ListValue(new List<RuntimeValue>(_buf)));
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class DistinctStreamSource : IStreamSource
    {
        // Matches list_unique semantics: linear-scan equality via
        // GetComparisonEq so it composes with whatever the operand types
        // define as equal. The List backing is O(n) per check; users with
        // large unique workloads should pre-bucket through a Set or a Map.
        private readonly StreamValue _src;
        private readonly List<RuntimeValue> _seen = new();
        private bool _done;
        public DistinctStreamSource(StreamValue src) { _src = src; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            while (!_done)
            {
                var r = await _src.PullNext(ctx);
                if (r.Done) { _done = true; return r; }
                bool dup = false;
                for (int i = 0; i < _seen.Count; i++)
                {
                    var (eq, _) = _seen[i].GetComparisonEq(r.Value!);
                    if (eq != null && eq.IsTrue()) { dup = true; break; }
                }
                if (!dup) { _seen.Add(r.Value!); return r; }
            }
            return StreamPullResult.DoneResult;
        }
        public void Close() { _done = true; _src.CloseSource(); _seen.Clear(); }
    }

    public sealed class ScanStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private RuntimeValue _state;
        private readonly BaseFunctionValue _fn;
        private bool _done;
        private bool _firstEmitted;
        public ScanStreamSource(StreamValue src, RuntimeValue seed, BaseFunctionValue fn)
        { _src = src; _state = seed; _fn = fn; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            if (!_firstEmitted) { _firstEmitted = true; return StreamPullResult.OfValue(_state); }
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            var fr = await _fn.Execute(StreamOpHelpers.Two(_state, r.Value!));
            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
            var v = fr.FuncReturnValue ?? fr.Value;
            if (v == null) { _done = true; return StreamPullResult.DoneResult; }
            _state = v;
            return StreamPullResult.OfValue(_state);
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class ZipStreamSource : IStreamSource
    {
        private readonly StreamValue _a;
        private readonly StreamValue _b;
        private bool _done;
        public ZipStreamSource(StreamValue a, StreamValue b) { _a = a; _b = b; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var ra = await _a.PullNext(ctx);
            if (ra.Done) { _done = true; _b.CloseSource(); return ra; }
            var rb = await _b.PullNext(ctx);
            if (rb.Done) { _done = true; _a.CloseSource(); return rb; }
            return StreamPullResult.OfValue(new TupleValue(new List<RuntimeValue> { ra.Value!, rb.Value! }));
        }
        public void Close() { _done = true; _a.CloseSource(); _b.CloseSource(); }
    }

    public sealed class EnumerateStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private int _idx;
        private bool _done;
        public EnumerateStreamSource(StreamValue src) { _src = src; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            return StreamPullResult.OfValue(new TupleValue(new List<RuntimeValue> { IntegerValue.Of(_idx++), r.Value! }));
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }

    public sealed class ConcatStreamSource : IStreamSource
    {
        private readonly StreamValue _a;
        private readonly StreamValue _b;
        private bool _onA = true;
        private bool _done;
        public ConcatStreamSource(StreamValue a, StreamValue b) { _a = a; _b = b; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            if (_onA)
            {
                var r = await _a.PullNext(ctx);
                if (!r.Done) return r;
                _onA = false;
            }
            var rb = await _b.PullNext(ctx);
            if (rb.Done) _done = true;
            return rb;
        }
        public void Close() { _done = true; _a.CloseSource(); _b.CloseSource(); }
    }

    public sealed class PeekStreamSource : IStreamSource
    {
        private readonly StreamValue _src;
        private readonly BaseFunctionValue _fn;
        private bool _done;
        public PeekStreamSource(StreamValue src, BaseFunctionValue fn) { _src = src; _fn = fn; }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var r = await _src.PullNext(ctx);
            if (r.Done) { _done = true; return r; }
            var fr = await _fn.Execute(StreamOpHelpers.One(r.Value!));
            if (fr.Error != null) { _done = true; return StreamPullResult.OfError(fr.Error); }
            return r;
        }
        public void Close() { _done = true; _src.CloseSource(); }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime.Streams
{
    // ---------------------------------------------------------------------------
    // Source iterators. Each is a concrete IStreamSource implementation so the
    // JIT can devirtualise the hot PullNext path and the AOT compiler can keep
    // every implementation reachable without reflection.
    //
    // Sources are *pure*: they produce values from local state (index, seed,
    // counter) without driving any user lambda. Operators wrap sources to add
    // per-element callbacks.
    //
    // The CollectionStreamSource is the bridge from materialised collections
    // (list, set, map, tuple) into the stream world. The other sources are
    // synthetic.
    // ---------------------------------------------------------------------------

    public sealed class EmptyStreamSource : IStreamSource
    {
        public static readonly EmptyStreamSource Instance = new EmptyStreamSource();
        public ValueTask<StreamPullResult> PullNext(Context ctx) => new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
        public void Close() { }
    }

    public sealed class OnceStreamSource : IStreamSource
    {
        private RuntimeValue? _value;
        public OnceStreamSource(RuntimeValue value) { _value = value; }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_value == null) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            var v = _value;
            _value = null;
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(v));
        }
        public void Close() { _value = null; }
    }

    public sealed class RangeStreamSource : IStreamSource
    {
        private long _current;
        private readonly long _end;
        private readonly long _step;
        private bool _done;
        public RangeStreamSource(long start, long end, long step)
        {
            _current = start;
            _end = end;
            _step = step == 0 ? 1 : step;
        }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            bool forward = _step > 0;
            bool exhausted = forward ? _current >= _end : _current <= _end;
            if (exhausted) { _done = true; return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult); }
            RuntimeValue v;
            if (_current >= int.MinValue && _current <= int.MaxValue)
                v = IntegerValue.Of((int)_current);
            else
                v = new LongValue(_current);
            _current += _step;
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(v));
        }
        public void Close() { _done = true; }
    }

    public sealed class RepeatStreamSource : IStreamSource
    {
        private readonly RuntimeValue _value;
        private long _remaining;       // -1 means infinite
        private bool _done;
        public RepeatStreamSource(RuntimeValue value, long n)
        {
            _value = value;
            _remaining = n;
        }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            if (_remaining == 0) { _done = true; return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult); }
            if (_remaining > 0) _remaining--;
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(_value));
        }
        public void Close() { _done = true; }
    }

    public sealed class IterateStreamSource : IStreamSource
    {
        private RuntimeValue _current;
        private readonly BaseFunctionValue _fn;
        private bool _first = true;
        private bool _done;
        private readonly Position _pStart;
        private readonly Position _pEnd;
        public IterateStreamSource(RuntimeValue seed, BaseFunctionValue fn, Position pStart, Position pEnd)
        {
            _current = seed;
            _fn = fn;
            _pStart = pStart;
            _pEnd = pEnd;
        }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            if (_first) { _first = false; return StreamPullResult.OfValue(_current); }
            var r = await _fn.Execute(new List<RuntimeValue> { _current });
            if (r.Error != null) { _done = true; return StreamPullResult.OfError(r.Error); }
            var v = r.FuncReturnValue ?? r.Value;
            if (v == null) { _done = true; return StreamPullResult.OfError(new RuntimeError(_pStart, _pEnd, "stream_iterate function returned null", ctx)); }
            _current = v;
            return StreamPullResult.OfValue(v);
        }
        public void Close() { _done = true; }
    }

    public sealed class GenerateStreamSource : IStreamSource
    {
        private readonly BaseFunctionValue _fn;
        private bool _done;
        private readonly Position _pStart;
        private readonly Position _pEnd;
        public GenerateStreamSource(BaseFunctionValue fn, Position pStart, Position pEnd)
        {
            _fn = fn;
            _pStart = pStart;
            _pEnd = pEnd;
        }
        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return StreamPullResult.DoneResult;
            var r = await _fn.Execute(new List<RuntimeValue>());
            if (r.Error != null) { _done = true; return StreamPullResult.OfError(r.Error); }
            var v = r.FuncReturnValue ?? r.Value;
            if (v == null || v.Type == RuntimeValueType.Null)
            {
                _done = true;
                return StreamPullResult.DoneResult;
            }
            // stream_generate convention: an Option<T>::None terminates, Some(x)
            // yields x; any other value yields directly (so user callbacks that
            // return raw values still work).
            if (v is EnumValue ev)
            {
                if (ev.MemberName == "None") { _done = true; return StreamPullResult.DoneResult; }
                if (ev.MemberName == "Some" && ev.Payload != null && ev.Payload.Count == 1)
                    return StreamPullResult.OfValue(ev.Payload[0]);
            }
            return StreamPullResult.OfValue(v);
        }
        public void Close() { _done = true; }
    }

    public sealed class ListStreamSource : IStreamSource
    {
        private readonly List<RuntimeValue> _items;
        private int _idx;
        public ListStreamSource(List<RuntimeValue> items) { _items = items; }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_idx >= _items.Count) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(_items[_idx++]));
        }
        public void Close() { _idx = _items.Count; }
    }

    public sealed class SetStreamSource : IStreamSource
    {
        private readonly IEnumerator<RuntimeValue> _it;
        private bool _done;
        public SetStreamSource(IEnumerable<RuntimeValue> items) { _it = items.GetEnumerator(); }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_done) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            if (!_it.MoveNext()) { _done = true; return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult); }
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(_it.Current));
        }
        public void Close() { _done = true; _it.Dispose(); }
    }

    // Source over the entries of a Map<K, V>. Distinct from the *operator*
    // MapStreamSource (which threads a fn(T) -> U over upstream values) —
    // they share a verb but different roles. The collection variant emits
    // (key, value) tuples; the operator transforms an existing stream.
    public sealed class MapCollectionStreamSource : IStreamSource
    {
        private readonly List<(RuntimeValue Key, RuntimeValue Value)> _pairs;
        private int _idx;
        public MapCollectionStreamSource(List<(RuntimeValue Key, RuntimeValue Value)> pairs) { _pairs = pairs; }
        public ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (_idx >= _pairs.Count) return new ValueTask<StreamPullResult>(StreamPullResult.DoneResult);
            var kv = _pairs[_idx++];
            var t = new TupleValue(new List<RuntimeValue> { kv.Key, kv.Value });
            return new ValueTask<StreamPullResult>(StreamPullResult.OfValue(t));
        }
        public void Close() { _idx = _pairs.Count; }
    }
}

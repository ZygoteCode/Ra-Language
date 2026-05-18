using System;
using System.Collections.Generic;
using System.Threading;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime.Async
{
    public sealed class AsyncChannel
    {
        private readonly Queue<RuntimeValue?> _buffer;
        private readonly int _capacity;
        private readonly object _lock = new object();
        private readonly Queue<TaskState> _waitingReaders = new Queue<TaskState>();
        private readonly Queue<(TaskState st, RuntimeValue? v)> _waitingWriters = new Queue<(TaskState, RuntimeValue?)>();
        private bool _closed;

        public int Capacity => _capacity;
        public bool IsClosed { get { lock (_lock) return _closed; } }
        public int Count { get { lock (_lock) return _buffer.Count; } }
        // Shared across all ChannelValue copies of this channel. Late-bound on
        // first send (or pre-bound from a typed declaration). Once non-null,
        // every subsequent send is verified against it.
        public RaLanguage.Types.TypeDescriptor? ElementType;

        public AsyncChannel(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 1;
            _buffer = new Queue<RuntimeValue?>(_capacity);
        }

        private sealed class TaskState
        {
            public readonly ManualResetEventSlim Event = new ManualResetEventSlim(false);
            public RuntimeValue? Value;
            public bool Cancelled;
            public bool Closed;
            public bool Completed;
        }

        public bool TrySendImmediate(RuntimeValue? value)
        {
            lock (_lock)
            {
                if (_closed) return false;
                if (_waitingReaders.Count > 0)
                {
                    var r = _waitingReaders.Dequeue();
                    r.Value = value;
                    r.Completed = true;
                    r.Event.Set();
                    return true;
                }
                if (_buffer.Count < _capacity)
                {
                    _buffer.Enqueue(value);
                    return true;
                }
                return false;
            }
        }

        public bool Send(RuntimeValue? value, CancellationToken token)
        {
            TaskState? st = null;
            lock (_lock)
            {
                if (_closed) return false;
                if (_waitingReaders.Count > 0)
                {
                    var r = _waitingReaders.Dequeue();
                    r.Value = value;
                    r.Completed = true;
                    r.Event.Set();
                    return true;
                }
                if (_buffer.Count < _capacity)
                {
                    _buffer.Enqueue(value);
                    return true;
                }
                st = new TaskState { Value = value };
                _waitingWriters.Enqueue((st, value));
            }

            using var reg = token.CanBeCanceled ? token.Register(static s =>
            {
                var ts = (TaskState)s!;
                ts.Cancelled = true;
                ts.Event.Set();
            }, st) : default;

            st.Event.Wait();
            if (st.Cancelled) return false;
            return st.Completed;
        }

        public (bool ok, RuntimeValue? value, bool closed) Receive(CancellationToken token)
        {
            TaskState? st = null;
            lock (_lock)
            {
                if (_buffer.Count > 0)
                {
                    var v = _buffer.Dequeue();
                    PromoteWriter();
                    return (true, v, false);
                }
                if (_closed)
                {
                    return (false, null, true);
                }
                st = new TaskState();
                _waitingReaders.Enqueue(st);
            }

            using var reg = token.CanBeCanceled ? token.Register(static s =>
            {
                var ts = (TaskState)s!;
                ts.Cancelled = true;
                ts.Event.Set();
            }, st) : default;

            st.Event.Wait();
            if (st.Cancelled) return (false, null, false);
            if (st.Closed) return (false, null, true);
            return (st.Completed, st.Value, false);
        }

        public bool TryReceiveImmediate(out RuntimeValue? value, out bool closed)
        {
            lock (_lock)
            {
                if (_buffer.Count > 0)
                {
                    value = _buffer.Dequeue();
                    closed = false;
                    PromoteWriter();
                    return true;
                }
                value = null;
                closed = _closed;
                return false;
            }
        }

        private void PromoteWriter()
        {
            if (_waitingWriters.Count == 0) return;
            var (w, v) = _waitingWriters.Dequeue();
            _buffer.Enqueue(v);
            w.Completed = true;
            w.Event.Set();
        }

        // Resolves when the channel has a value to receive or is closed.
        // Does NOT consume the value. Used by select() so multiple sources can
        // be polled concurrently without draining any of them prematurely.
        // Implemented as a sub-2ms poll loop because the channel waiter queues
        // are tightly coupled to consume-on-wake semantics; a peek-style waiter
        // would require restructuring them. Acceptable for select's low fan-in,
        // low frequency use; revisit if profiling shows it as a hotspot.
        public async System.Threading.Tasks.Task WhenReadable(CancellationToken token)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_buffer.Count > 0 || _closed) return;
                }
                try { await System.Threading.Tasks.Task.Delay(2, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_closed) return;
                _closed = true;
                while (_waitingReaders.Count > 0)
                {
                    var r = _waitingReaders.Dequeue();
                    r.Closed = true;
                    r.Event.Set();
                }
                while (_waitingWriters.Count > 0)
                {
                    var (w, _) = _waitingWriters.Dequeue();
                    w.Cancelled = true;
                    w.Event.Set();
                }
            }
        }
    }
}

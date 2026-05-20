using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        // Peek-waiters used by select(): each registered TaskCompletionSource is set
        // (without consuming) the moment the channel becomes readable. Replaces the
        // previous 2ms polling loop, which both wasted CPU and added latency.
        private readonly List<TaskCompletionSource<bool>> _peekWaiters = new List<TaskCompletionSource<bool>>();

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
            List<TaskCompletionSource<bool>>? peeks = null;
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
                    peeks = DrainPeekWaitersLocked();
                    goto signalPeeks;
                }
                return false;
            }
        signalPeeks:
            SignalPeeks(peeks);
            return true;
        }

        public bool Send(RuntimeValue? value, CancellationToken token)
        {
            TaskState? st = null;
            List<TaskCompletionSource<bool>>? peeks = null;
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
                    peeks = DrainPeekWaitersLocked();
                    SignalPeeks(peeks);
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

        // Resolves when the channel has a value to receive or is closed. Does NOT
        // consume the value, so multiple select() arms can race on the same channel
        // without one of them stealing the item. Event-driven: senders and Close()
        // signal the registered peek-waiters directly, so latency is bounded by the
        // scheduler, not by a poll interval.
        public Task WhenReadable(CancellationToken token)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (_buffer.Count > 0 || _closed) return Task.CompletedTask;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _peekWaiters.Add(tcs);
            }

            if (!token.CanBeCanceled) return tcs.Task;

            var reg = token.Register(static state =>
            {
                var t = (TaskCompletionSource<bool>)state!;
                t.TrySetResult(false);
            }, tcs);
            tcs.Task.ContinueWith(static (_, state) =>
            {
                ((CancellationTokenRegistration)state!).Dispose();
            }, reg, TaskScheduler.Default);
            return tcs.Task;
        }

        private List<TaskCompletionSource<bool>>? DrainPeekWaitersLocked()
        {
            if (_peekWaiters.Count == 0) return null;
            var copy = new List<TaskCompletionSource<bool>>(_peekWaiters);
            _peekWaiters.Clear();
            return copy;
        }

        private static void SignalPeeks(List<TaskCompletionSource<bool>>? peeks)
        {
            if (peeks == null) return;
            for (int i = 0; i < peeks.Count; i++)
            {
                peeks[i].TrySetResult(true);
            }
        }

        public void Close()
        {
            List<TaskCompletionSource<bool>>? peeks = null;
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
                peeks = DrainPeekWaitersLocked();
            }
            SignalPeeks(peeks);
        }
    }
}

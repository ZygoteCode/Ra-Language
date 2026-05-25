using System;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime.Async
{
    public enum RaTaskStatus : byte
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Faulted = 3,
        Cancelled = 4
    }

    // RaTaskCore IS the work item. By implementing IThreadPoolWorkItem, the scheduler
    // can queue the task directly to the thread pool without allocating a closure
    // wrapper. The body delegate and execution context are stored as fields on the
    // task itself, which we already pay for.
    //
    // Synchronisation model (see also the scheduler audit, item 4.7):
    //
    //   * Completion is signalled through both a `TaskCompletionSource` (so .NET-aware
    //     code can `await` AsTask) AND a `ManualResetEventSlim` (so `Wait` can block
    //     directly on the event without going through Task's GetAwaiter().GetResult()
    //     machinery). The MRES exists because tree-walking interpretation is
    //     synchronous — `await` on a Ra task has to block the host thread, and going
    //     through `Task.GetAwaiter().GetResult()` allocates the awaiter struct and
    //     pays a state-machine cost per await. MRES.Wait(token) hits the OS primitive
    //     directly.
    //   * `WaitAsync` returns the TCS Task for callers that DO have an async-capable
    //     frame to compose with. Currently used by the runtime only opportunistically
    //     (e.g. future CPS-style scheduler upgrade); kept here as the seam for that
    //     transition.
    //   * `s_blockingWaits` counts every Wait() that actually parked the host thread.
    //     Read by `RaTaskCore.BlockingWaitCount` for diagnostics — a high value
    //     under fan-out indicates the cooperative scheduler is not yet in use and
    //     the program is paying the sync-over-async tax described in the audit.
    //
    // M70 pooling:
    //
    //   `Rent` factories return a Pending core. `_taskValueRefs` is bumped by every
    //   `TaskValue` wrapper holding this core; the wrapper's finalizer decrements.
    //   When the count hits zero AND the core has completed, ownership returns to
    //   the process-wide pool via `Return`. `Return` resets the per-instance state
    //   (fresh `TaskCompletionSource`, `MRES.Reset`, cleared body/ctx/timer/error)
    //   and the next `Rent` reuses without re-allocating the `MRES` or the C# object
    //   header.
    public sealed class RaTaskCore : IThreadPoolWorkItem
    {
        private TaskCompletionSource<RuntimeValue?> _tcs
            = new TaskCompletionSource<RuntimeValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _doneEvent = new ManualResetEventSlim(initialState: false);
        private long _id;
        private static long s_nextId;
        private static long s_blockingWaits;

        // Number of host-thread blocking Wait() calls served by this process.
        // Diagnostic-only — use to detect fiber-pool starvation patterns.
        public static long BlockingWaitCount => Interlocked.Read(ref s_blockingWaits);

        // -------- M70 pool ----------------------------------------
        //
        // Bounded process-wide free list. Cap protects against
        // pathological growth (fan-out spikes that never drain).
        // Overflow returns drop the core; GC reclaims as usual.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<RaTaskCore> s_pool
            = new System.Collections.Concurrent.ConcurrentQueue<RaTaskCore>();
        private const int PoolCapacity = 256;
        private static int s_poolCount;

        // Diagnostic: how many pool hits served `Rent` so far.
        private static long s_poolHits;
        public static long PoolHitCount => Interlocked.Read(ref s_poolHits);
        public static int PoolFreeCount => Volatile.Read(ref s_poolCount);

        // Owners-by-TaskValue refcount. Bumped in `TaskValue` ctor,
        // decremented in `TaskValue` finalizer (or explicit Release).
        // When the count returns to zero on a completed core, the core
        // is recycled.
        private int _taskValueRefs;

        public CancellationScope CancellationScope { get; private set; }
        public RaTaskCore? Parent { get; private set; }
        public string DebugName { get; set; }
        public RaTaskStatus Status { get; private set; } = RaTaskStatus.Pending;
        public RuntimeValue? Result { get; private set; }
        public Error? Error { get; private set; }
        public long Id => _id;
        public Task<RuntimeValue?> AsTask => _tcs.Task;
        public CancellationToken Token => CancellationScope.Token;
        public bool IsCompleted => Status >= RaTaskStatus.Completed;
        public bool IsCancelled => Status == RaTaskStatus.Cancelled;
        public bool IsFaulted => Status == RaTaskStatus.Faulted;

        private Func<AsyncContext, ValueTask<ValueResult>>? _body;
        private AsyncContext? _bodyCtx;

        public RaLanguage.Types.TypeDescriptor? ElementType;

        public RaTaskCore(CancellationScope scope, RaTaskCore? parent, string debugName)
        {
            CancellationScope = scope;
            Parent = parent;
            DebugName = debugName;
            _id = Interlocked.Increment(ref s_nextId);
        }

        // M70: factory that prefers a recycled core when one is
        // available. Initial state matches the constructor — caller
        // sees a Pending task ready for `TrySetRunning` /
        // `AttachBody` / scheduler enqueue.
        public static RaTaskCore Rent(CancellationScope scope, RaTaskCore? parent, string debugName)
        {
            if (s_pool.TryDequeue(out var t))
            {
                Interlocked.Decrement(ref s_poolCount);
                Interlocked.Increment(ref s_poolHits);
                t.InitialiseRented(scope, parent, debugName);
                return t;
            }
            return new RaTaskCore(scope, parent, debugName);
        }

        private void InitialiseRented(CancellationScope scope, RaTaskCore? parent, string debugName)
        {
            CancellationScope = scope;
            Parent = parent;
            DebugName = debugName;
            _id = Interlocked.Increment(ref s_nextId);
            // Status, _tcs, _doneEvent, body/ctx, Result/Error, ElementType,
            // timer, ctReg, refcount were cleared by Return(); leave them.
        }

        public bool TrySetRunning()
        {
            if (Status != RaTaskStatus.Pending) return false;
            Status = RaTaskStatus.Running;
            return true;
        }

        public void Complete(RuntimeValue? value)
        {
            if (IsCompleted) return;
            Result = value;
            Status = RaTaskStatus.Completed;
            _tcs.TrySetResult(value);
            _doneEvent.Set();
            TryAutoRecycle();
        }

        public void Fault(Error error)
        {
            if (IsCompleted) return;
            Error = error;
            Status = RaTaskStatus.Faulted;
            _tcs.TrySetResult(null);
            _doneEvent.Set();
            TryAutoRecycle();
        }

        public void CancelObserved()
        {
            if (IsCompleted) return;
            Status = RaTaskStatus.Cancelled;
            _tcs.TrySetResult(null);
            _doneEvent.Set();
            TryAutoRecycle();
        }

        public void RequestCancel()
        {
            CancellationScope.Cancel();
        }

        public void Wait(CancellationToken externalToken = default)
        {
            if (IsCompleted) return;

            // Block directly on the completion event. Avoids the per-await
            // allocations the previous `Task.WaitAny(...Task.Delay(...))` path
            // paid (a Timer-backed Task plus a fresh Task[] array on each call)
            // and skips the TaskAwaiter state-machine cost.
            //
            // Increment the diagnostic counter EXACTLY when this Wait would
            // actually park the calling thread (i.e. completion did not race
            // ahead between the IsCompleted check above and the MRES.Wait).
            Interlocked.Increment(ref s_blockingWaits);

            // Cooperative-scheduler hint. The default ThreadPool executor
            // treats this as a no-op; a future cooperative scheduler can use
            // the bracketing pair (NotifyBlocking / NotifyResumed) to expand
            // its worker count for the duration of the park.
            var sched = FiberExecutorRegistry.Current;
            sched.NotifyBlocking();
            try
            {
                if (!externalToken.CanBeCanceled)
                {
                    _doneEvent.Wait();
                    return;
                }

                _doneEvent.Wait(externalToken);
            }
            finally
            {
                sched.NotifyResumed();
            }
        }

        // Async-friendly wait. Returns the underlying TCS Task so a caller in
        // an async-capable frame can compose without blocking a host thread.
        // The synchronous interpreter still uses Wait(), but external
        // integrations and the future CPS scheduler can build on top of this.
        public Task<RuntimeValue?> WaitAsync() => _tcs.Task;

        internal void AttachBody(Func<AsyncContext, ValueTask<ValueResult>> body, AsyncContext ctx)
        {
            _body = body;
            _bodyCtx = ctx;
        }

        void IThreadPoolWorkItem.Execute()
        {
            var body = _body;
            var ctx = _bodyCtx;
            _body = null;
            _bodyCtx = null;

            try
            {
                if (CancellationScope.IsCancelled)
                {
                    CancelObserved();
                    return;
                }
                if (body == null || ctx == null)
                {
                    Complete(null);
                    return;
                }

                // Run the body. If it completes synchronously the result is
                // already there; if it suspends, register a continuation so
                // the worker thread is released immediately instead of
                // blocking on GetAwaiter().GetResult().
                var task = body(ctx);
                if (task.IsCompletedSuccessfully)
                {
                    var (value, err) = task.Result;
                    FinishBody(value, err);
                }
                else
                {
                    task.AsTask().ContinueWith(static (t, state) =>
                    {
                        var self = (RaTaskCore)state!;
                        try
                        {
                            var (value, err) = t.Result;
                            self.FinishBody(value, err);
                        }
                        catch (OperationCanceledException)
                        {
                            self.CancelObserved();
                        }
                        catch (Exception ex)
                        {
                            var pos = new RaLanguage.Lexer.Position(0, 0, 0, "<async>", "");
                            self.Fault(new RuntimeError(pos, pos, $"Unhandled exception in async task '{self.DebugName}': {ex.Message}", null));
                        }
                    }, this, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            catch (OperationCanceledException)
            {
                CancelObserved();
            }
            catch (Exception ex)
            {
                var pos = new RaLanguage.Lexer.Position(0, 0, 0, "<async>", "");
                Fault(new RuntimeError(pos, pos, $"Unhandled exception in async task '{DebugName}': {ex.Message}", null));
            }
        }

        private void FinishBody(RuntimeValue? value, Error? err)
        {
            if (CancellationScope.IsCancelled && err != null && AsyncScheduler.IsCancellationError(err))
            {
                CancelObserved();
                return;
            }
            if (err != null) Fault(err);
            else Complete(value);
        }

        // Completes this task after `delayMs` without occupying a thread pool worker.
        // Backed by System.Threading.Timer, which dispatches on a thread-pool callback
        // for ~zero-cost duration. Cancellation cancels the timer immediately.
        internal void ArmCompletionTimer(int delayMs)
        {
            if (delayMs <= 0)
            {
                Complete(RaLanguage.Interpreter.Values.Primitives.NullValue.Null);
                return;
            }

            Timer? timer = null;
            CancellationTokenRegistration ctReg = default;

            timer = new Timer(static state =>
            {
                var self = (RaTaskCore)state!;
                if (!self.IsCompleted) self.Complete(RaLanguage.Interpreter.Values.Primitives.NullValue.Null);
                var t = Interlocked.Exchange(ref self._completionTimer, null);
                t?.Dispose();
                var reg = self._completionCtReg;
                self._completionCtReg = default;
                try { reg.Dispose(); } catch { }
            }, this, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            _completionTimer = timer;

            if (CancellationScope.IsCancelled)
            {
                CancelObserved();
                timer.Dispose();
                _completionTimer = null;
                return;
            }

            var token = CancellationScope.Token;
            if (token.CanBeCanceled)
            {
                ctReg = token.Register(static state =>
                {
                    var self = (RaTaskCore)state!;
                    if (!self.IsCompleted) self.CancelObserved();
                    var t = Interlocked.Exchange(ref self._completionTimer, null);
                    t?.Dispose();
                }, this);
                _completionCtReg = ctReg;
            }

            timer.Change(delayMs, System.Threading.Timeout.Infinite);
        }

        private Timer? _completionTimer;
        private CancellationTokenRegistration _completionCtReg;

        public static RaTaskCore FromCompletedValue(RuntimeValue? value)
        {
            var t = Rent(new CancellationScope(), null, "<completed>");
            t.TrySetRunning();
            t.Complete(value);
            return t;
        }

        public static RaTaskCore FromError(Error error)
        {
            var t = Rent(new CancellationScope(), null, "<faulted>");
            t.TrySetRunning();
            t.Fault(error);
            return t;
        }

        // ---------------- M70 ref counting --------------------------
        //
        // `AcquireOwner` / `ReleaseOwner` are paired by every
        // `TaskValue` wrapper holding this core. The constructor
        // calls Acquire; the finalizer (or explicit dispose) calls
        // Release. When the count returns to zero AND the task has
        // already completed, the core enters the recycle path. A
        // task that completes BEFORE any `TaskValue` is observed
        // (rare — internal-only cores) auto-recycles inside the
        // completion path through `TryAutoRecycle`.
        internal void AcquireOwner()
        {
            Interlocked.Increment(ref _taskValueRefs);
        }

        internal void ReleaseOwner()
        {
            int after = Interlocked.Decrement(ref _taskValueRefs);
            if (after == 0 && IsCompleted) Recycle();
        }

        // Optional explicit return entry-point for callers that
        // bypass `TaskValue` wrapping (e.g. internal join helpers
        // that consume the result then drop the core). No-op if the
        // task is still live or has TaskValue owners.
        public static void Return(RaTaskCore t)
        {
            if (t == null) return;
            if (!t.IsCompleted) return;
            if (Volatile.Read(ref t._taskValueRefs) != 0) return;
            t.Recycle();
        }

        // Inside completion paths a task that nobody wraps in a
        // `TaskValue` is otherwise GC-bound. Pull it back into the
        // pool right after completion so the next `Rent` reuses it.
        private void TryAutoRecycle()
        {
            if (Volatile.Read(ref _taskValueRefs) != 0) return;
            Recycle();
        }

        // Reset the core to a Pending state and enqueue it for reuse.
        // Caller is responsible for ensuring no concurrent observers
        // remain — `ReleaseOwner` at refcount zero + `IsCompleted`
        // provides that guarantee; `Return` re-checks defensively.
        private void Recycle()
        {
            // Bounded pool. Drop on overflow.
            int next = Interlocked.Increment(ref s_poolCount);
            if (next > PoolCapacity)
            {
                Interlocked.Decrement(ref s_poolCount);
                return;
            }
            ResetForPool();
            s_pool.Enqueue(this);
        }

        private void ResetForPool()
        {
            Status = RaTaskStatus.Pending;
            Result = null;
            Error = null;
            _body = null;
            _bodyCtx = null;
            ElementType = null;
            // Old TCS is one-shot; the next renter needs a fresh
            // continuation source.
            _tcs = new TaskCompletionSource<RuntimeValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _doneEvent.Reset();
            // Drop any lingering timer / cancellation registration so
            // a stale callback doesn't fire against the recycled core.
            var oldTimer = Interlocked.Exchange(ref _completionTimer, null);
            oldTimer?.Dispose();
            try { _completionCtReg.Dispose(); } catch { }
            _completionCtReg = default;
            // CancellationScope / Parent / DebugName / _id are
            // re-assigned in `InitialiseRented`.
        }
    }
}

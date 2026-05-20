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
    public sealed class RaTaskCore : IThreadPoolWorkItem
    {
        private readonly TaskCompletionSource<RuntimeValue?> _tcs = new TaskCompletionSource<RuntimeValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _id;
        private static long s_nextId;

        public CancellationScope CancellationScope { get; }
        public RaTaskCore? Parent { get; }
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

        private Func<AsyncContext, ValueResult>? _body;
        private AsyncContext? _bodyCtx;

        public RaLanguage.Types.TypeDescriptor? ElementType;

        public RaTaskCore(CancellationScope scope, RaTaskCore? parent, string debugName)
        {
            CancellationScope = scope;
            Parent = parent;
            DebugName = debugName;
            _id = Interlocked.Increment(ref s_nextId);
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
        }

        public void Fault(Error error)
        {
            if (IsCompleted) return;
            Error = error;
            Status = RaTaskStatus.Faulted;
            _tcs.TrySetResult(null);
        }

        public void CancelObserved()
        {
            if (IsCompleted) return;
            Status = RaTaskStatus.Cancelled;
            _tcs.TrySetResult(null);
        }

        public void RequestCancel()
        {
            CancellationScope.Cancel();
        }

        public void Wait(CancellationToken externalToken = default)
        {
            if (IsCompleted) return;

            if (!externalToken.CanBeCanceled)
            {
                try { _tcs.Task.GetAwaiter().GetResult(); }
                catch { }
                return;
            }

            try
            {
                var waitTask = _tcs.Task;
                if (waitTask.IsCompleted) return;
                int idx = Task.WaitAny(new Task[] { waitTask, Task.Delay(System.Threading.Timeout.Infinite, externalToken) });
                if (idx == 1) externalToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        internal void AttachBody(Func<AsyncContext, ValueResult> body, AsyncContext ctx)
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
                var (value, err) = body(ctx);
                if (CancellationScope.IsCancelled && err != null && AsyncScheduler.IsCancellationError(err))
                {
                    CancelObserved();
                    return;
                }
                if (err != null) Fault(err);
                else Complete(value);
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
            var t = new RaTaskCore(new CancellationScope(), null, "<completed>");
            t.TrySetRunning();
            t.Complete(value);
            return t;
        }

        public static RaTaskCore FromError(Error error)
        {
            var t = new RaTaskCore(new CancellationScope(), null, "<faulted>");
            t.TrySetRunning();
            t.Fault(error);
            return t;
        }
    }
}

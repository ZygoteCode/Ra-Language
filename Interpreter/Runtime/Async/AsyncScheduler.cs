using System;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime.Async
{
    public static class AsyncScheduler
    {
        // Cold path: schedules an async fn body to run on the active fiber executor.
        // Allocation footprint per call:
        //   - 1 CancellationScope (CTS allocated lazily, only if anyone touches Token/Cancel)
        //   - 1 RaTaskCore (also serves as the work item, no closure object needed)
        //   - 1 AsyncContext (single small object, cannot be shared because children
        //                     get distinct cancellation scopes)
        // The previous design also allocated a Task.Run lambda + display class which
        // are now both eliminated.
        public static RaTaskCore Schedule(string name, AsyncContext? parentAsync, Func<AsyncContext, (RuntimeValue?, Error?)> body)
        {
            var childScope = new CancellationScope(parentAsync?.CancellationScope);
            var task = new RaTaskCore(childScope, parentAsync?.CurrentTask, name);
            var childCtx = new AsyncContext(childScope)
            {
                CurrentTask = task,
                InsideAsyncFunction = true
            };

            task.TrySetRunning();
            task.AttachBody(body, childCtx);
            FiberExecutorRegistry.Current.Queue(task);

            return task;
        }

        // Cold path: schedules a task that completes after `delayMs` via a Timer,
        // WITHOUT consuming a thread pool worker for the wait. Required for true
        // parallel fan-out — the previous `Task.Delay().GetAwaiter().GetResult()`
        // inside a fiber pinned a thread per pending sleep, exhausting the pool
        // under fan-out of more than (min thread count) sleeps.
        public static RaTaskCore ScheduleTimer(string name, AsyncContext? parentAsync, int delayMs)
        {
            var childScope = new CancellationScope(parentAsync?.CancellationScope);
            var task = new RaTaskCore(childScope, parentAsync?.CurrentTask, name);
            task.TrySetRunning();
            task.ArmCompletionTimer(delayMs);
            return task;
        }

        public static bool IsCancellationError(Error err)
        {
            if (err is RuntimeError re)
            {
                if (re.Details != null && re.Details.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static Error MakeCancellationError(Position posStart, Position posEnd, RaLanguage.Interpreter.Runtime.Context? ctx, string detail = "Task was cancelled")
        {
            return new RuntimeError(posStart, posEnd, detail, ctx);
        }

        public static Error MakeTimeoutError(Position posStart, Position posEnd, RaLanguage.Interpreter.Runtime.Context? ctx, int ms)
        {
            return new RuntimeError(posStart, posEnd, $"Operation timed out after {ms}ms", ctx);
        }
    }
}

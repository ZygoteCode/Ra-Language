using System.Threading.Tasks;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Decouples scheduling policy from the rest of the runtime.
    //
    // The default executor (ThreadPoolFiberExecutor) maps directly onto the
    // .NET ThreadPool and is the right pick for tasks dominated by I/O or
    // short bursts of CPU work. The Ra audit (item 4.7) flagged that fibers
    // running on real thread-pool workers do not scale to high fan-out
    // because each pending `await` pins one worker (sync-over-async).
    // Future implementations (bounded fiber pool, work-stealing, cooperative
    // reactor, or a CPS-style scheduler that replaces blocking awaits with
    // continuations) can be plugged in via FiberExecutorRegistry.Current
    // without touching the parser, the visitors, or any user-facing
    // semantics. The two hint methods (NotifyBlocking / NotifyResumed) give
    // such schedulers the seam they need: a cooperative scheduler can grow
    // its worker count when many fibers park, shrink when they resume.
    public interface IFiberExecutor
    {
        // Enqueue a unit of work for execution. The implementation owns
        // dispatch policy (preferLocal, FIFO vs LIFO, affinity, etc.).
        void Queue(IThreadPoolWorkItem work);

        // Advisory: the calling thread is about to block on a Wait.
        // Default impl is a no-op; cooperative schedulers can use this to
        // expand the worker pool or hand off pending work to another thread.
        void NotifyBlocking() { }

        // Advisory: the calling thread has resumed from a Wait.
        // Counterpart to NotifyBlocking.
        void NotifyResumed() { }
    }

    internal sealed class ThreadPoolFiberExecutor : IFiberExecutor
    {
        public static readonly ThreadPoolFiberExecutor Instance = new ThreadPoolFiberExecutor();

        public void Queue(IThreadPoolWorkItem work)
        {
            // preferLocal:false avoids LIFO scheduling that would unfairly starve
            // sibling fibers when one parent task spawns many children in a tight
            // loop. We trade marginal locality for predictable progress.
            ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
        }

        // ThreadPoolFiberExecutor relies on the .NET pool's own injection
        // heuristic to grow under contention; we don't add a manual hint here.
        // The interface-default no-ops are inherited intentionally.
    }

    public static class FiberExecutorRegistry
    {
        private static IFiberExecutor _current = ThreadPoolFiberExecutor.Instance;
        public static IFiberExecutor Current
        {
            get => _current;
            set => _current = value ?? ThreadPoolFiberExecutor.Instance;
        }
    }
}

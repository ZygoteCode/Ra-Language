using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Decouples scheduling policy from the rest of the runtime.
    //
    // The default executor maps directly onto the .NET ThreadPool and is the right
    // pick for tasks dominated by I/O or short bursts of CPU work. Future
    // implementations (bounded fiber pool, work-stealing, cooperative reactor)
    // can be plugged in via FiberExecutorRegistry.Current without touching either
    // the parser, the visitors, or any user-facing semantics.
    public interface IFiberExecutor
    {
        void Queue(IThreadPoolWorkItem work);
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

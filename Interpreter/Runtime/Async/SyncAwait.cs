using System.Threading.Tasks;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Sync-blocking adapter for sites that genuinely cannot become async
    // (annotation processors that run at definition time, module-loading
    // top frames, validators that never see a Ra `await`). The visitor
    // pipeline itself is async; SyncAwait collapses a ValueTask back to its
    // value with one host-thread block, used only at boundaries we know
    // never propagate a real await.
    //
    // Anywhere on the call path that may transit through `await x` in user
    // code MUST stay async — using SyncAwait there pins a worker for the
    // duration of the wait, defeating the visitor-pipeline async fix
    // documented in the v5.7 scheduler audit.
    internal static class SyncAwait
    {
        public static T Get<T>(ValueTask<T> task)
        {
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.AsTask().GetAwaiter().GetResult();
        }
    }
}

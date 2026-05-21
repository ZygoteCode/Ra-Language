using System.Threading.Tasks;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Thread-local AsyncContext override used to bridge the spawn site and the
    // dispatch site of an async function call. The spawn visitor needs to tell
    // ExecuteAsyncDispatch "use *this* parent for the scheduled child" but the
    // function's static Context object is shared across concurrent fibers, so it
    // cannot carry per-call data safely. A thread-local push/pop is correct here
    // because the dispatch always runs synchronously on the same thread that set
    // the override.
    internal static class AsyncContextOverride
    {
        [ThreadStatic] private static AsyncContext? _current;

        public static AsyncContext? Current => _current;

        public static AsyncContext? Push(AsyncContext? value)
        {
            var prior = _current;
            _current = value;
            return prior;
        }

        public static void Pop(AsyncContext? prior)
        {
            _current = prior;
        }
    }
}

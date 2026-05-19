using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Single dedicated thread that runs in STA apartment mode (Windows) and serves as
    /// dispatch target for `@dll_import(sta_thread = true)` calls. Most COM/UI Win32 APIs
    /// require STA; calling them from a thread-pool worker may corrupt or fail.
    ///
    /// On non-Windows platforms STA is a no-op (the thread still serializes calls).
    /// </summary>
    public static class StaThreadDispatcher
    {
        private static readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
        private static readonly Lazy<Thread> _worker = new(StartWorker);

        public static T Invoke<T>(Func<T> action)
        {
            var thread = _worker.Value;
            if (Thread.CurrentThread == thread)
            {
                return action();
            }

            T? result = default;
            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            _queue.Add(() =>
            {
                try { result = action(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error != null) throw new InvocationException(error);
            return result!;
        }

        public static void InvokeVoid(Action action)
        {
            Invoke<int>(() => { action(); return 0; });
        }

        private static Thread StartWorker()
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ra-ffi-sta"
            };
            try { t.SetApartmentState(ApartmentState.STA); } catch { /* non-Windows */ }
            t.Start();
            return t;
        }

        private static void WorkerLoop()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch { /* swallowed: per-call try/catch above already captured */ }
            }
        }

        public sealed class InvocationException : Exception
        {
            public InvocationException(Exception inner) : base(inner.Message, inner) { }
        }
    }
}

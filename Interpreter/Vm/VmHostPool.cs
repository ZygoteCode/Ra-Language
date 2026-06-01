using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter.Vm
{
    // A reusable (Interpreter, VmExecutor) pair — the "host" a function /
    // method body runs against. Both are otherwise allocated fresh on EVERY
    // call (the call fast paths in FunctionValue / BoundClassMethodValue did
    // `new Interpreter()` + `new VmExecutor(...)` per invocation). Neither
    // carries per-call state that survives the call:
    //
    //   * VmExecutor's only field is the readonly `_interpreter`; all
    //     execution state lives in the frame / ctx / locals passed to
    //     Execute, so a single executor is reentrant-safe (IrExpressionEvaluator
    //     already caches and reuses one per thread for nested sub-expression
    //     evaluation).
    //   * Interpreter's only mutable per-instance state is `Labels`
    //     (goto/label), which ResetForReuse clears before the host returns to
    //     the pool — so labels never leak across call boundaries.
    public sealed class VmHost
    {
        public readonly Interpreter Interpreter;
        public readonly VmExecutor Executor;

        public VmHost()
        {
            Interpreter = new Interpreter();
            Executor = new VmExecutor(Interpreter);
        }
    }

    // Per-thread LIFO pool of VmHosts. Discipline mirrors VmExecutor's argList
    // pool exactly:
    //
    //   * Rent at call entry; Return ONLY on synchronous completion. A
    //     suspended (awaited) Execute still references its host across the
    //     await, so returning it would alias a live host into the pool — the
    //     caller leaves it for the GC on that rare path instead.
    //   * Nested / recursive calls each Rent a DISTINCT host (the outer call's
    //     host is still out of the pool, not yet returned), so the per-host
    //     `Labels` state stays isolated exactly as a fresh `new Interpreter()`
    //     did. The pool is thread-static, so no host is ever shared across
    //     threads / fibers.
    public static class VmHostPool
    {
        [System.ThreadStatic] private static Stack<VmHost>? t_pool;
        private const int PoolCap = 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VmHost Rent()
        {
            var pool = t_pool;
            if (pool != null && pool.Count > 0) return pool.Pop();
            return new VmHost();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(VmHost host)
        {
            host.Interpreter.ResetForReuse();
            var pool = t_pool ??= new Stack<VmHost>();
            if (pool.Count < PoolCap) pool.Push(host);
        }
    }
}

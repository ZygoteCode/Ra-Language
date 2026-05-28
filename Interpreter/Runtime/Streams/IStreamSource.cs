using System.Threading.Tasks;

namespace RaLanguage.Interpreter.Runtime.Streams
{
    // The contract for any pull iterator behind a StreamValue. Implementations
    // are concrete classes (devirtualisable, AOT-safe).
    //
    // PullNext returns the next element OR a Done result. The caller (terminal
    // operator or `for x in s {}` loop) drives the pipeline by repeatedly
    // calling PullNext until Done. Operators wrap an upstream source and
    // forward PullNext through their own transform, so the chain composes
    // bottom-up without intermediate materialisation.
    //
    // Close releases any resource the source holds (file handle, network
    // socket, child task). For the built-in operator family it is a no-op;
    // library-defined sources override it. Idempotent.
    //
    // PullNext is async because user-defined lambdas threaded through
    // `stream_map(s, fn)` may themselves be async (or call into async
    // builtins). For purely synchronous chains the ValueTask short-circuits
    // (no allocation, no continuation), so we pay nothing for the async
    // surface on the hot path.
    public interface IStreamSource
    {
        ValueTask<StreamPullResult> PullNext(Context ctx);
        void Close();
    }
}

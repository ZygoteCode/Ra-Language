using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Streams;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Streams
{
    // Sync pull-based lazy stream. Holds:
    //   * an IStreamSource — the head of the pipeline; operators chain by
    //     wrapping the previous stream's Source in a new IStreamSource.
    //   * IsDone / IsCancelled / TerminalError flags — sticky terminal state
    //     so the next PullNext after exhaustion is a constant-time done.
    //
    // A StreamValue is consumed at most once. Operator wrappers take
    // ownership of their upstream — PullNext drains it. Re-running a pipeline
    // means rebuilding it from a source.
    //
    // The Type tag is Stream (sync); the async push/pull counterpart uses
    // AsyncStreamValue with RuntimeValueType.AsyncStream.
    public sealed class StreamValue : RuntimeValue
    {
        public IStreamSource Source { get; private set; }
        public TypeDescriptor? ElementType { get; set; }

        public bool IsDone { get; private set; }
        public bool IsCancelled { get; private set; }

        public override RuntimeValueType Type => RuntimeValueType.Stream;

        // Streams are reference values: aliasing a stream means sharing the
        // pipeline. IsCopy = false matches lists/maps. Spawning across fibers
        // is unsafe by default (the pull machine is not thread-safe).
        public override bool IsCopy => false;
        public override bool IsSync => false;

        public StreamValue(IStreamSource source, TypeDescriptor? elementType = null)
        {
            Source = source;
            ElementType = elementType;
        }

        public override RuntimeValue Copy()
        {
            // Streams do not support structural copy. Cloning a half-consumed
            // pipeline would fork the upstream's state silently — exactly the
            // class of bug the design rejects. Return self; the caller's
            // memory-model contract is "containers alias by default" and a
            // stream is treated as a container of pull state.
            return this;
        }

        public async ValueTask<StreamPullResult> PullNext(Context ctx)
        {
            if (IsCancelled || IsDone) return StreamPullResult.DoneResult;
            var r = await Source.PullNext(ctx);
            if (r.Done)
            {
                IsDone = true;
                Source.Close();
            }
            return r;
        }

        public void Cancel()
        {
            if (IsCancelled) return;
            IsCancelled = true;
            IsDone = true;
            Source.Close();
        }

        public void CloseSource()
        {
            if (IsDone) return;
            IsDone = true;
            Source.Close();
        }

        public override string ToString() => IsDone ? "<stream done>" : (IsCancelled ? "<stream cancelled>" : "<stream>");
        public override bool IsTrue() => !IsDone;
    }
}

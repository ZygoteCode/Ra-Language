using System.Threading.Tasks;
using System;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Centralised async wrapping for member methods (class, struct, trait).
    //
    // The synchronous body is provided as a thunk. When the caller flagged the
    // method as async or async-stream, the thunk is scheduled on a fiber and the
    // caller receives a TaskValue / AsyncStreamValue immediately. Otherwise the
    // thunk runs inline. This keeps every method-bound value class free from
    // boilerplate.
    public static class AsyncMethodDispatch
    {
        public static RuntimeResult Dispatch(bool isAsync, bool isAsyncStream, string fnName, Context callerCtx, Position posStart, Position posEnd, Func<AsyncContext?, RuntimeResult> syncBody)
        {
            if (!isAsync && !isAsyncStream)
            {
                return syncBody(callerCtx?.AsyncCtx);
            }

            var parentAsync = callerCtx?.AsyncCtx;

            if (isAsyncStream)
            {
                var stream = new AsyncStreamCore(8, parentAsync?.CancellationScope);
                var streamValue = new AsyncStreamValue(stream);
                streamValue.SetContext(callerCtx!).SetPos(posStart, posEnd);
                var producer = AsyncScheduler.Schedule($"stream:{fnName}", parentAsync, childAsyncCtx =>
                {
                    childAsyncCtx.InsideAsyncStream = true;
                    childAsyncCtx.CurrentStreamProducer = new StreamProducerAdapter(stream, streamValue);
                    var bodyRes = syncBody(childAsyncCtx);
                    stream.Close();
                    if (bodyRes.Error != null) return (null, bodyRes.Error);
                    return (bodyRes.FuncReturnValue ?? bodyRes.Value, null);
                });
                stream.AttachProducer(producer);
                return new RuntimeResult().Success(streamValue);
            }

            var task = AsyncScheduler.Schedule($"async:{fnName}", parentAsync, childAsyncCtx =>
            {
                childAsyncCtx.InsideAsyncFunction = true;
                var bodyRes = syncBody(childAsyncCtx);
                if (bodyRes.Error != null) return (null, bodyRes.Error);
                return (bodyRes.FuncReturnValue ?? bodyRes.Value, null);
            });
            return new RuntimeResult().Success(new TaskValue(task).SetContext(callerCtx!).SetPos(posStart, posEnd));
        }

    }

    public sealed class StreamProducerAdapter : IAsyncStreamProducer
    {
        private readonly AsyncStreamCore _core;
        public AsyncStreamValue? OwnerValue { get; }
        public StreamProducerAdapter(AsyncStreamCore core, AsyncStreamValue? owner) { _core = core; OwnerValue = owner; }
        public bool Emit(RuntimeValue value) => _core.Emit(value);
        public System.Threading.CancellationToken Token => _core.Token;
    }
}

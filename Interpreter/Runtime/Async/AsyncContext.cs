using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Async
{
    public sealed class AsyncContext
    {
        public CancellationScope CancellationScope { get; }
        public RaTaskCore? CurrentTask { get; set; }
        public bool InsideAsyncFunction { get; set; }
        public bool InsideAsyncStream { get; set; }
        public IAsyncStreamProducer? CurrentStreamProducer { get; set; }

        public AsyncContext(CancellationScope scope)
        {
            CancellationScope = scope;
        }

        public AsyncContext CreateChild()
        {
            return new AsyncContext(new CancellationScope(CancellationScope))
            {
                CurrentTask = CurrentTask,
                InsideAsyncFunction = InsideAsyncFunction,
                InsideAsyncStream = InsideAsyncStream,
                CurrentStreamProducer = CurrentStreamProducer
            };
        }

        public CancellationToken Token => CancellationScope.Token;
    }

    public interface IAsyncStreamProducer
    {
        bool Emit(RaLanguage.Interpreter.Values.RuntimeValue value);
        CancellationToken Token { get; }
        RaLanguage.Interpreter.Values.Async.AsyncStreamValue? OwnerValue { get; }
    }
}

using System;
using System.Threading;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime.Async
{
    public sealed class AsyncStreamCore
    {
        private readonly AsyncChannel _channel;
        private RaTaskCore? _producerTask;
        public CancellationScope CancellationScope { get; }
        public bool ProducerStarted { get; private set; }
        public CancellationToken Token => CancellationScope.Token;
        public Error? TerminalError { get; private set; }
        public RaLanguage.Types.TypeDescriptor? ElementType;

        public AsyncStreamCore(int bufferCapacity, CancellationScope? parentScope)
        {
            _channel = new AsyncChannel(bufferCapacity <= 0 ? 1 : bufferCapacity);
            CancellationScope = new CancellationScope(parentScope);
        }

        public void AttachProducer(RaTaskCore producerTask)
        {
            _producerTask = producerTask;
            ProducerStarted = true;
        }

        public bool Emit(RuntimeValue value)
        {
            return _channel.Send(value, CancellationScope.Token);
        }

        public (bool ok, RuntimeValue? value, bool closed, Error? error) PullNext(CancellationToken externalToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, CancellationScope.Token);
            var (ok, value, closed) = _channel.Receive(linked.Token);
            if (!ok && !closed && _producerTask != null && _producerTask.IsFaulted)
            {
                return (false, null, true, _producerTask.Error);
            }
            return (ok, value, closed, null);
        }

        public void Close()
        {
            _channel.Close();
        }

        public void Cancel()
        {
            CancellationScope.Cancel();
            _channel.Close();
        }

        public void RecordTerminalError(Error err)
        {
            TerminalError = err;
        }

        public System.Threading.Tasks.Task WhenReadable(CancellationToken token)
        {
            return _channel.WhenReadable(token);
        }
    }
}

using RaLanguage.Errors;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Parser
{
    public class ParserResult : IDisposable
    {
        public Error? Error { get; private set; }
        public AstNode? Node { get; private set; }
        public int LastRegisteredAdvanceCount { get; private set; } = 0;
        public int AdvanceCount { get; private set; } = 0;
        public int ToReverseCount { get; private set; } = 0;

        private bool _disposed;

        public void RegisterAdvancement()
        {
            LastRegisteredAdvanceCount = 1;
            AdvanceCount++;
        }

        public AstNode Register(ParserResult res)
        {
            LastRegisteredAdvanceCount = res.AdvanceCount;
            AdvanceCount += res.AdvanceCount;
            if (res.Error != null) Error = res.Error;
            return res.Node!;
        }

        public AstNode? TryRegister(ParserResult res)
        {
            if (res.Error != null)
            {
                ToReverseCount = res.AdvanceCount;
                return null;
            }
            return Register(res);
        }

        private void DisposeInternal()
        {
            if (!_disposed)
            {
                Node = null;
                Error = null;
                LastRegisteredAdvanceCount = 0;
                AdvanceCount = 0;
                ToReverseCount = 0;
                _disposed = true;
            }
        }

        public void Dispose()
        {
            DisposeInternal();
            GC.SuppressFinalize(this);
        }

        public ParserResult Success(AstNode node)
        {
            var resultNode = node;
            DisposeInternal();
            Node = resultNode;
            return this;
        }

        public ParserResult Failure(Error error)
        {
            var resultError = error;
            DisposeInternal();
            Error = resultError;
            return this;
        }
    }
}
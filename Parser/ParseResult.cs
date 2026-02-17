using RaLanguage.Errors;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Parser
{
    public class ParseResult
    {
        public Error? Error { get; private set; }
        public AstNode? Node { get; private set; }
        public int LastRegisteredAdvanceCount { get; private set; } = 0;
        public int AdvanceCount { get; private set; } = 0;
        public int ToReverseCount { get; private set; } = 0;

        public void RegisterAdvancement()
        {
            LastRegisteredAdvanceCount = 1;
            AdvanceCount++;
        }

        public AstNode Register(ParseResult res)
        {
            LastRegisteredAdvanceCount = res.AdvanceCount;
            AdvanceCount += res.AdvanceCount;
            if (res.Error != null) Error = res.Error;
            return res.Node;
        }

        public AstNode? TryRegister(ParseResult res)
        {
            if (res.Error != null)
            {
                ToReverseCount = res.AdvanceCount;
                return null;
            }
            return Register(res);
        }

        public ParseResult Success(AstNode node)
        {
            Node = node;
            return this;
        }

        public ParseResult Failure(Error error)
        {
            if (Error == null || LastRegisteredAdvanceCount == 0)
            {
                Error = error;
            }
            return this;
        }
    }
}
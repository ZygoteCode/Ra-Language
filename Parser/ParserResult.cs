using RaLanguage.Errors;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Parser
{
    public class ParserResult
    {
        public Error? Error { get; set; }
        public AstNode? Node { get; private set; }
        public int LastRegisteredAdvanceCount { get; private set; } = 0;
        public int AdvanceCount { get; private set; } = 0;
        public int ToReverseCount { get; private set; } = 0;
        public DiagnosticBag Diagnostics { get; } = new DiagnosticBag();

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
            Diagnostics.AddRange(res.Diagnostics);
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

        public ParserResult Success(AstNode node)
        {
            var resultNode = node;
            Node = resultNode;
            return this;
        }

        public ParserResult Failure(Error error)
        {
            var resultError = error;
            Error = resultError;
            Diagnostics.AddError($"{error.ErrorName}: {error.Details}", error.PositionStart, error.PositionEnd);
            return this;
        }
    }
}
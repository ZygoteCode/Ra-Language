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
            Node = node;
            return this;
        }

        public ParserResult Failure(Error error)
        {
            Error = error;
            // Preserve the full Diagnostic (severity, code, span, label, help, chain).
            // The previous implementation flattened to a plain string and lost span / code info.
            // Dedupe: if the bag already holds a diagnostic at the same span+code, drop the
            // duplicate so panic-mode recovery doesn't stack identical "expected X" messages
            // emitted by successive parser layers unwinding through the same offending token.
            if (!ContainsSameDiagnostic(error.Diagnostic))
            {
                Diagnostics.Add(error.Diagnostic);
            }
            return this;
        }

        private bool ContainsSameDiagnostic(Diagnostic candidate)
        {
            if (candidate == null) return true;
            var span = candidate.PrimarySpan;
            for (int i = 0; i < Diagnostics.Diagnostics.Count; i++)
            {
                var existing = Diagnostics.Diagnostics[i];
                if (existing.PrimarySpan.Start.Idx == span.Start.Idx &&
                    existing.PrimarySpan.End.Idx == span.End.Idx &&
                    existing.Code == candidate.Code &&
                    string.Equals(existing.Title, candidate.Title, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

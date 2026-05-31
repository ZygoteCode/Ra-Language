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

        // The diagnostic bag is the single largest source of parser allocation:
        // a fresh ParserResult is created for every grammar rule (and the deep
        // expression precedence chain visits ~15 of them for a trivial `a + b`),
        // yet the overwhelming majority never record a diagnostic. Allocating a
        // DiagnosticBag (+ its inner List) up-front for each was pure waste.
        //
        // The bag is now created lazily on first need (an error via Failure, or
        // merging a non-empty child via Register). Empty results never touch the
        // heap for diagnostics, and propagation still works because a child only
        // forces an allocation in `this` when it actually carries diagnostics.
        private DiagnosticBag? _diagnostics;

        public DiagnosticBag Diagnostics => _diagnostics ??= new DiagnosticBag();

        // Null-safe queries used on the hot path / by the driver so a result
        // with no diagnostics is not forced to allocate a bag just to be asked.
        public bool HasErrors => _diagnostics != null && _diagnostics.HasErrors;
        public Diagnostic? FirstError => _diagnostics?.FirstError;

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
            // Only force a bag (in either result) when the child actually carries
            // diagnostics. The common case — a clean sub-parse — allocates nothing.
            var child = res._diagnostics;
            if (child != null && child.Count > 0) Diagnostics.AddRange(child);
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
            // No bag yet ⇒ nothing recorded ⇒ nothing to dedupe against. Avoids
            // forcing a lazy allocation on the first Failure of a result.
            if (_diagnostics == null) return false;
            var span = candidate.PrimarySpan;
            var list = _diagnostics.Diagnostics;
            for (int i = 0; i < list.Count; i++)
            {
                var existing = list[i];
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

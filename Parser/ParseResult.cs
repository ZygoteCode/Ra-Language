using RaLanguage.Errors;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Parser
{
    public class ParseResult
    {
        public AstNode? Node { get; }
        public DiagnosticBag Diagnostics { get; }
        public bool HasErrors => Diagnostics.HasErrors;

        public ParseResult(AstNode? node, DiagnosticBag diagnostics)
        {
            Node = node;
            Diagnostics = diagnostics;
        }
    }
}

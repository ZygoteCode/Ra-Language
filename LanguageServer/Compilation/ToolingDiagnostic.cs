using RaLanguage.LanguageServer.Protocol;

namespace RaLanguage.LanguageServer.Compilation
{
    /// <summary>
    /// Transport-agnostic diagnostic produced by the tooling front-end. Spans are
    /// stored as absolute UTF-16 offsets (<c>StartOffset</c>/<c>EndOffset</c>) so the
    /// owning document can map them to LSP ranges through its own line index.
    /// </summary>
    public sealed class ToolingDiagnostic
    {
        public int StartOffset { get; }
        public int EndOffset { get; }
        public DiagnosticSeverity Severity { get; }
        public string Message { get; }
        public string? Code { get; }
        public string Source { get; }

        public ToolingDiagnostic(
            int startOffset,
            int endOffset,
            DiagnosticSeverity severity,
            string message,
            string? code = null,
            string source = "ra")
        {
            StartOffset = startOffset;
            EndOffset = endOffset < startOffset ? startOffset : endOffset;
            Severity = severity;
            Message = message;
            Code = code;
            Source = source;
        }
    }
}

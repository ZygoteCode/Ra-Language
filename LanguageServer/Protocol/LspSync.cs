namespace RaLanguage.LanguageServer.Protocol
{
    // textDocument/did* notifications, plus the diagnostics push payload and the
    // window/logMessage payload.

    public sealed class DidOpenTextDocumentParams
    {
        public TextDocumentItem TextDocument { get; set; } = new();
    }

    /// <summary>
    /// A single content change. When <see cref="Range"/> is null this is a whole-
    /// document replacement (Full sync); otherwise it is an incremental edit
    /// replacing <see cref="Range"/> with <see cref="Text"/>.
    /// </summary>
    public sealed class TextDocumentContentChangeEvent
    {
        public Range? Range { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public sealed class DidChangeTextDocumentParams
    {
        public VersionedTextDocumentIdentifier TextDocument { get; set; } = new();
        public TextDocumentContentChangeEvent[] ContentChanges { get; set; } = System.Array.Empty<TextDocumentContentChangeEvent>();
    }

    public sealed class DidCloseTextDocumentParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
    }

    public sealed class DidSaveTextDocumentParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public string? Text { get; set; }
    }

    public sealed class DiagnosticRelatedInformation
    {
        public Location Location { get; set; } = new();
        public string Message { get; set; } = string.Empty;
    }

    public sealed class LspDiagnostic
    {
        public Range Range { get; set; } = new();
        public DiagnosticSeverity? Severity { get; set; }
        public string? Code { get; set; }
        public string? Source { get; set; }
        public string Message { get; set; } = string.Empty;
        public DiagnosticRelatedInformation[]? RelatedInformation { get; set; }
    }

    public sealed class PublishDiagnosticsParams
    {
        public string Uri { get; set; } = string.Empty;
        public int? Version { get; set; }
        public LspDiagnostic[] Diagnostics { get; set; } = System.Array.Empty<LspDiagnostic>();
    }

    public sealed class LogMessageParams
    {
        public MessageType Type { get; set; }
        public string Message { get; set; } = string.Empty;

        public LogMessageParams() { }
        public LogMessageParams(MessageType type, string message) { Type = type; Message = message; }
    }
}

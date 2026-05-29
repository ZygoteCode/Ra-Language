using System.Text.Json.Serialization;

namespace RaLanguage.LanguageServer.Protocol
{
    // Common structural LSP types. Modelled as mutable classes with nullable
    // members so the System.Text.Json source generator can both emit them and
    // tolerantly read partial client payloads (missing fields stay null/default).

    /// <summary>Zero-based line + UTF-16 character offset within a document.</summary>
    public sealed class Position
    {
        public int Line { get; set; }
        public int Character { get; set; }

        public Position() { }
        public Position(int line, int character) { Line = line; Character = character; }
    }

    /// <summary>Half-open [start, end) span.</summary>
    public sealed class Range
    {
        public Position Start { get; set; } = new();
        public Position End { get; set; } = new();

        public Range() { }
        public Range(Position start, Position end) { Start = start; End = end; }
    }

    public sealed class Location
    {
        public string Uri { get; set; } = string.Empty;
        public Range Range { get; set; } = new();

        public Location() { }
        public Location(string uri, Range range) { Uri = uri; Range = range; }
    }

    public sealed class LocationLink
    {
        public Range? OriginSelectionRange { get; set; }
        public string TargetUri { get; set; } = string.Empty;
        public Range TargetRange { get; set; } = new();
        public Range TargetSelectionRange { get; set; } = new();
    }

    public sealed class TextDocumentIdentifier
    {
        public string Uri { get; set; } = string.Empty;
    }

    public sealed class VersionedTextDocumentIdentifier
    {
        public string Uri { get; set; } = string.Empty;
        public int Version { get; set; }
    }

    public sealed class TextDocumentItem
    {
        public string Uri { get; set; } = string.Empty;
        public string LanguageId { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Base shape for the many <c>{ textDocument, position }</c> requests.</summary>
    public sealed class TextDocumentPositionParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public Position Position { get; set; } = new();
    }

    public sealed class TextEdit
    {
        public Range Range { get; set; } = new();
        public string NewText { get; set; } = string.Empty;

        public TextEdit() { }
        public TextEdit(Range range, string newText) { Range = range; NewText = newText; }
    }

    public sealed class MarkupContent
    {
        public string Kind { get; set; } = MarkupKind.Markdown;
        public string Value { get; set; } = string.Empty;

        public MarkupContent() { }
        public MarkupContent(string kind, string value) { Kind = kind; Value = value; }

        public static MarkupContent Markdown(string value) => new(MarkupKind.Markdown, value);
        public static MarkupContent PlainText(string value) => new(MarkupKind.PlainText, value);
    }

    public sealed class Command
    {
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("command")] public string CommandId { get; set; } = string.Empty;
        // Arguments intentionally omitted in v1 (no server-side commands wired yet).
    }

    public sealed class WorkspaceFolder
    {
        public string Uri { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}

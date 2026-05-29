using System.Collections.Generic;

namespace RaLanguage.LanguageServer.Protocol
{
    // Request params + result payloads for the language features. Requests that
    // are a plain { textDocument, position } reuse TextDocumentPositionParams
    // (hover, definition, documentHighlight, signatureHelp, prepareRename).

    // ---- Hover ----
    public sealed class Hover
    {
        public MarkupContent Contents { get; set; } = new();
        public Range? Range { get; set; }
    }

    // ---- Completion ----
    public sealed class CompletionContext
    {
        public CompletionTriggerKind TriggerKind { get; set; }
        public string? TriggerCharacter { get; set; }
    }

    public sealed class CompletionParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public Position Position { get; set; } = new();
        public CompletionContext? Context { get; set; }
    }

    public sealed class CompletionItem
    {
        public string Label { get; set; } = string.Empty;
        public CompletionItemKind? Kind { get; set; }
        public string? Detail { get; set; }
        public MarkupContent? Documentation { get; set; }
        public string? InsertText { get; set; }
        public InsertTextFormat? InsertTextFormat { get; set; }
        public string? SortText { get; set; }
        public string? FilterText { get; set; }
        public bool? Preselect { get; set; }
    }

    public sealed class CompletionList
    {
        public bool IsIncomplete { get; set; }
        public CompletionItem[] Items { get; set; } = System.Array.Empty<CompletionItem>();
    }

    // ---- Signature help ----
    public sealed class ParameterInformation
    {
        public string Label { get; set; } = string.Empty;
        public MarkupContent? Documentation { get; set; }
    }

    public sealed class SignatureInformation
    {
        public string Label { get; set; } = string.Empty;
        public MarkupContent? Documentation { get; set; }
        public ParameterInformation[]? Parameters { get; set; }
        public int? ActiveParameter { get; set; }
    }

    public sealed class SignatureHelp
    {
        public SignatureInformation[] Signatures { get; set; } = System.Array.Empty<SignatureInformation>();
        public int? ActiveSignature { get; set; }
        public int? ActiveParameter { get; set; }
    }

    // ---- Document symbols ----
    public sealed class DocumentSymbolParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
    }

    public sealed class DocumentSymbol
    {
        public string Name { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public SymbolKind Kind { get; set; }
        public Range Range { get; set; } = new();
        public Range SelectionRange { get; set; } = new();
        public DocumentSymbol[]? Children { get; set; }
    }

    // ---- References ----
    public sealed class ReferenceContext
    {
        public bool IncludeDeclaration { get; set; }
    }

    public sealed class ReferenceParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public Position Position { get; set; } = new();
        public ReferenceContext Context { get; set; } = new();
    }

    // ---- Document highlight ----
    public sealed class DocumentHighlight
    {
        public Range Range { get; set; } = new();
        public DocumentHighlightKind? Kind { get; set; }
    }

    // ---- Folding ----
    public sealed class FoldingRangeParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
    }

    public sealed class FoldingRange
    {
        public int StartLine { get; set; }
        public int? StartCharacter { get; set; }
        public int EndLine { get; set; }
        public int? EndCharacter { get; set; }
        public string? Kind { get; set; }
        public string? CollapsedText { get; set; }
    }

    // ---- Selection ranges ----
    public sealed class SelectionRangeParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public Position[] Positions { get; set; } = System.Array.Empty<Position>();
    }

    public sealed class SelectionRange
    {
        public Range Range { get; set; } = new();
        public SelectionRange? Parent { get; set; }
    }

    // ---- Semantic tokens ----
    public sealed class SemanticTokensParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
    }

    public sealed class SemanticTokens
    {
        public string? ResultId { get; set; }
        public int[] Data { get; set; } = System.Array.Empty<int>();
    }

    // ---- Rename ----
    public sealed class RenameParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
        public Position Position { get; set; } = new();
        public string NewName { get; set; } = string.Empty;
    }

    public sealed class PrepareRenameResult
    {
        public Range Range { get; set; } = new();
        public string Placeholder { get; set; } = string.Empty;
    }

    public sealed class WorkspaceEdit
    {
        public Dictionary<string, TextEdit[]> Changes { get; set; } = new();
    }

    // ---- Workspace symbols ----
    public sealed class WorkspaceSymbolParams
    {
        public string Query { get; set; } = string.Empty;
    }

    public sealed class SymbolInformation
    {
        public string Name { get; set; } = string.Empty;
        public SymbolKind Kind { get; set; }
        public Location Location { get; set; } = new();
        public string? ContainerName { get; set; }
    }

    // ---- Document links (clickable import paths) ----
    public sealed class DocumentLinkParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new();
    }

    public sealed class DocumentLink
    {
        public Range Range { get; set; } = new();
        public string? Target { get; set; }
        public string? Tooltip { get; set; }
    }
}

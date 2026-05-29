using System.Text.Json;

namespace RaLanguage.LanguageServer.Protocol
{
    // initialize / initialized / shutdown lifecycle payloads and the server's
    // advertised capabilities. The raw client `capabilities` object is kept as a
    // JsonElement so the server can probe individual client features (e.g.
    // semantic-token support) without modelling the entire client capability tree.

    public sealed class ClientInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Version { get; set; }
    }

    public sealed class InitializeParams
    {
        public int? ProcessId { get; set; }
        public ClientInfo? ClientInfo { get; set; }
        public string? Locale { get; set; }
        public string? RootPath { get; set; }
        public string? RootUri { get; set; }
        public WorkspaceFolder[]? WorkspaceFolders { get; set; }

        /// <summary>Raw client capability tree, probed on demand.</summary>
        public JsonElement Capabilities { get; set; }

        public JsonElement InitializationOptions { get; set; }
    }

    public sealed class ServerInfo
    {
        public string Name { get; set; } = "Ra Language Server";
        public string? Version { get; set; }
    }

    public sealed class InitializeResult
    {
        public ServerCapabilities Capabilities { get; set; } = new();
        public ServerInfo ServerInfo { get; set; } = new();
    }

    public sealed class SaveOptions
    {
        public bool IncludeText { get; set; }
    }

    public sealed class TextDocumentSyncOptions
    {
        public bool OpenClose { get; set; }
        public TextDocumentSyncKind Change { get; set; }
        public SaveOptions? Save { get; set; }
    }

    public sealed class CompletionOptions
    {
        public string[]? TriggerCharacters { get; set; }
        public bool ResolveProvider { get; set; }
    }

    public sealed class SignatureHelpOptions
    {
        public string[]? TriggerCharacters { get; set; }
        public string[]? RetriggerCharacters { get; set; }
    }

    public sealed class RenameOptions
    {
        public bool PrepareProvider { get; set; }
    }

    public sealed class DocumentLinkOptions
    {
        public bool ResolveProvider { get; set; }
    }

    public sealed class SemanticTokensLegend
    {
        public string[] TokenTypes { get; set; } = System.Array.Empty<string>();
        public string[] TokenModifiers { get; set; } = System.Array.Empty<string>();
    }

    public sealed class SemanticTokensOptions
    {
        public SemanticTokensLegend Legend { get; set; } = new();
        public bool Full { get; set; }
        public bool Range { get; set; }
    }

    public sealed class WorkspaceFoldersServerCapabilities
    {
        public bool Supported { get; set; }
    }

    public sealed class WorkspaceServerCapabilities
    {
        public WorkspaceFoldersServerCapabilities? WorkspaceFolders { get; set; }
    }

    public sealed class ServerCapabilities
    {
        public string? PositionEncoding { get; set; }
        public TextDocumentSyncOptions? TextDocumentSync { get; set; }
        public bool HoverProvider { get; set; }
        public CompletionOptions? CompletionProvider { get; set; }
        public SignatureHelpOptions? SignatureHelpProvider { get; set; }
        public bool DefinitionProvider { get; set; }
        public bool ReferencesProvider { get; set; }
        public bool DocumentHighlightProvider { get; set; }
        public bool DocumentSymbolProvider { get; set; }
        public bool WorkspaceSymbolProvider { get; set; }
        public RenameOptions? RenameProvider { get; set; }
        public bool FoldingRangeProvider { get; set; }
        public bool SelectionRangeProvider { get; set; }
        public DocumentLinkOptions? DocumentLinkProvider { get; set; }
        public SemanticTokensOptions? SemanticTokensProvider { get; set; }
        public WorkspaceServerCapabilities? Workspace { get; set; }
    }
}

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RaLanguage.LanguageServer.Protocol
{
    /// <summary>
    /// System.Text.Json source-generated serialization context for every type that
    /// crosses the JSON-RPC wire. This is the project's AOT-safety contract: under
    /// <c>PublishTrimmed</c> reflection-based serialization is disabled, so each
    /// concrete params/result/notification type MUST be registered here. The
    /// generator emits metadata + fast-path writers at compile time — no reflection,
    /// no runtime codegen, fully trimmer-analyzable.
    ///
    /// Wire conventions are baked in via the attribute: camelCase property names and
    /// omission of null members (LSP optionals). LSP integer enums use the default
    /// numeric form (NOT a string converter) which is exactly what the protocol wants.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Default)]
    // Lifecycle
    [JsonSerializable(typeof(InitializeParams))]
    [JsonSerializable(typeof(InitializeResult))]
    // Text sync
    [JsonSerializable(typeof(DidOpenTextDocumentParams))]
    [JsonSerializable(typeof(DidChangeTextDocumentParams))]
    [JsonSerializable(typeof(DidCloseTextDocumentParams))]
    [JsonSerializable(typeof(DidSaveTextDocumentParams))]
    // Diagnostics / window
    [JsonSerializable(typeof(PublishDiagnosticsParams))]
    [JsonSerializable(typeof(LogMessageParams))]
    // Shared { textDocument, position } requests (hover, definition, highlight,
    // signatureHelp, prepareRename)
    [JsonSerializable(typeof(TextDocumentPositionParams))]
    // Hover
    [JsonSerializable(typeof(Hover))]
    // Completion
    [JsonSerializable(typeof(CompletionParams))]
    [JsonSerializable(typeof(CompletionList))]
    [JsonSerializable(typeof(CompletionItem))]
    // Signature help
    [JsonSerializable(typeof(SignatureHelp))]
    // Document symbols
    [JsonSerializable(typeof(DocumentSymbolParams))]
    [JsonSerializable(typeof(DocumentSymbol[]))]
    // References / definition (Location | Location[])
    [JsonSerializable(typeof(ReferenceParams))]
    [JsonSerializable(typeof(Location))]
    [JsonSerializable(typeof(Location[]))]
    // Document highlight
    [JsonSerializable(typeof(DocumentHighlight[]))]
    // Folding
    [JsonSerializable(typeof(FoldingRangeParams))]
    [JsonSerializable(typeof(FoldingRange[]))]
    // Selection ranges
    [JsonSerializable(typeof(SelectionRangeParams))]
    [JsonSerializable(typeof(SelectionRange[]))]
    // Semantic tokens
    [JsonSerializable(typeof(SemanticTokensParams))]
    [JsonSerializable(typeof(SemanticTokens))]
    // Rename
    [JsonSerializable(typeof(RenameParams))]
    [JsonSerializable(typeof(WorkspaceEdit))]
    [JsonSerializable(typeof(PrepareRenameResult))]
    [JsonSerializable(typeof(Dictionary<string, TextEdit[]>))]
    // Workspace symbols
    [JsonSerializable(typeof(WorkspaceSymbolParams))]
    [JsonSerializable(typeof(SymbolInformation[]))]
    // Document links
    [JsonSerializable(typeof(DocumentLinkParams))]
    [JsonSerializable(typeof(DocumentLink[]))]
    public sealed partial class RaLspJsonContext : JsonSerializerContext
    {
    }
}

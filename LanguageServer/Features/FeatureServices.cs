using System.Collections.Generic;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    // Engine contracts. Each feature is a small, transport-independent service that
    // turns a managed document (+ request coordinates) into an LSP result. They are
    // stateless and reuse the document's cached compilation, so they are trivially
    // unit-testable against a synthetic RaDocument without any JSON-RPC plumbing.

    public interface IDiagnosticsService
    {
        PublishDiagnosticsParams Compute(RaDocument document);
    }

    public interface ISemanticTokensService
    {
        SemanticTokens Compute(RaDocument document, ISet<string> typeNames);
        SemanticTokensLegend Legend { get; }
    }

    public interface IFoldingRangeService
    {
        FoldingRange[] Compute(RaDocument document);
    }

    public interface ISelectionRangeService
    {
        SelectionRange[] Compute(RaDocument document, Position[] positions);
    }

    public interface IDocumentSymbolService
    {
        DocumentSymbol[] Compute(RaDocument document);
    }

    public interface IHoverService
    {
        Hover? Compute(RaDocument document, Position position);
    }

    public interface ICompletionService
    {
        CompletionList Compute(RaDocument document, Position position, CompletionContext? context);
    }

    public interface ISignatureHelpService
    {
        SignatureHelp? Compute(RaDocument document, Position position);
    }

    public interface IDefinitionService
    {
        Location[]? Compute(RaDocument document, Position position);
    }

    public interface IReferenceService
    {
        Location[]? Compute(RaDocument document, Position position, bool includeDeclaration);
    }

    public interface IDocumentHighlightService
    {
        DocumentHighlight[]? Compute(RaDocument document, Position position);
    }

    public interface IRenameService
    {
        PrepareRenameResult? Prepare(RaDocument document, Position position);
        WorkspaceEdit? Rename(RaDocument document, Position position, string newName);
    }
}

using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Go-to-definition. Prefers the scope-aware <see cref="SemanticModel"/> (precise,
    /// shadow-correct); falls back to structural declarations + declaration-context
    /// occurrences for names the binder leaves unresolved (members, builtins).
    /// </summary>
    public sealed class DefinitionService : IDefinitionService
    {
        /// <summary>Set at initialize; enables cross-file (imported-module) resolution.</summary>
        public WorkspaceIndex? Workspace { get; set; }

        public Location[]? Compute(RaDocument document, Position position)
        {
            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            // Import path string or module alias → jump to the imported file.
            if (Workspace != null)
            {
                var comp = document.GetCompilation();
                var target = ImportNavigator.ResolveAtOffset(comp.Ast, comp.Tokens, offset, doc.FileName, Workspace);
                if (target != null)
                    return new[] { new Location(UriUtil.FromFileSystemPath(target), doc.RangeOf(0, 0)) };
            }

            var bound = document.GetSemanticModel().SymbolAt(offset);
            if (bound != null)
            {
                return new[] { new Location(doc.RawUri, doc.RangeOf(bound.NameStart, bound.NameEnd)) };
            }

            var compilation = document.GetCompilation();
            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token) ||
                token.Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            string name = TokenLocator.Text(token);
            var locations = new List<Location>();

            var index = SymbolIndex.Build(compilation.Ast);
            foreach (var symbol in index.FindByName(name))
            {
                locations.Add(new Location(doc.RawUri, doc.RangeOf(symbol.SelectionStart, symbol.SelectionEnd)));
            }

            if (locations.Count == 0)
            {
                var tokens = compilation.Tokens;
                foreach (int i in IdentifierScanner.FindOccurrences(tokens, name))
                {
                    if (IdentifierScanner.IsDeclaration(tokens, i))
                        locations.Add(new Location(doc.RawUri, doc.RangeOf(tokens[i].PositionStart.Idx, tokens[i].PositionEnd.Idx)));
                }
            }

            // Cross-file: resolve against the workspace, preferring imported modules.
            if (locations.Count == 0 && Workspace != null)
            {
                var imported = new HashSet<string>(
                    Workspace.ResolveImports(doc.FileName, compilation.Ast), System.StringComparer.OrdinalIgnoreCase);
                var hits = Workspace.FindByName(name);
                foreach (var (file, sym) in hits)
                    if (imported.Contains(file.Path))
                        locations.Add(new Location(file.Uri, file.RangeOf(sym.SelectionStart, sym.SelectionEnd)));
                if (locations.Count == 0)
                    foreach (var (file, sym) in hits)
                        locations.Add(new Location(file.Uri, file.RangeOf(sym.SelectionStart, sym.SelectionEnd)));
            }

            return locations.Count > 0 ? locations.ToArray() : null;
        }
    }

    /// <summary>Find-all-references — semantic-model-first, name-based fallback.</summary>
    public sealed class ReferenceService : IReferenceService
    {
        /// <summary>Set at initialize; enables cross-file reference aggregation for global symbols.</summary>
        public WorkspaceIndex? Workspace { get; set; }

        private static bool IsGlobalKind(BoundKind kind) => kind is
            BoundKind.Function or BoundKind.Class or BoundKind.Struct or BoundKind.Record or
            BoundKind.Enum or BoundKind.Interface or BoundKind.Trait or BoundKind.Annotation or
            BoundKind.Delegate or BoundKind.Namespace;

        public Location[]? Compute(RaDocument document, Position position, bool includeDeclaration)
        {
            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            var bound = document.GetSemanticModel().SymbolAt(offset);
            if (bound != null)
            {
                var locs = new List<Location>(bound.References.Count + 1);
                if (includeDeclaration)
                    locs.Add(new Location(doc.RawUri, doc.RangeOf(bound.NameStart, bound.NameEnd)));
                foreach (var r in bound.References)
                    locs.Add(new Location(doc.RawUri, doc.RangeOf(r.Start, r.End)));
                // Top-level symbols can be referenced from other modules: aggregate
                // name occurrences across the workspace (documented name-based boundary).
                if (Workspace != null && IsGlobalKind(bound.Kind))
                    foreach (var (file, start, end) in Workspace.FindOccurrences(bound.Name, doc.FileName))
                        locs.Add(new Location(file.Uri, file.RangeOf(start, end)));
                return locs.Count > 0 ? locs.ToArray() : null;
            }

            var compilation = document.GetCompilation();
            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token) ||
                token.Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            string name = TokenLocator.Text(token);
            var tokens = compilation.Tokens;
            var locations = new List<Location>();
            foreach (int i in IdentifierScanner.FindOccurrences(tokens, name))
            {
                if (!includeDeclaration && IdentifierScanner.IsDeclaration(tokens, i)) continue;
                locations.Add(new Location(doc.RawUri, doc.RangeOf(tokens[i].PositionStart.Idx, tokens[i].PositionEnd.Idx)));
            }
            // Unresolved-locally → likely a top-level/imported name; aggregate cross-file.
            if (Workspace != null)
                foreach (var (file, start, end) in Workspace.FindOccurrences(name, doc.FileName))
                    locations.Add(new Location(file.Uri, file.RangeOf(start, end)));
            return locations.Count > 0 ? locations.ToArray() : null;
        }
    }

    /// <summary>Document highlights — read/write occurrences, semantic-first.</summary>
    public sealed class DocumentHighlightService : IDocumentHighlightService
    {
        public DocumentHighlight[]? Compute(RaDocument document, Position position)
        {
            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            var bound = document.GetSemanticModel().SymbolAt(offset);
            if (bound != null)
            {
                var hs = new List<DocumentHighlight>(bound.References.Count + 1)
                {
                    new() { Range = doc.RangeOf(bound.NameStart, bound.NameEnd), Kind = DocumentHighlightKind.Write },
                };
                foreach (var r in bound.References)
                    hs.Add(new DocumentHighlight { Range = doc.RangeOf(r.Start, r.End), Kind = r.IsWrite ? DocumentHighlightKind.Write : DocumentHighlightKind.Read });
                return hs.ToArray();
            }

            var compilation = document.GetCompilation();
            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token) ||
                token.Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            string name = TokenLocator.Text(token);
            var tokens = compilation.Tokens;
            var highlights = new List<DocumentHighlight>();
            foreach (int i in IdentifierScanner.FindOccurrences(tokens, name))
            {
                highlights.Add(new DocumentHighlight
                {
                    Range = doc.RangeOf(tokens[i].PositionStart.Idx, tokens[i].PositionEnd.Idx),
                    Kind = IdentifierScanner.IsWrite(tokens, i) ? DocumentHighlightKind.Write : DocumentHighlightKind.Read,
                });
            }
            return highlights.Count > 0 ? highlights.ToArray() : null;
        }
    }

    /// <summary>
    /// Rename — semantic-first (precise, shadow-correct), name-based fallback. Returns
    /// null on an invalid new name so the dispatcher surfaces a clean error.
    /// </summary>
    public sealed class RenameService : IRenameService
    {
        public PrepareRenameResult? Prepare(RaDocument document, Position position)
        {
            var compilation = document.GetCompilation();
            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token) ||
                token.Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            return new PrepareRenameResult
            {
                Range = doc.RangeOf(token.PositionStart.Idx, token.PositionEnd.Idx),
                Placeholder = TokenLocator.Text(token),
            };
        }

        public WorkspaceEdit? Rename(RaDocument document, Position position, string newName)
        {
            if (!IdentifierScanner.IsValidIdentifier(newName)) return null;

            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            var bound = document.GetSemanticModel().SymbolAt(offset);
            if (bound != null)
            {
                var edits = new List<TextEdit>(bound.References.Count + 1)
                {
                    new(doc.RangeOf(bound.NameStart, bound.NameEnd), newName),
                };
                foreach (var r in bound.References)
                    edits.Add(new TextEdit(doc.RangeOf(r.Start, r.End), newName));
                return new WorkspaceEdit { Changes = new Dictionary<string, TextEdit[]> { [doc.RawUri] = edits.ToArray() } };
            }

            var compilation = document.GetCompilation();
            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token) ||
                token.Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            string name = TokenLocator.Text(token);
            var tokens = compilation.Tokens;
            var occurrences = IdentifierScanner.FindOccurrences(tokens, name);
            if (occurrences.Count == 0) return null;

            var nameEdits = new TextEdit[occurrences.Count];
            for (int i = 0; i < occurrences.Count; i++)
            {
                var t = tokens[occurrences[i]];
                nameEdits[i] = new TextEdit(doc.RangeOf(t.PositionStart.Idx, t.PositionEnd.Idx), newName);
            }
            return new WorkspaceEdit { Changes = new Dictionary<string, TextEdit[]> { [doc.RawUri] = nameEdits } };
        }
    }
}

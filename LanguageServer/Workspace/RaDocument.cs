using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Features;
using RaLanguage.LanguageServer.Protocol;

namespace RaLanguage.LanguageServer.Workspace
{
    /// <summary>
    /// A managed document: its live text plus a lazily-computed, version-keyed
    /// <see cref="RaCompilation"/>. The compilation is recomputed only when the text
    /// version changes, so repeated feature requests on an unedited buffer reuse a
    /// single front-end pass. All access is serialized so a debounced diagnostics
    /// pass and an interactive request never compile the same buffer concurrently.
    /// </summary>
    public sealed class RaDocument
    {
        private readonly object _gate = new();
        private RaCompilation? _cached;
        private int _cachedVersion = int.MinValue;
        private SemanticModel? _model;
        private int _modelVersion = int.MinValue;
        private IReadOnlyList<Token>? _tokens;
        private int _tokensVersion = int.MinValue;

        public TextDocument Document { get; }

        public RaDocument(TextDocument document)
        {
            Document = document;
        }

        public int Version => Document.Version;

        public void ApplyChanges(IReadOnlyList<TextDocumentContentChangeEvent> changes, int newVersion)
        {
            lock (_gate)
            {
                Document.ApplyChanges(changes, newVersion);
                // Cache is invalidated implicitly: GetCompilation sees the new version.
            }
        }

        /// <summary>Front-end analysis of the current text (cached per version).</summary>
        public RaCompilation GetCompilation()
        {
            lock (_gate)
            {
                if (_cached != null && _cachedVersion == Document.Version)
                {
                    return _cached;
                }

                _cached = ToolingCompiler.Compile(Document.FileName, Document.Text);
                _cachedVersion = Document.Version;
                return _cached;
            }
        }

        /// <summary>
        /// Lexer output only (no parse), cached per version. Used by latency-sensitive
        /// token features (semantic highlighting) so they don't pay for a full parse on
        /// every keystroke.
        /// </summary>
        public IReadOnlyList<Token> GetTokens()
        {
            lock (_gate)
            {
                if (_tokens != null && _tokensVersion == Document.Version) return _tokens;
                try
                {
                    var lexer = new RaLanguage.Lexer.Lexer(Document.FileName, Document.Text);
                    var (toks, _) = lexer.MakeTokens();
                    _tokens = toks;
                }
                catch
                {
                    _tokens = System.Array.Empty<Token>();
                }
                _tokensVersion = Document.Version;
                return _tokens;
            }
        }

        /// <summary>The last computed compilation without forcing a fresh parse (may be stale or null).</summary>
        public RaCompilation? TryGetCachedCompilation()
        {
            lock (_gate) { return _cached; }
        }

        /// <summary>Scope-aware semantic model of the current text (cached per version).</summary>
        public SemanticModel GetSemanticModel()
        {
            lock (_gate)
            {
                if (_model != null && _modelVersion == Document.Version)
                {
                    return _model;
                }

                try
                {
                    _model = SemanticBinder.Build(GetCompilation().Ast);
                }
                catch
                {
                    _model = SemanticBinder.Build(null); // empty model; features fall back to name-based
                }
                _modelVersion = Document.Version;
                return _model;
            }
        }
    }
}

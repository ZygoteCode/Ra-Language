using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.LanguageServer.Compilation
{
    /// <summary>
    /// Immutable result of running the Ra front-end (lexer + parser + warning-only
    /// static analysis) over one text snapshot. The VM, IR compiler and interpreter
    /// are deliberately never touched — tooling only needs the syntax tree, token
    /// stream and diagnostics. Any of <see cref="Ast"/> may be null when parsing of
    /// broken/partial input fails; <see cref="Tokens"/> is still populated so the
    /// token-driven features keep working.
    /// </summary>
    public sealed class RaCompilation
    {
        public string FileName { get; }
        public string Text { get; }
        public IReadOnlyList<Token> Tokens { get; }
        public AstNode? Ast { get; }
        public IReadOnlyList<ToolingDiagnostic> Diagnostics { get; }

        public RaCompilation(
            string fileName,
            string text,
            IReadOnlyList<Token> tokens,
            AstNode? ast,
            IReadOnlyList<ToolingDiagnostic> diagnostics)
        {
            FileName = fileName;
            Text = text;
            Tokens = tokens;
            Ast = ast;
            Diagnostics = diagnostics;
        }
    }
}

using System;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Special;
using ErrSeverity = RaLanguage.Errors.DiagnosticSeverity;
using LspSeverity = RaLanguage.LanguageServer.Protocol.DiagnosticSeverity;

namespace RaLanguage.LanguageServer.Compilation
{
    /// <summary>
    /// Drives the Ra front-end for tooling. This is the firewall that keeps the VM
    /// out of the LSP path: it runs only the lexer, the parser and the warning-only
    /// <see cref="StaticAnalyzer"/>. Every phase is wrapped so that a crash on
    /// half-typed input degrades gracefully (partial tokens/AST + whatever
    /// diagnostics were collected) instead of taking the server down.
    /// </summary>
    public static class ToolingCompiler
    {
        public static RaCompilation Compile(string fileName, string text)
        {
            var diagnostics = new List<ToolingDiagnostic>();
            IReadOnlyList<Token> tokens = Array.Empty<Token>();
            AstNode? ast = null;

            try
            {
                var lexer = new RaLanguage.Lexer.Lexer(fileName, text);
                var (lexed, lexDiagnostics) = lexer.MakeTokens();
                tokens = lexed;
                Append(lexDiagnostics, diagnostics);

                // Parse even when the lexer reported errors — tolerant tooling wants
                // the best-effort tree. The parser owns its own error reporting; we
                // still guard against a hard throw on malformed token streams.
                try
                {
                    var parser = new RaLanguage.Parser.Parser(lexed);
                    var parseResult = parser.Parse();
                    ast = parseResult.Node;
                    Append(parseResult.Diagnostics, diagnostics);

                    // Error recovery: if the whole-file parse failed, try to rebuild a
                    // richer top-level scope by parsing each declaration in isolation.
                    // Only adopt it when it recovers strictly more structure, so it can
                    // never make the outline worse than the core parser produced.
                    if (parseResult.Diagnostics.HasErrors)
                    {
                        var recovered = RecoveryParser.TryRecover(lexed, out var segmentDiagnostics);
                        // Surface every segment's errors (deduped), so one broken
                        // declaration no longer hides errors in the others.
                        AppendDedup(segmentDiagnostics, diagnostics);
                        if (recovered != null)
                        {
                            int mainCount = (ast as ScopeNode)?.Nodes.Count ?? (ast != null ? 1 : 0);
                            if (recovered.Nodes.Count > mainCount) ast = recovered;
                        }
                    }

                    if (ast != null)
                    {
                        RunStaticAnalysis(ast, diagnostics);
                    }
                }
                catch
                {
                    // Parser could not recover; tokens + lexer diagnostics remain usable.
                }
            }
            catch
            {
                // Lexer failed catastrophically; return whatever we have.
            }

            return new RaCompilation(fileName, text, tokens, ast, diagnostics);
        }

        private static void RunStaticAnalysis(AstNode ast, List<ToolingDiagnostic> sink)
        {
            try
            {
                // Warning-only pass. A child of the builtin table lets Result/Option
                // and the registered builtins resolve; user symbols stay unresolved,
                // which keeps the analyzer conservative (no false positives).
                var symbols = new SymbolTable(RaLanguage.Program.BuiltinSymbolTable);
                List<StaticAnalyzerDiagnostic> results = StaticAnalyzer.Analyze(ast, symbols);
                for (int i = 0; i < results.Count; i++)
                {
                    var d = results[i];
                    sink.Add(new ToolingDiagnostic(
                        d.PositionStart.Idx,
                        d.PositionEnd.Idx,
                        LspSeverity.Warning,
                        d.Message,
                        code: "RA0301"));
                }
            }
            catch
            {
                // Never let static analysis break the editing experience.
            }
        }

        private static void AppendDedup(List<Diagnostic> raw, List<ToolingDiagnostic> sink)
        {
            if (raw == null || raw.Count == 0) return;
            var seen = new HashSet<(int, int, string)>();
            foreach (var d in sink) seen.Add((d.StartOffset, d.EndOffset, d.Message));
            for (int i = 0; i < raw.Count; i++)
            {
                var t = Convert(raw[i]);
                if (seen.Add((t.StartOffset, t.EndOffset, t.Message))) sink.Add(t);
            }
        }

        private static void Append(DiagnosticBag bag, List<ToolingDiagnostic> sink)
        {
            if (bag == null) return;
            var items = bag.Diagnostics;
            for (int i = 0; i < items.Count; i++)
            {
                sink.Add(Convert(items[i]));
            }
        }

        private static ToolingDiagnostic Convert(Diagnostic d)
        {
            int start = 0, end = 0;
            if (d.PrimarySpan.IsValid)
            {
                start = d.PrimarySpan.Start.Idx;
                end = d.PrimarySpan.End.Idx;
            }

            string message = !string.IsNullOrEmpty(d.Title)
                ? d.Title
                : (d.Message ?? string.Empty);

            string? code = d.Code.IsEmpty ? null : d.Code.Id;

            return new ToolingDiagnostic(start, end, MapSeverity(d.Severity), message, code);
        }

        private static LspSeverity MapSeverity(ErrSeverity severity) => severity switch
        {
            ErrSeverity.Error => LspSeverity.Error,
            ErrSeverity.Warning => LspSeverity.Warning,
            ErrSeverity.Info => LspSeverity.Information,
            ErrSeverity.Note => LspSeverity.Information,
            ErrSeverity.Help => LspSeverity.Hint,
            _ => LspSeverity.Information,
        };
    }
}

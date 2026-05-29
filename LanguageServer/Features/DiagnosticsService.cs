using System;
using System.Collections.Generic;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Projects diagnostics onto LSP ranges: parser/lexer/recovery diagnostics from the
    /// compilation, plus the binder/type static analysis (undefined symbols, unknown
    /// types). All independent findings are emitted together so the editor shows every
    /// error at once rather than one-at-a-time. Zero-width spans widen to one char.
    /// </summary>
    public sealed class DiagnosticsService : IDiagnosticsService
    {
        /// <summary>Set at initialize; enables cross-file (imported) symbol/type resolution.</summary>
        public WorkspaceIndex? Workspace { get; set; }

        public PublishDiagnosticsParams Compute(RaDocument document)
        {
            var compilation = document.GetCompilation();
            var doc = document.Document;
            int length = doc.Text.Length;

            // Parse/lexer/recovery diagnostics + static (binder/type) diagnostics.
            var all = new List<ToolingDiagnostic>(compilation.Diagnostics);
            try
            {
                var model = document.GetSemanticModel();
                var index = SymbolIndex.Build(compilation.Ast);

                var imported = new HashSet<string>(StringComparer.Ordinal);
                var arity = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
                var aliasExports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var importedExports = new List<RaSymbol>();

                foreach (var s in index.Flat) AddArity(arity, s);

                if (Workspace != null)
                {
                    foreach (var path in Workspace.ResolveImports(doc.FileName, compilation.Ast))
                        foreach (var s in Workspace.ExportsOf(path))
                        {
                            imported.Add(s.Name);
                            AddArity(arity, s);
                            importedExports.Add(s);
                        }
                    BuildAliasExports(aliasExports, compilation.Ast, doc.FileName);
                }

                var typeTable = new TypeTable(index, importedExports, Workspace);
                var varEnv = VarEnv.Build(compilation.Ast, typeTable);
                var statics = StaticDiagnostics.Analyze(
                    compilation.Ast, compilation.Tokens, model, BuiltinNames(), imported, arity, aliasExports,
                    typeTable, varEnv);
                Merge(all, statics);

                // Conservative use-after-move (affine `let`) checking.
                var moves = new List<ToolingDiagnostic>();
                MoveAnalyzer.Analyze(compilation.Ast, varEnv, typeTable, moves);
                Merge(all, moves);

                // Duplicate-definition detection (vars same-scope; types vs scope/builtin/import).
                var redecls = new List<ToolingDiagnostic>();
                RedeclarationAnalyzer.Analyze(compilation.Ast, BuiltinNames(), imported, redecls);
                Merge(all, redecls);
            }
            catch
            {
                // Static analysis must never break the editing experience.
            }

            var result = new LspDiagnostic[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                int start = Clamp(d.StartOffset, 0, length);
                int end = Clamp(d.EndOffset, start, length);
                if (end == start) end = Math.Min(start + 1, length);
                result[i] = new LspDiagnostic
                {
                    Range = doc.RangeOf(start, end),
                    Severity = d.Severity,
                    Code = d.Code,
                    Source = d.Source,
                    Message = d.Message,
                };
            }

            return new PublishDiagnosticsParams
            {
                Uri = doc.RawUri,
                Version = doc.Version,
                Diagnostics = result,
            };
        }

        private static void AddArity(Dictionary<string, List<(int, int)>> map, RaSymbol s)
        {
            if (!s.IsCallable) return;
            if (!map.TryGetValue(s.Name, out var list)) { list = new List<(int, int)>(); map[s.Name] = list; }
            list.Add((s.MinArgs, s.MaxArgs));
        }

        private void BuildAliasExports(Dictionary<string, HashSet<string>> map, AstNode? ast, string currentFile)
        {
            if (Workspace == null || ast is not ScopeNode scope) return;
            foreach (var node in scope.Nodes)
            {
                if (node is not ImportAliasNode al) continue;
                var path = Workspace.ResolveModulePath(al.Specifier, currentFile);
                if (path == null) continue;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in Workspace.ExportsOf(path)) set.Add(s.Name);
                map[al.Alias] = set;
            }
        }

        // Merge with dedup by (start, end, message) so overlapping passes don't double up.
        private static void Merge(List<ToolingDiagnostic> into, List<ToolingDiagnostic> extra)
        {
            var seen = new HashSet<(int, int, string)>();
            foreach (var d in into) seen.Add((d.StartOffset, d.EndOffset, d.Message));
            foreach (var d in extra)
                if (seen.Add((d.StartOffset, d.EndOffset, d.Message))) into.Add(d);
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        // Builtin value names (print, channel, Result, Option, …) — stable after init; cached.
        private static HashSet<string>? s_builtins;
        private static HashSet<string> BuiltinNames()
        {
            var cached = s_builtins;
            if (cached != null) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var name in RaLanguage.Program.BuiltinSymbolTable.GetLocalKeys()) set.Add(name);
            }
            catch
            {
                // ignore; empty set just means fewer allowed names
            }
            s_builtins = set;
            return set;
        }
    }
}

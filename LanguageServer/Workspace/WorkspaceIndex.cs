using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Modules;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Features;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.LanguageServer.Workspace
{
    /// <summary>One indexed source file: its compilation, structural symbols and a line index for range mapping.</summary>
    public sealed class IndexedFile
    {
        public string Path { get; }
        public string Uri { get; }
        public RaCompilation Compilation { get; }
        public SymbolIndex Symbols { get; }
        public LineIndex Lines { get; }

        public IndexedFile(string path, RaCompilation compilation, SymbolIndex symbols, LineIndex lines)
        {
            Path = path;
            Uri = UriUtil.FromFileSystemPath(path);
            Compilation = compilation;
            Symbols = symbols;
            Lines = lines;
        }

        public Protocol.Range RangeOf(int startOffset, int endOffset)
        {
            var (sl, sc) = Lines.OffsetToPosition(startOffset);
            var (el, ec) = Lines.OffsetToPosition(endOffset < startOffset ? startOffset : endOffset);
            return new Protocol.Range(new Position(sl, sc), new Position(el, ec));
        }
    }

    /// <summary>
    /// Cross-file index over the workspace's <c>.ra</c> files, built with the front-end
    /// only (lexer + parser + structural symbols) — the VM is never involved. Powers
    /// workspace symbols and cross-file definition/references. Files are indexed on a
    /// background thread at startup and re-indexed on edits; module imports are resolved
    /// through the same <see cref="ModuleResolver"/> the interpreter uses.
    /// </summary>
    public sealed class WorkspaceIndex
    {
        private const int MaxFiles = 3000;
        private const int MaxBytes = 2_000_000;

        private readonly ConcurrentDictionary<string, IndexedFile> _files =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ModuleResolver _resolver;
        private readonly string _root;
        private readonly PositionEncodingKind _encoding;
        private readonly Transport.LspLogger _log;
        private volatile bool _capped;

        public WorkspaceIndex(string root, string stdRoot, PositionEncodingKind encoding, Transport.LspLogger log)
        {
            _root = root;
            _encoding = encoding;
            _log = log;
            _resolver = new ModuleResolver(root, stdRoot);
        }

        public int Count => _files.Count;

        public void StartBackgroundIndex()
        {
            _ = Task.Run(IndexAll);
        }

        private void IndexAll()
        {
            try
            {
                int n = 0;
                foreach (string path in EnumerateRaFiles(_root))
                {
                    if (n >= MaxFiles) { _capped = true; break; }
                    IndexPath(path, null);
                    n++;
                }
                _log.Info($"workspace indexed: {_files.Count} file(s){(_capped ? $" (capped at {MaxFiles})" : "")}.");
            }
            catch (Exception ex)
            {
                _log.Exception("workspace index", ex);
            }
        }

        /// <summary>(Re)index a file. Pass <paramref name="openText"/> for an open buffer; null reads disk.</summary>
        public void Reindex(string path, string? openText)
        {
            if (string.IsNullOrEmpty(path)) return;
            IndexPath(path, openText);
        }

        public void Remove(string path)
        {
            if (!string.IsNullOrEmpty(path)) _files.TryRemove(Key(path), out _);
        }

        private IndexedFile? IndexPath(string path, string? openText)
        {
            try
            {
                string? text = openText ?? ReadFile(path);
                if (text == null || text.Length > MaxBytes) return null;
                var compilation = ToolingCompiler.Compile(path, text);
                var symbols = SymbolIndex.Build(compilation.Ast);
                var lines = new LineIndex(text, _encoding);
                var file = new IndexedFile(path, compilation, symbols, lines);
                _files[Key(path)] = file;
                return file;
            }
            catch
            {
                return null;
            }
        }

        // ---- queries ----

        /// <summary>All declarations named <paramref name="name"/> across indexed files.</summary>
        public List<(IndexedFile File, RaSymbol Symbol)> FindByName(string name)
        {
            var result = new List<(IndexedFile, RaSymbol)>();
            foreach (var file in _files.Values)
                foreach (var sym in file.Symbols.Flat)
                    if (sym.Name == name) result.Add((file, sym));
            return result;
        }

        /// <summary>Imported top-level <b>type</b> names (class/struct/enum/interface) for highlighting.</summary>
        public HashSet<string> ImportedTypeNames(string currentFilePath, Parser.Nodes.AstNode? ast)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in ResolveImports(currentFilePath, ast))
            {
                if (!_files.TryGetValue(Key(path), out var file)) continue;
                foreach (var sym in file.Symbols.TopLevel)
                    if (sym.Kind is Protocol.SymbolKind.Class or Protocol.SymbolKind.Struct
                        or Protocol.SymbolKind.Enum or Protocol.SymbolKind.Interface)
                        set.Add(sym.Name);
            }
            return set;
        }

        /// <summary>Resolve a single module specifier to an absolute path (front-end only), or null.</summary>
        public string? ResolveModulePath(ModuleSpecifier spec, string currentFile)
        {
            try
            {
                var r = _resolver.Resolve(spec, currentFile);
                return r.Ok ? r.AbsolutePath : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Resolve the files a document imports (front-end module resolution).</summary>
        public List<string> ResolveImports(string currentFilePath, Parser.Nodes.AstNode? ast)
        {
            var paths = new List<string>();
            if (ast is not ScopeNode scope) return paths;

            foreach (var node in scope.Nodes)
            {
                ModuleSpecifier? spec = node switch
                {
                    ImportAllNode a => a.Specifier,
                    ImportSelectiveNode s => s.Specifier,
                    ImportAliasNode al => al.Specifier,
                    _ => null,
                };
                if (spec == null) continue;

                try
                {
                    var resolved = _resolver.Resolve(spec, currentFilePath);
                    if (resolved.Ok && resolved.AbsolutePath != null)
                    {
                        paths.Add(resolved.AbsolutePath);
                        if (!_files.ContainsKey(Key(resolved.AbsolutePath)))
                            IndexPath(resolved.AbsolutePath, null); // pull imported module in on demand
                    }
                }
                catch
                {
                    // ignore unresolved imports
                }
            }
            return paths;
        }

        /// <summary>Top-level exported symbols of an indexed file (empty if not indexed).</summary>
        public IReadOnlyList<Features.RaSymbol> ExportsOf(string path)
        {
            if (path != null && _files.TryGetValue(Key(path), out var file)) return file.Symbols.TopLevel;
            return System.Array.Empty<Features.RaSymbol>();
        }

        /// <summary>Top-level declarations named <paramref name="name"/> across the workspace (types/extensions; for base-chain resolution).</summary>
        public List<Features.RaSymbol> FindTypes(string name)
        {
            var result = new List<Features.RaSymbol>();
            foreach (var file in _files.Values)
                foreach (var sym in file.Symbols.TopLevel)
                    if (sym.Name == name) result.Add(sym);
            return result;
        }

        /// <summary>Members of every top-level type/extension named <paramref name="typeName"/> (aggregated).</summary>
        public List<Features.RaSymbol> FindTypeMembers(string typeName)
        {
            var result = new List<Features.RaSymbol>();
            foreach (var file in _files.Values)
                foreach (var sym in file.Symbols.TopLevel)
                    if (sym.Name == typeName && sym.Children.Count > 0)
                        result.AddRange(sym.Children);
            return result;
        }

        /// <summary>Top-level exported names from the modules <paramref name="currentFilePath"/> imports.</summary>
        public HashSet<string> ImportedNames(string currentFilePath, Parser.Nodes.AstNode? ast)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in ResolveImports(currentFilePath, ast))
            {
                if (_files.TryGetValue(Key(path), out var file))
                    foreach (var sym in file.Symbols.TopLevel)
                        set.Add(sym.Name);
            }
            return set;
        }

        /// <summary>Name occurrences across all indexed files except <paramref name="excludePath"/>.</summary>
        public List<(IndexedFile File, int Start, int End)> FindOccurrences(string name, string? excludePath)
        {
            var result = new List<(IndexedFile, int, int)>();
            string? exclude = excludePath == null ? null : Key(excludePath);
            foreach (var file in _files.Values)
            {
                if (exclude != null && Key(file.Path) == exclude) continue;
                var tokens = file.Compilation.Tokens;
                foreach (int i in IdentifierScanner.FindOccurrences(tokens, name))
                    result.Add((file, tokens[i].PositionStart.Idx, tokens[i].PositionEnd.Idx));
            }
            return result;
        }

        /// <summary>Fuzzy subsequence search over top-level declaration names.</summary>
        public List<(IndexedFile File, RaSymbol Symbol, int Score)> FuzzySearch(string query, int limit)
        {
            var hits = new List<(IndexedFile, RaSymbol, int)>();
            bool empty = string.IsNullOrEmpty(query);
            foreach (var file in _files.Values)
            {
                foreach (var sym in file.Symbols.Flat)
                {
                    int score = empty ? 0 : FuzzyScore(sym.Name, query);
                    if (empty || score > int.MinValue) hits.Add((file, sym, score));
                }
            }
            hits.Sort(static (a, b) => b.Item3.CompareTo(a.Item3));
            if (hits.Count > limit) hits.RemoveRange(limit, hits.Count - limit);
            return hits;
        }

        // ---- helpers ----

        private static int FuzzyScore(string candidate, string query)
        {
            // Case-insensitive subsequence match; reward contiguous + prefix hits.
            int ci = 0, qi = 0, score = 0, streak = 0;
            while (ci < candidate.Length && qi < query.Length)
            {
                if (char.ToLowerInvariant(candidate[ci]) == char.ToLowerInvariant(query[qi]))
                {
                    qi++;
                    streak++;
                    score += 1 + streak + (ci == 0 ? 5 : 0);
                }
                else
                {
                    streak = 0;
                }
                ci++;
            }
            return qi == query.Length ? score : int.MinValue;
        }

        private static IEnumerable<string> EnumerateRaFiles(string root)
        {
            IEnumerator<string> e;
            try
            {
                e = Directory.EnumerateFiles(root, "*.ra", SearchOption.AllDirectories).GetEnumerator();
            }
            catch
            {
                yield break;
            }
            using (e)
            {
                while (true)
                {
                    string current;
                    try { if (!e.MoveNext()) break; current = e.Current; }
                    catch { continue; }
                    if (IsNoise(current)) continue;
                    yield return current;
                }
            }
        }

        private static bool IsNoise(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/.git/") || p.Contains("/node_modules/");
        }

        private static string? ReadFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static string Key(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}

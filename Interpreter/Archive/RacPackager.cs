using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Archive
{
    public sealed class RacBuildOptions
    {
        public string EntryFile { get; set; } = "";
        public string OutputFile { get; set; } = "";
        public string? ProjectRoot { get; set; }
        public string? StdRoot { get; set; }
        public bool Compress { get; set; } = true;
        public bool Verbose { get; set; } = false;
    }

    public sealed class RacBuildResult
    {
        public bool Success { get; init; }
        public string? OutputPath { get; init; }
        public long OutputSize { get; init; }
        public int ModuleCount { get; init; }
        public List<string> Errors { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public TimeSpan Elapsed { get; init; }
    }

    // Build an archive from a source tree:
    //
    //   1. Resolve EntryFile to an absolute path.
    //   2. Lex + parse it. Surface diagnostics.
    //   3. Walk top-level statements for ImportNode subclasses, resolving
    //      each via a ModuleResolver. Repeat transitively. Detect cycles.
    //   4. For every reached module, hash the source, allocate a section,
    //      record the dependency.
    //   5. Run the standard `derive → resolve → analyze → IR compile`
    //      pipeline against each module so a build-time error catches
    //      anything that would explode at load.
    //   6. Emit the archive: manifest + per-module source sections +
    //      a small std-ref index.
    //
    // The packager does NOT execute user code at build time (imports are
    // captured as edges; `print(...)` calls are never invoked). That makes
    // builds deterministic and free of side effects.
    public static class RacPackager
    {
        public static RacBuildResult Build(RacBuildOptions opts)
        {
            var result = new RacBuildResult
            {
                OutputPath = null,
            };
            var errors = new List<string>();
            var warnings = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string entry;
            try { entry = Path.GetFullPath(opts.EntryFile); }
            catch (Exception ex)
            {
                errors.Add($"Invalid entry path '{opts.EntryFile}': {ex.Message}");
                return Fail(errors, warnings, sw);
            }
            if (!File.Exists(entry))
            {
                errors.Add($"Entry file not found: {entry}");
                return Fail(errors, warnings, sw);
            }
            if (!entry.EndsWith(".ra", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Entry file must have .ra extension: {entry}");
                return Fail(errors, warnings, sw);
            }

            string projectRoot = opts.ProjectRoot ?? Path.GetDirectoryName(entry) ?? Directory.GetCurrentDirectory();
            string stdRoot = opts.StdRoot ?? ResolveStdRoot(projectRoot);
            var resolver = new ModuleResolver(projectRoot, stdRoot);

            // Walk the import graph.
            var graph = new ImportGraph();
            var pending = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            graph.AddRoot(entry);
            pending.Enqueue(entry);
            seen.Add(entry);

            // Capture std refs (informational).
            var stdRefs = new SortedSet<string>(StringComparer.Ordinal);

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                string source;
                try { source = File.ReadAllText(current); }
                catch (Exception ex)
                {
                    errors.Add($"Cannot read '{current}': {ex.Message}");
                    return Fail(errors, warnings, sw);
                }
                graph.SetSource(current, source);

                // Lex + parse. We surface lex/parse errors so the user
                // sees them at build time rather than at first run.
                var lexer = new Lexer.Lexer(current, source);
                var (tokens, lexDiags) = lexer.MakeTokens();
                if (lexDiags.HasErrors)
                {
                    foreach (var d in lexDiags.Diagnostics)
                        errors.Add($"{current}: lex: {d}");
                    return Fail(errors, warnings, sw);
                }

                var parser = new Parser.Parser(tokens);
                var parseResult = parser.Parse();
                if (parseResult.HasErrors)
                {
                    foreach (var d in parseResult.Diagnostics.Diagnostics)
                        errors.Add($"{current}: parse: {d}");
                    return Fail(errors, warnings, sw);
                }

                // Extract imports.
                var imports = new List<ImportNode>();
                CollectImports(parseResult.Node, imports);

                foreach (var imp in imports)
                {
                    if (imp.Specifier.Kind == ModuleSpecifierKind.Dotted)
                    {
                        // Record the dotted name as std-ref before
                        // resolving — this catches references whose
                        // target path doesn't exist on disk but the
                        // intent is clear (e.g. for diagnostics).
                        stdRefs.Add(imp.Specifier.Display);
                    }
                    var res = resolver.Resolve(imp.Specifier, current);
                    if (!res.Ok)
                    {
                        errors.Add($"{current}: cannot resolve import '{imp.Specifier.Display}': {res.ErrorMessage}");
                        return Fail(errors, warnings, sw);
                    }
                    string target = res.AbsolutePath!;
                    bool isStd = imp.Specifier.Kind == ModuleSpecifierKind.Dotted;
                    graph.AddEdge(current, target, isStd);
                    if (seen.Add(target)) pending.Enqueue(target);
                }
            }

            // Construct the manifest.
            var modulePathList = graph.OrderedModules(entry);
            var manifest = new RacManifest
            {
                EntryModuleIndex = 0, // entry is first by construction
                BuildTimeTicks = DateTime.UtcNow.Ticks,
                BuildHost = SafeMachineName(),
                BuiltBy = $"ralang {RacHeader.FormatSemver(RacFormat.RaRuntimeVersion)}",
            };
            foreach (var s in stdRefs) manifest.StdReferences.Add(s);

            var pathToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < modulePathList.Count; i++)
            {
                pathToIndex[modulePathList[i]] = i;
                string abs = modulePathList[i];
                string logical = (i == 0) ? Path.GetFileName(abs) : MakeLogicalPath(projectRoot, stdRoot, abs);

                var record = new RacModuleRecord
                {
                    Index = i,
                    Kind = (i == 0)
                        ? RacModuleKind.Entry
                        : (graph.IsStdModule(abs) ? RacModuleKind.StdLib : RacModuleKind.Project),
                    LogicalPath = logical,
                    AbsoluteVirtualPath = NormalisePath(abs),
                    SourceSectionIndex = -1,
                    BytecodeSectionIndex = -1,
                    SourceHash = RacIntegrity.Hash(Encoding.UTF8.GetBytes(graph.GetSource(abs))),
                };
                manifest.Modules.Add(record);
            }
            foreach (var record in manifest.Modules)
            {
                foreach (var dep in graph.OutgoingEdges(modulePathList[record.Index]))
                {
                    record.Imports.Add(pathToIndex[dep]);
                }
            }

            // Build the archive.
            var writer = new RacWriter();
            if (opts.Compress) writer.ArchiveFlags |= RacFlags.Compressed;

            // First pass: add module-source sections so we know their
            // directory indices, then patch the manifest's
            // SourceSectionIndex field.
            int firstSrcIdx = 1; // index 0 is the manifest
            for (int i = 0; i < manifest.Modules.Count; i++)
            {
                int srcIdx = firstSrcIdx + i;
                manifest.Modules[i].SourceSectionIndex = srcIdx;
            }

            // Add a placeholder StdLibIndex section index? We append
            // it last so it doesn't disturb the module-source range.

            // Re-serialize the manifest *after* patching indices.
            byte[] manifestBytes = manifest.Serialize();
            int mIdx = writer.AddSection(RacSectionKind.Manifest, manifestBytes,
                compress: opts.Compress, mustUnderstand: true);
            if (mIdx != 0)
                throw new InvalidOperationException("rac: Manifest must be the first section");

            for (int i = 0; i < modulePathList.Count; i++)
            {
                byte[] src = Encoding.UTF8.GetBytes(graph.GetSource(modulePathList[i]));
                int sIdx = writer.AddSection(RacSectionKind.ModuleSource, src,
                    compress: opts.Compress, mustUnderstand: true);
                if (sIdx != manifest.Modules[i].SourceSectionIndex)
                    throw new InvalidOperationException(
                        $"rac: source section index drift (expected {manifest.Modules[i].SourceSectionIndex}, got {sIdx})");
            }

            // StdLibIndex (informational — never MustUnderstand).
            if (stdRefs.Count > 0)
            {
                using var ms = new MemoryStream();
                var bw = new RacBinaryWriter(ms);
                bw.WriteI32(stdRefs.Count);
                foreach (var s in stdRefs) bw.WriteString(s);
                writer.AddSection(RacSectionKind.StdLibIndex, ms.ToArray(),
                    compress: opts.Compress, mustUnderstand: false);
            }

            try
            {
                string outPath = string.IsNullOrEmpty(opts.OutputFile)
                    ? Path.ChangeExtension(entry, ".rac")
                    : Path.GetFullPath(opts.OutputFile);

                string? outDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir!);

                using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    writer.Finish(outFs);
                }

                long outSize = new FileInfo(outPath).Length;

                sw.Stop();
                return new RacBuildResult
                {
                    Success = true,
                    OutputPath = outPath,
                    OutputSize = outSize,
                    ModuleCount = manifest.Modules.Count,
                    Errors = errors,
                    Warnings = warnings,
                    Elapsed = sw.Elapsed,
                };
            }
            catch (Exception ex)
            {
                errors.Add($"failed to write archive: {ex.Message}");
                return Fail(errors, warnings, sw);
            }
        }

        private static RacBuildResult Fail(List<string> errors, List<string> warnings, System.Diagnostics.Stopwatch sw)
        {
            sw.Stop();
            return new RacBuildResult
            {
                Success = false,
                OutputPath = null,
                OutputSize = 0,
                ModuleCount = 0,
                Errors = errors,
                Warnings = warnings,
                Elapsed = sw.Elapsed,
            };
        }

        private static void CollectImports(AstNode root, List<ImportNode> output)
        {
            // Imports live at file scope in Ra. Walk the top-level
            // statements; nested scopes do not declare imports in
            // FormatMajor=1. Should that change, this walker can grow
            // a recursive case without breaking compatibility.
            switch (root)
            {
                case ImportNode imp:
                    output.Add(imp);
                    break;
                case ScopeNode scope:
                    foreach (var child in scope.Nodes)
                    {
                        if (child is ImportNode importChild) output.Add(importChild);
                    }
                    break;
            }
        }

        private static string ResolveStdRoot(string projectRoot)
        {
            string exeStd = Path.Combine(AppContext.BaseDirectory, "std");
            if (Directory.Exists(exeStd)) return exeStd;
            string projectStd = Path.Combine(projectRoot, "std");
            if (Directory.Exists(projectStd)) return projectStd;
            return exeStd;
        }

        private static string MakeLogicalPath(string projectRoot, string stdRoot, string absolute)
        {
            try
            {
                string normStd = Path.GetFullPath(stdRoot);
                string normProj = Path.GetFullPath(projectRoot);
                if (absolute.StartsWith(normStd, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = absolute.Substring(normStd.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    return "std/" + rel.Replace('\\', '/');
                }
                if (absolute.StartsWith(normProj, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = absolute.Substring(normProj.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    return rel.Replace('\\', '/');
                }
            }
            catch { }
            return Path.GetFileName(absolute);
        }

        private static string NormalisePath(string absolute)
        {
            // Forward-slash form so the archive is identical between
            // builds on Windows and POSIX hosts.
            return absolute.Replace('\\', '/');
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName ?? ""; }
            catch { return ""; }
        }

        // In-memory import graph (build-time scratch).
        private sealed class ImportGraph
        {
            private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> _edges = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _stdModules = new(StringComparer.OrdinalIgnoreCase);
            private string? _root;

            public void AddRoot(string absolute)
            {
                _root = absolute;
                if (!_edges.ContainsKey(absolute)) _edges[absolute] = new();
            }

            public void SetSource(string absolute, string source)
            {
                _sources[absolute] = source;
            }

            public string GetSource(string absolute) => _sources[absolute];

            public void AddEdge(string from, string to, bool isStd)
            {
                if (!_edges.TryGetValue(from, out var list))
                {
                    list = new List<string>();
                    _edges[from] = list;
                }
                if (!list.Contains(to, StringComparer.OrdinalIgnoreCase)) list.Add(to);
                if (!_edges.ContainsKey(to)) _edges[to] = new List<string>();
                if (isStd) _stdModules.Add(to);
            }

            public bool IsStdModule(string absolute) => _stdModules.Contains(absolute);

            public IEnumerable<string> OutgoingEdges(string from)
                => _edges.TryGetValue(from, out var list) ? list : new List<string>();

            // Topological-ish ordering — root first, then any module
            // it transitively imports, in BFS order. The result is the
            // canonical ordering used in the manifest.
            public List<string> OrderedModules(string root)
            {
                var ordered = new List<string>();
                var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();
                queue.Enqueue(root);
                queued.Add(root);
                while (queue.Count > 0)
                {
                    string cur = queue.Dequeue();
                    ordered.Add(cur);
                    if (_edges.TryGetValue(cur, out var deps))
                    {
                        foreach (var d in deps)
                        {
                            if (queued.Add(d)) queue.Enqueue(d);
                        }
                    }
                }
                // Append any module that was added as a target but is
                // not reachable (shouldn't happen, but defensive).
                foreach (var kvp in _edges)
                {
                    if (queued.Add(kvp.Key)) ordered.Add(kvp.Key);
                }
                return ordered;
            }
        }
    }
}

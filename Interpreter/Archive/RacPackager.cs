using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Patterns;
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
        // v1.1 (#6): tree-shake the bundled standard library — drop std
        // top-level decls whose names never appear in any non-std module's
        // AST (transitive over each kept decl's private dependencies).
        // Default on; flip to false via `--no-tree-shake` for diagnostics
        // or for programs that resolve std helpers reflectively.
        public bool TreeShakeStd { get; set; } = true;
        // v1.1 (#7): build the archive-level SharedConstPool. Default
        // on. Set false via `--no-const-pool` for diagnostics or to
        // produce a v1-style archive (every const inlined) for size
        // comparison.
        public bool SharedConstPoolEnabled { get; set; } = true;

        // v1.2 (#sig): optional signing config. When SignKeyPath is
        // set, the packager loads the PEM-encoded private key, signs
        // the archive's canonical payload (file header identity bits
        // + every non-signature directory entry) and appends a
        // Signature section.
        public string? SignKeyPath { get; set; }
        public string SignerId { get; set; } = "";
        // Embedded ships the full public key alongside the signature
        // (verifier needs no external state). Fingerprint ships only
        // the SHA-256 fingerprint; the verifier resolves the matching
        // public key from a trust store.
        public RacSignatureKeyMode SignKeyMode { get; set; } = RacSignatureKeyMode.Embedded;

        // v2.0 (#zstd): pick the compression codec. Default Zstd; flip
        // to Deflate for diagnostic / reproducibility-with-old-tooling
        // runs. Zstd level 1..22 — 11 is a balanced default; 19+ for
        // smallest archive at much higher pack time.
        public RacCodecKind Codec { get; set; } = RacCodecKind.Zstd;
        public int ZstdLevel { get; set; } = 11;
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
                // Cache the parsed AST for the bytecode-payload pass below.
                graph.SetParsed(current, parseResult.Node);

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

                        // Virtual std modules/packages (std.prelude.*,
                        // std.sys.*, bare std) carry no source — the runtime
                        // synthesises them from the built-in store at load
                        // time. They are not files, so skip bundling; the
                        // resolver would (correctly) fail to find a path.
                        string dotted = string.Join(".", imp.Specifier.Segments!);
                        if (RaLanguage.Interpreter.Modules.StdLibrary.IsVirtualStdPath(dotted))
                            continue;
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

            // ---- v1.1 (#6) tree-shake std modules.
            //
            // Walks the parsed AST of every module to gather every
            // identifier-name reference, then for each std module drops
            // top-level pub/private decls whose names appear in nothing
            // the importer can reach. The packager replaces the std
            // module's source with the rewritten one (and re-hashes it
            // for the manifest's SourceHash field). Bytecode for shaken
            // std modules is skipped — the runtime re-lex/parses the
            // slimmed source on first import, which costs less than
            // shipping kilobytes of dead code does in the first place.
            StdLibTreeShaker.Result? shakeResult = null;
            if (opts.TreeShakeStd)
            {
                var stdPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sourcesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var astsByPath = new Dictionary<string, AstNode>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < modulePathList.Count; i++)
                {
                    var p = modulePathList[i];
                    sourcesByPath[p] = graph.GetSource(p);
                    var ast = graph.GetParsed(p);
                    if (ast != null) astsByPath[p] = ast;
                    if (graph.IsStdModule(p)) stdPaths.Add(p);
                }
                if (stdPaths.Count > 0)
                {
                    shakeResult = StdLibTreeShaker.Shake(modulePathList, sourcesByPath, astsByPath, stdPaths);
                    foreach (var kvp in shakeResult.RewrittenSources)
                    {
                        // Overwrite cached source so the manifest hash +
                        // ModuleSource section payload + bytecode all
                        // see the slimmed text. v1.2: re-parse the
                        // rewritten std module so its cached AST also
                        // matches the trimmed bytes — bytecode emission
                        // for std modules below depends on it.
                        graph.SetSource(kvp.Key, kvp.Value);
                        var reLex = new Lexer.Lexer(kvp.Key, kvp.Value);
                        var (reToks, reDiags) = reLex.MakeTokens();
                        if (reDiags.HasErrors)
                        {
                            foreach (var d in reDiags.Diagnostics)
                                errors.Add($"{kvp.Key}: lex (post-shake): {d}");
                            return Fail(errors, warnings, sw);
                        }
                        var reParser = new Parser.Parser(reToks);
                        var reParse = reParser.Parse();
                        if (reParse.HasErrors)
                        {
                            foreach (var d in reParse.Diagnostics.Diagnostics)
                                errors.Add($"{kvp.Key}: parse (post-shake): {d}");
                            return Fail(errors, warnings, sw);
                        }
                        graph.SetParsed(kvp.Key, reParse.Node);
                    }
                    if (opts.Verbose && shakeResult.Stats.DeclsDropped > 0)
                    {
                        warnings.Add(
                            $"tree-shake: {shakeResult.Stats.ModulesShaken}/{shakeResult.Stats.ModulesScanned} std modules slimmed, "
                            + $"{shakeResult.Stats.DeclsDropped} decls dropped, "
                            + $"{shakeResult.Stats.BytesBefore - shakeResult.Stats.BytesAfter:N0} bytes saved");
                    }
                }
            }

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
                    SourceHash = RacIntegrity.Hash(System.Text.Encoding.UTF8.GetBytes(graph.GetSource(abs))),
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

            // ---- Pre-compile each module's IR to a ModuleBytecode payload.
            //
            // v1.1: when the IR compile succeeds AND the AST serialiser
            // doesn't trip on an unsupported node, the loader can skip
            // lex/parse/IR-compile at runtime and feed the deserialised
            // RaFunction straight into VmExecutor. If compilation or
            // serialisation fails we drop the bytecode for that module
            // and fall back to the v1.0 source-only path — the runner
            // sees BytecodeSectionIndex == -1 and lex/parses the source
            // section instead.
            //
            // The compile itself runs the full Program.Run prefix
            // (DeriveTransformer → MatchSimplifier → Resolver →
            // IrCompiler.CompileScript) against a fresh global symbol
            // table so analysis passes that consult the symbol table
            // (e.g. annotation registration) see a clean slate per
            // module.
            //
            // Two-pass: first compile every eligible module's IR (cheap —
            // we cached the parsed AST during the import-graph walk).
            // Then observe every RaFunction.Consts into a build-time
            // SharedConstPoolBuilder; finalize; and serialize each
            // RaFunction with the resulting pool. Const slots whose
            // value appears in >= 2 modules emit a 5-byte pool ref
            // instead of an inline copy.
            var bytecodePayloads = new byte[modulePathList.Count][];
            var compiledFns = new RaFunction?[modulePathList.Count];
            int bytecodeCompiled = 0;
            int bytecodeFailed = 0;
            for (int i = 0; i < modulePathList.Count; i++)
            {
                var path = modulePathList[i];
                var ast = graph.GetParsed(path);
                if (ast == null) continue;
                // v1.2: compile *every* module — std included. The
                // runner now loads per-module bytecode for imports too,
                // so std modules need to ship their compiled IR or
                // ModuleManager would have to lex/parse them at load
                // time from the source section, defeating the boot-time
                // win the bytecode payload exists to deliver.
                try
                {
                    DeriveTransformer.Apply(ast);
                    MatchSimplifier.Apply(ast);
                    Resolver.Resolve(ast);
                    var scriptFn = IrCompiler.CompileScript(ast, path);
                    // v4 (#pre-compiled children): walk every nested
                    // function / struct method / trait method /
                    // operator referenced by this script's IR and
                    // pre-compile each one's body to IR. The
                    // serialiser persists `.CompiledBody` and the
                    // runtime's GetOrCompileBody / GetOrCompile*
                    // short-circuit on first dispatch — zero AST→IR
                    // work at load.
                    Runtime.RaFunctionPrecompiler.PrecompileChildren(scriptFn);
                    compiledFns[i] = scriptFn;
                }
                catch (Exception ex)
                {
                    errors.Add($"{path}: bytecode compile failed: {ex.GetType().Name}: {ex.Message}");
                    bytecodeFailed++;
                }
            }
            // v1.2: bytecode is required, not best-effort. Any compile
            // failure short-circuits the build so v3 archives stay
            // consistent.
            if (bytecodeFailed > 0) return Fail(errors, warnings, sw);

            // Pass 1.5 — accumulate constant references across the
            // compiled RaFunction tree (including nested function
            // bodies that have been opportunistically pre-compiled).
            var poolBuilder = opts.SharedConstPoolEnabled ? new SharedConstPoolBuilder() : null;
            if (poolBuilder != null)
            {
                for (int i = 0; i < modulePathList.Count; i++)
                {
                    var fn = compiledFns[i];
                    if (fn == null) continue;
                    ObserveFunctionConsts(fn, poolBuilder);
                }
                poolBuilder.Finalise();
            }
            byte[]? sharedPoolPayload = null;
            if (poolBuilder != null && poolBuilder.Pooled > 0)
            {
                sharedPoolPayload = poolBuilder.Pool.Encode();
                if (opts.Verbose)
                {
                    warnings.Add(
                        $"const pool: observed {poolBuilder.Observed} refs, pooled {poolBuilder.Pooled} values "
                        + $"(strings={poolBuilder.Pool.Strings.Count}, numbers={poolBuilder.Pool.Numbers.Count}, "
                        + $"ints={poolBuilder.Pool.Integers.Count}, longs={poolBuilder.Pool.Longs.Count}, "
                        + $"doubles={poolBuilder.Pool.Doubles.Count}, floats={poolBuilder.Pool.Floats.Count}, "
                        + $"ulongs={poolBuilder.Pool.ULongs.Count}, int128s={poolBuilder.Pool.Int128s.Count}, "
                        + $"uint128s={poolBuilder.Pool.UInt128s.Count}, decimals={poolBuilder.Pool.Decimals.Count})");
                }
            }

            // Pass 2 — serialise each compiled RaFunction now that the
            // pool is finalised. Any const whose value matches a pool
            // entry encodes as a 5-byte pool ref; the rest stay inline.
            for (int i = 0; i < modulePathList.Count; i++)
            {
                var fn = compiledFns[i];
                if (fn == null) continue;
                try
                {
                    bytecodePayloads[i] = ModuleBytecodeIo.Serialize(fn, poolBuilder);
                    bytecodeCompiled++;
                }
                catch (ModuleBytecodeUnsupportedException ex)
                {
                    errors.Add($"{modulePathList[i]}: bytecode serializer encountered an AST node it cannot handle: {ex.Message}");
                    bytecodeFailed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{modulePathList[i]}: bytecode serialization failed: {ex.GetType().Name}: {ex.Message}");
                    bytecodeFailed++;
                }
            }
            // v1.2: any serialization failure is fatal — we no longer
            // ship archives where some modules carry source-only and
            // others carry bytecode, since the runner now relies on
            // bytecode for every module.
            if (bytecodeFailed > 0) return Fail(errors, warnings, sw);

            // Build the archive.
            //
            // Section layout (locked by RacFormat invariants):
            //   0:                       Manifest
            //   1..N:                    ModuleSource (one per module, in manifest order)
            //   N+1 (optional):          SharedConstPool (when poolBuilder.Pooled > 0)
            //   next..next+M-1:          ModuleBytecode (one per module that compiled)
            //   (last):                  StdLibIndex   (only if non-empty)
            var writer = new RacWriter
            {
                Codec = opts.Codec,
                ZstdLevel = opts.ZstdLevel,
            };
            if (opts.Compress) writer.ArchiveFlags |= RacFlags.Compressed;

            // v1.2 (#sig): wire signing config. Load the private key
            // up-front so a malformed PEM fails the build cleanly
            // before we waste cycles laying out sections.
            if (!string.IsNullOrEmpty(opts.SignKeyPath))
            {
                RacKeyPair signKey;
                try
                {
                    signKey = RacKeyStore.LoadPrivateKey(opts.SignKeyPath!);
                }
                catch (Exception ex)
                {
                    errors.Add($"failed to load signing key '{opts.SignKeyPath}': {ex.Message}");
                    return Fail(errors, warnings, sw);
                }
                writer.SignWith(signKey, opts.SignerId, opts.SignKeyMode);
            }

            // Pre-assign section indices so the manifest carries
            // stable cross-references. Source sections live at
            // `[1, 1+N)`. SharedConstPool (if any) sits immediately
            // after the sources at `1+N`; ModuleBytecode sections
            // follow.
            int firstSrcIdx = 1;
            for (int i = 0; i < manifest.Modules.Count; i++)
                manifest.Modules[i].SourceSectionIndex = firstSrcIdx + i;
            int nextIdx = firstSrcIdx + manifest.Modules.Count;
            if (sharedPoolPayload != null) nextIdx++; // reserve SharedConstPool slot
            for (int i = 0; i < manifest.Modules.Count; i++)
            {
                if (bytecodePayloads[i] != null)
                {
                    manifest.Modules[i].BytecodeSectionIndex = nextIdx++;
                }
            }

            // Re-serialize the manifest *after* patching indices.
            byte[] manifestBytes = manifest.Serialize();
            int mIdx = writer.AddSection(RacSectionKind.Manifest, manifestBytes,
                compress: opts.Compress, mustUnderstand: true);
            if (mIdx != 0)
                throw new InvalidOperationException("rac: Manifest must be the first section");

            for (int i = 0; i < modulePathList.Count; i++)
            {
                byte[] src = System.Text.Encoding.UTF8.GetBytes(graph.GetSource(modulePathList[i]));
                int sIdx = writer.AddSection(RacSectionKind.ModuleSource, src,
                    compress: opts.Compress, mustUnderstand: true);
                if (sIdx != manifest.Modules[i].SourceSectionIndex)
                    throw new InvalidOperationException(
                        $"rac: source section index drift (expected {manifest.Modules[i].SourceSectionIndex}, got {sIdx})");
            }

            // SharedConstPool — sits between the ModuleSource block
            // and the ModuleBytecode block so the loader can fetch it
            // before any bytecode payload that references it.
            if (sharedPoolPayload != null)
            {
                writer.AddSection(RacSectionKind.SharedConstPool, sharedPoolPayload,
                    compress: opts.Compress, mustUnderstand: false);
            }

            // ModuleBytecode sections. Bytecode is the *fast path* but
            // never the *only path* — every module also carries its
            // source section, so a future loader that can't make sense
            // of the bytecode (corrupt, format-bumped, etc.) still has
            // a clean fallback. Hence NOT MustUnderstand.
            for (int i = 0; i < modulePathList.Count; i++)
            {
                if (bytecodePayloads[i] == null) continue;
                int bIdx = writer.AddSection(RacSectionKind.ModuleBytecode, bytecodePayloads[i],
                    compress: opts.Compress, mustUnderstand: false);
                if (bIdx != manifest.Modules[i].BytecodeSectionIndex)
                    throw new InvalidOperationException(
                        $"rac: bytecode section index drift (expected {manifest.Modules[i].BytecodeSectionIndex}, got {bIdx})");
            }

            // StdLibIndex (informational — never MustUnderstand). v1.1
            // emits the tagged form when a shake report exists; falls
            // back to the bare v1.0 form when there's just a list of
            // std refs and no shake happened.
            bool hasShakeReport = shakeResult != null && shakeResult.Stats.Modules.Count > 0;
            if (stdRefs.Count > 0 || hasShakeReport)
            {
                byte[] payload;
                if (hasShakeReport)
                {
                    var shaken = new List<StdLibIndexSection.ShakenModule>();
                    foreach (var rep in shakeResult!.Stats.Modules)
                    {
                        shaken.Add(new StdLibIndexSection.ShakenModule
                        {
                            Path = MakeLogicalPath(projectRoot, stdRoot, rep.Path),
                            BytesBefore = rep.BytesBefore,
                            BytesAfter = rep.BytesAfter,
                            Kept = rep.Kept,
                            Dropped = rep.Dropped,
                        });
                    }
                    payload = StdLibIndexSection.EncodeTagged(stdRefs, shaken);
                }
                else
                {
                    payload = StdLibIndexSection.EncodeBare(stdRefs);
                }
                writer.AddSection(RacSectionKind.StdLibIndex, payload,
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

        // Walk the compiled `RaFunction` and feed every entry of its
        // `Consts[]` array into the shared-pool builder. v1.1 (#7) — the
        // builder tracks reference counts so the finalise step pools
        // only values that show up in >= 2 slots across the whole
        // archive.
        //
        // Scope discipline: we ONLY observe the script-level
        // RaFunction.Consts of each module — never the consts of
        // nested function bodies. That mirrors what the current
        // v2 bytecode payload actually serialises: only the script's
        // Consts[]; nested fn bodies are re-IR-compiled from the AST
        // at runtime on first call. Observing nested-body consts
        // would inflate the pool with values that never get pool-
        // ref'd at write time, paying the pool storage cost without
        // realising the dedup save.
        private static void ObserveFunctionConsts(RaFunction fn, SharedConstPoolBuilder pool)
        {
            var visited = new HashSet<RaFunction>();
            ObserveRecursive(fn, pool, visited);
        }

        // Walk every RaFunction reachable through the script —
        // including the pre-compiled bodies cached on AST nodes in
        // FuncDefRefs / DefineRefs (struct / trait / extension /
        // operator methods). Cross-module dedup only works when the
        // pool sees every const that will eventually hit the
        // serialiser, regardless of which compiled-body lookup
        // pulled it in.
        private static void ObserveRecursive(RaFunction fn, SharedConstPoolBuilder pool,
            HashSet<RaFunction> visited)
        {
            if (fn == null || !visited.Add(fn)) return;
            pool.ObserveMany(fn.Consts);
            if (fn.Children != null)
                foreach (var child in fn.Children) ObserveRecursive(child, pool, visited);
            if (fn.FuncDefRefs != null)
                foreach (var node in fn.FuncDefRefs)
                {
                    if (node?.CompiledBody != null)
                        ObserveRecursive(node.CompiledBody, pool, visited);
                }
            if (fn.DefineRefs != null)
                foreach (var node in fn.DefineRefs)
                {
                    switch (node)
                    {
                        case Parser.Nodes.Classes.ClassDefinitionNode c:
                            foreach (var m in c.Methods)
                                if (m.CompiledBody != null) ObserveRecursive(m.CompiledBody, pool, visited);
                            foreach (var op in c.Operators)
                                if (op.CompiledBody != null) ObserveRecursive(op.CompiledBody, pool, visited);
                            break;
                        case Parser.Nodes.Structs.StructDefinitionNode s:
                            foreach (var m in s.Methods)
                                if (m.CompiledBody != null) ObserveRecursive(m.CompiledBody, pool, visited);
                            foreach (var op in s.Operators)
                                if (op.CompiledBody != null) ObserveRecursive(op.CompiledBody, pool, visited);
                            break;
                        case Parser.Nodes.Traits.TraitDefinitionNode t:
                            foreach (var m in t.Methods)
                                if (m.CompiledBody != null) ObserveRecursive(m.CompiledBody, pool, visited);
                            break;
                        case Parser.Nodes.Classes.ExtensionDefinitionNode e:
                            foreach (var m in e.Methods)
                                if (m.CompiledBody != null) ObserveRecursive(m.CompiledBody, pool, visited);
                            foreach (var op in e.Operators)
                                if (op.CompiledBody != null) ObserveRecursive(op.CompiledBody, pool, visited);
                            break;
                    }
                }
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
            private readonly Dictionary<string, AstNode> _parsedAsts = new(StringComparer.OrdinalIgnoreCase);
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

            public void SetParsed(string absolute, AstNode root)
            {
                _parsedAsts[absolute] = root;
            }

            public AstNode? GetParsed(string absolute)
                => _parsedAsts.TryGetValue(absolute, out var n) ? n : null;

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

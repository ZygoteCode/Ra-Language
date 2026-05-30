using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Archive;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Special;
using System.IO;

namespace RaLanguage.Interpreter.Modules
{
    public enum ModuleState
    {
        Loading,
        Loaded,
        Failed
    }

    public sealed class LoadedModule
    {
        public string AbsolutePath { get; }
        public SymbolTable SymbolTable { get; }
        public ExtensionRegistry Extensions { get; set; }

        public ModuleState State { get; internal set; }
        public DateTime LoadedAt { get; internal set; }

        public LoadedModule(string absolutePath, SymbolTable symbolTable, ExtensionRegistry extensions)
        {
            AbsolutePath = absolutePath;
            SymbolTable = symbolTable;
            Extensions = extensions;
            State = ModuleState.Loading;
            LoadedAt = DateTime.MinValue;
        }

        public IEnumerable<KeyValuePair<string, RuntimeValue>> EnumerateExports()
        {
            foreach (var key in SymbolTable.GetLocalKeys())
            {
                var entry = SymbolTable.GetEntry(key);
                if (entry != null && entry.IsPublic && entry.Value != null)
                {
                    yield return new KeyValuePair<string, RuntimeValue>(key, entry.Value);
                }
            }
        }

        public RuntimeValue? GetExport(string name)
        {
            var entry = SymbolTable.GetEntry(name);
            return (entry != null && entry.IsPublic) ? entry.Value : null;
        }
    }

    public sealed class ModuleLoadResult
    {
        public LoadedModule? Module { get; }
        public Error? Error { get; }

        public bool Ok => Module != null && Error == null;

        private ModuleLoadResult(LoadedModule? module, Error? error)
        {
            Module = module;
            Error = error;
        }

        public static ModuleLoadResult Success(LoadedModule module) => new(module, null);
        public static ModuleLoadResult Failure(Error error) => new(null, error);
    }

    public sealed class ModuleManager
    {
        private readonly Dictionary<string, LoadedModule> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _loadingChain = new();

        // v1.2 (#1): when running from a .rac archive, the RacRunner
        // pre-deserialises every module's ModuleBytecode payload and
        // registers it here. Load() consults this map first — if a
        // precompiled RaFunction is available, we skip the entire
        // lex → parse → DeriveTransformer → Resolver pipeline and
        // hand the already-compiled IR straight to VmExecutor. The
        // module's SymbolTable / ExtensionRegistry still get
        // populated as the precompiled script executes because the
        // top-level OP_DefineFunction / OP_NATIVE_DEFINE opcodes do
        // the same work the AST visitors would.
        private readonly Dictionary<string, RaFunction> _precompiled = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterPrecompiled(string absolutePath, RaFunction fn)
        {
            if (string.IsNullOrEmpty(absolutePath) || fn == null) return;
            _precompiled[absolutePath] = fn;
        }

        public bool HasPrecompiled(string absolutePath)
            => !string.IsNullOrEmpty(absolutePath) && _precompiled.ContainsKey(absolutePath);

        // M88: process-wide accumulator of every binding name ever
        // assigned by any function in any loaded module. Read by the
        // LICM `LoadLocalS` hoist's cross-file gate when an importing
        // function has `HasImports = true` — the typed walker behind
        // the per-function `MutatedNames` set cannot see into modules
        // that load lazily at runtime, so this registry serves as a
        // conservative super-set for the closure-aliasing check.
        //
        // Static field on purpose: the registry survives across
        // `InitializeSymbolTable` resets (which reset
        // `ModuleManager` instances) because the LICM consults it
        // from any compilation that hands its IR to a VM run.
        public static readonly System.Collections.Generic.HashSet<string> GlobalMutatedNames =
            new(System.StringComparer.Ordinal);

        // M88: register every binding name a loaded module may mutate.
        // Walks the module's compiled function values (or the module's
        // symbol table when those are still in AST form) and unions
        // each contributing function's `MutatedNames` into the global
        // registry. Idempotent — a name added by one module re-added
        // by another costs only a hash lookup.
        public static void RegisterModuleMutatedNames(LoadedModule module)
        {
            if (module == null) return;
            // Walk every binding in the module's symbol table (not
            // only exports) — a non-exported helper closure can still
            // mutate state observed elsewhere when its result is
            // returned through an exported wrapper.
            foreach (var key in module.SymbolTable.GetLocalKeys())
            {
                var entry = module.SymbolTable.GetEntry(key);
                if (entry == null) continue;
                if (entry.Value is RaLanguage.Interpreter.Values.Functions.FunctionValue fv
                    && fv.CompiledBody?.MutatedNames != null)
                {
                    foreach (var nm in fv.CompiledBody.MutatedNames)
                        GlobalMutatedNames.Add(nm);
                }
            }
        }
        private readonly ModuleResolver _resolver;
        // Parent scope for every loaded module / user scope — the always-on
        // core (ADTs + annotation types), NOT the built-in functions.
        private readonly Func<SymbolTable> _coreProvider;
        // Source for virtual std-module synthesis — the full built-in store
        // (where the categorised function values actually live).
        private readonly Func<SymbolTable> _functionStoreProvider;

        public ModuleResolver Resolver => _resolver;

        public ModuleManager(ModuleResolver resolver, Func<SymbolTable> coreProvider, Func<SymbolTable> functionStoreProvider)
        {
            _resolver = resolver;
            _coreProvider = coreProvider;
            _functionStoreProvider = functionStoreProvider;
        }

        public void Clear()
        {
            _cache.Clear();
            _loadingChain.Clear();
        }

        public ModuleLoadResult Load(
            ModuleSpecifier spec,
            string currentFile,
            IInterpreter interpreter,
            Position posStart,
            Position posEnd)
        {
            // Virtual std-library surface. A dotted path under `std` may name
            // a manifest-synthesised module of categorised built-ins (e.g.
            // `std.prelude.io`), a package aggregate (`std.prelude`,
            // `std.prelude.*`, `std.sys`, bare `std`), or — when a physical
            // `std/<...>.ra` file exists — fall straight through to the file
            // loader below so legacy imports like `import std.io` are
            // byte-for-byte unchanged.
            if (spec.Kind == ModuleSpecifierKind.Dotted
                && spec.Segments != null && spec.Segments.Count >= 1
                && string.Equals(spec.Segments[0], StdLibrary.Root, StringComparison.Ordinal))
            {
                if (TryPlanStdImport(spec, out var stdPlan))
                    return LoadStdPlan(stdPlan, currentFile, interpreter, posStart, posEnd);
                // else: physical std file — handled by the resolver path below.
            }

            var resolution = _resolver.Resolve(spec, currentFile);
            if (!resolution.Ok)
            {
                return ModuleLoadResult.Failure(
                    new ModuleNotFoundError(posStart, posEnd, resolution.ErrorMessage ?? "Module not found"));
            }

            string absolute = resolution.AbsolutePath!;

            if (_cache.TryGetValue(absolute, out var existing))
            {
                if (existing.State == ModuleState.Loaded)
                    return ModuleLoadResult.Success(existing);

                if (existing.State == ModuleState.Loading)
                {
                    string chain = string.Join(" -> ", _loadingChain) + " -> " + absolute;
                    return ModuleLoadResult.Failure(
                        new CircularImportError(posStart, posEnd,
                            $"Circular import detected when loading '{spec.Display}':\n  {chain}"));
                }
            }

            // M83 — depth-based safety net beyond the state-based
            // cycle detection above. State-based detection catches
            // A→B→A loops because A's cache entry is in `Loading`
            // when B re-imports it. But adversarial / generated
            // module graphs that AREN'T cyclic but explode in
            // depth (a chain of 10000 unique imports) could blow
            // the C# stack since each Load call recurses through
            // the parser → interpreter → ImportNodeVisitor →
            // ModuleManager.Load chain. Cap the loading chain at
            // a defensive depth so runaway chains surface as a
            // clean RuntimeError instead of a StackOverflow that
            // AppDomain cannot catch.
            const int MaxImportChainDepth = 512;
            if (_loadingChain.Count >= MaxImportChainDepth)
            {
                string chain = string.Join(" -> ", _loadingChain) + " -> " + absolute;
                return ModuleLoadResult.Failure(
                    new ModuleLoadError(posStart, posEnd,
                        $"Import chain too deep ({_loadingChain.Count} levels) when loading '{spec.Display}':\n  {chain}"));
            }

            var moduleSymbolTable = new SymbolTable(_coreProvider());
            var moduleExtensions = new ExtensionRegistry();
            var module = new LoadedModule(absolute, moduleSymbolTable, moduleExtensions);

            _cache[absolute] = module;
            _loadingChain.Add(absolute);

            try
            {
                var moduleContext = new Context(
                    displayName: absolute,
                    parent: null,
                    parentEntryPos: null,
                    extensions: moduleExtensions);
                moduleContext.SymbolTable = moduleSymbolTable;

                // v1.2 (#1): fast path. RacRunner registered the
                // module's precompiled RaFunction tree at archive-load
                // time, so we can skip lex / parse / DeriveTransformer /
                // Resolver entirely and hand the cached IR straight to
                // the VM. The top-level OP_DefineFunction /
                // OP_NATIVE_DEFINE / OP_DefineClass etc. populate the
                // moduleSymbolTable the same way the AST visitor path
                // would. Imports inside the precompiled module recurse
                // through this same ModuleManager.Load, which will hit
                // the precompiled path again if their bytecode is
                // registered (the RacRunner registers every module
                // before driving the entry, so this holds for any
                // import reachable through the archive's manifest).
                if (_precompiled.TryGetValue(absolute, out var precompiled))
                {
                    var bcRun = AwaitSync(new RaLanguage.Interpreter.Vm.VmExecutor(interpreter).RunScript(precompiled, moduleContext));
                    if (bcRun.Error != null)
                    {
                        module.State = ModuleState.Failed;
                        _cache.Remove(absolute);
                        return ModuleLoadResult.Failure(bcRun.Error);
                    }
                    FreezeFunctionClosures(moduleSymbolTable, moduleContext);
                    module.State = ModuleState.Loaded;
                    module.LoadedAt = DateTime.UtcNow;
                    RegisterModuleMutatedNames(module);
                    return ModuleLoadResult.Success(module);
                }

                string source;
                try
                {
                    source = VirtualFs.ReadAllText(absolute);
                }
                catch (Exception ex)
                {
                    module.State = ModuleState.Failed;
                    _cache.Remove(absolute);
                    return ModuleLoadResult.Failure(
                        new ModuleLoadError(posStart, posEnd,
                            $"Failed to read module file '{absolute}': {ex.Message}"));
                }

                var lexer = new Lexer.Lexer(absolute, source);
                var (tokens, lexerDiagnostics) = lexer.MakeTokens();

                if (lexerDiagnostics.HasErrors)
                {
                    module.State = ModuleState.Failed;
                    _cache.Remove(absolute);
                    var err = new ModuleLoadError(posStart, posEnd,
                        $"failed to lex module '{absolute}' ({lexerDiagnostics.Summary()})");
                    err.WithCause(lexerDiagnostics.FirstError);
                    return ModuleLoadResult.Failure(err);
                }

                var parser = new Parser.Parser(tokens);
                var parseResult = parser.Parse();

                if (parseResult.HasErrors)
                {
                    module.State = ModuleState.Failed;
                    _cache.Remove(absolute);
                    var err = new ModuleLoadError(posStart, posEnd,
                        $"failed to parse module '{absolute}' ({parseResult.Diagnostics.Summary()})");
                    err.WithCause(parseResult.Diagnostics.FirstError);
                    return ModuleLoadResult.Failure(err);
                }

                if (parseResult.Node == null)
                {
                    module.State = ModuleState.Loaded;
                    module.LoadedAt = DateTime.UtcNow;
                    return ModuleLoadResult.Success(module);
                }

                DeriveTransformer.Apply(parseResult.Node);
                // M19: imported modules need Resolver too so their function
                // bodies get FrameId / ParamBindings populated. Without this
                // every FunctionValue created from a module compiles with
                // FrameId<0 → CompiledBody=null → "no executable body" at
                // call time, since the AST fallback is gone.
                Pipeline.Resolver.Resolve(parseResult.Node);

                Error? executionError = ExecuteModule(parseResult.Node, moduleContext, interpreter);
                if (executionError != null)
                {
                    module.State = ModuleState.Failed;
                    _cache.Remove(absolute);
                    return ModuleLoadResult.Failure(executionError);
                }

                FreezeFunctionClosures(moduleSymbolTable, moduleContext);

                module.State = ModuleState.Loaded;
                module.LoadedAt = DateTime.UtcNow;
                // M88: register the loaded module's exported function
                // MutatedNames into the process-wide registry so any
                // subsequent LICM re-analysis (tier-up compile) of an
                // importer can prove individual binding names safe
                // against cross-module mutation without falling back
                // to the blanket `HasImports` refuse-all gate.
                RegisterModuleMutatedNames(module);
                return ModuleLoadResult.Success(module);
            }
            catch (Exception ex)
            {
                module.State = ModuleState.Failed;
                _cache.Remove(absolute);
                return ModuleLoadResult.Failure(
                    new ModuleLoadError(posStart, posEnd,
                        $"Unexpected error while loading module '{absolute}': {ex.Message}"));
            }
            finally
            {
                if (_loadingChain.Count > 0 && _loadingChain[_loadingChain.Count - 1] == absolute)
                {
                    _loadingChain.RemoveAt(_loadingChain.Count - 1);
                }
            }
        }

        // ---- std-library virtual modules & packages ---------------------

        private enum StdPlanKind { VirtualModule, Package, NotFound }

        private readonly struct StdPlan
        {
            public StdPlanKind Kind { get; }
            public string CacheKey { get; }
            public string DottedPath { get; }
            public string? PhysicalDir { get; }
            public string? ErrorMessage { get; }

            private StdPlan(StdPlanKind kind, string cacheKey, string dotted, string? physicalDir, string? error)
            { Kind = kind; CacheKey = cacheKey; DottedPath = dotted; PhysicalDir = physicalDir; ErrorMessage = error; }

            public static StdPlan Module(string dotted) =>
                new(StdPlanKind.VirtualModule, "\0std-mod:" + dotted, dotted, null, null);
            public static StdPlan PackageOf(string dotted, string? physicalDir) =>
                new(StdPlanKind.Package, "\0std-pkg:" + dotted, dotted, physicalDir, null);
            public static StdPlan NotFound(string dotted, string error) =>
                new(StdPlanKind.NotFound, "\0std-x:" + dotted, dotted, null, error);
        }

        // Returns true when the dotted std path is a virtual module/package
        // (yielding a plan); false when it is a physical std file the normal
        // resolver should load (which preserves `import std.io`).
        private bool TryPlanStdImport(ModuleSpecifier spec, out StdPlan plan)
        {
            var segs = spec.Segments!;
            bool wildcard = spec.IsWildcard;
            string dotted = string.Join(".", segs);
            bool isRoot = segs.Count == 1;

            string? physicalFile = isRoot ? null : StdSubFile(segs);
            bool physicalFileExists = physicalFile != null && VirtualFs.Exists(physicalFile);
            string physicalDir = StdSubDir(segs);
            bool physicalDirExists = VirtualFs.DirectoryExists(physicalDir);

            bool isModule = StdLibrary.IsModule(dotted);
            bool isPackage = isRoot || StdLibrary.HasDescendants(dotted) || physicalDirExists;

            if (wildcard)
            {
                if (isPackage) { plan = StdPlan.PackageOf(dotted, physicalDirExists ? physicalDir : null); return true; }
                if (isModule) { plan = StdPlan.Module(dotted); return true; }
                if (physicalFileExists) { plan = default; return false; }
                plan = StdPlan.NotFound(dotted, StdNotFoundMessage(dotted)); return true;
            }

            // Non-wildcard precedence: a manifest virtual module wins (so the
            // categorised built-ins are never shadowed by a stray file), then
            // a physical file (legacy `import std.io`), then a package.
            if (isModule) { plan = StdPlan.Module(dotted); return true; }
            if (physicalFileExists) { plan = default; return false; }
            if (isPackage) { plan = StdPlan.PackageOf(dotted, physicalDirExists ? physicalDir : null); return true; }

            plan = StdPlan.NotFound(dotted, StdNotFoundMessage(dotted));
            return true;
        }

        private string StdSubFile(IReadOnlyList<string> segs)
        {
            var parts = new string[segs.Count - 1];
            for (int i = 1; i < segs.Count; i++) parts[i - 1] = segs[i];
            string rel = string.Join(Path.DirectorySeparatorChar.ToString(), parts) + ".ra";
            return Path.GetFullPath(Path.Combine(_resolver.StdRoot, rel));
        }

        private string StdSubDir(IReadOnlyList<string> segs)
        {
            if (segs.Count == 1) return _resolver.StdRoot;
            var parts = new string[segs.Count - 1];
            for (int i = 1; i < segs.Count; i++) parts[i - 1] = segs[i];
            string rel = string.Join(Path.DirectorySeparatorChar.ToString(), parts);
            return Path.GetFullPath(Path.Combine(_resolver.StdRoot, rel));
        }

        private static string StdNotFoundMessage(string dotted)
        {
            var available = StdLibrary.SortedModulePaths();
            string list = available.Count == 0 ? "(none)" : string.Join(", ", available);
            return $"no std module or package '{dotted}'. Built-in std modules: {list}. "
                 + "Use a trailing '.*' to import a whole package, or create a physical "
                 + "'std/<path>.ra' file for a custom module.";
        }

        private ModuleLoadResult LoadStdPlan(StdPlan plan, string currentFile, IInterpreter interpreter, Position posStart, Position posEnd)
        {
            switch (plan.Kind)
            {
                case StdPlanKind.NotFound:
                    return ModuleLoadResult.Failure(new ModuleNotFoundError(posStart, posEnd, plan.ErrorMessage ?? "Module not found"));
                case StdPlanKind.VirtualModule:
                    return LoadVirtualModule(plan);
                case StdPlanKind.Package:
                    return LoadPackage(plan, currentFile, interpreter, posStart, posEnd);
                default:
                    return ModuleLoadResult.Failure(new ModuleLoadError(posStart, posEnd, $"unhandled std plan for '{plan.DottedPath}'"));
            }
        }

        // Synthesises a single virtual module: a fresh SymbolTable populated
        // with the live BuiltInFunctionValue instances for the module's
        // categorised members, all marked public so they export.
        private ModuleLoadResult LoadVirtualModule(StdPlan plan)
        {
            if (_cache.TryGetValue(plan.CacheKey, out var existing) && existing.State == ModuleState.Loaded)
                return ModuleLoadResult.Success(existing);

            var members = StdLibrary.ModuleMembers(plan.DottedPath);
            var st = new SymbolTable();
            var module = new LoadedModule(plan.CacheKey, st, new ExtensionRegistry());
            if (members != null)
            {
                var builtins = _functionStoreProvider();
                foreach (var name in members)
                {
                    var entry = builtins.GetEntry(name);
                    if (entry?.Value == null) continue;
                    st.Set(name, entry.Value, isLet: false, declaredType: null, isStaticallyTyped: false, isPublic: true);
                }
            }
            module.State = ModuleState.Loaded;
            module.LoadedAt = DateTime.UtcNow;
            _cache[plan.CacheKey] = module;
            return ModuleLoadResult.Success(module);
        }

        // Aggregates a package: the union of every virtual module beneath the
        // path PLUS every physical `.ra` file in the matching std directory
        // (recursively). Physical children load through the normal pipeline,
        // so their exports and extensions merge into the aggregate.
        private ModuleLoadResult LoadPackage(StdPlan plan, string currentFile, IInterpreter interpreter, Position posStart, Position posEnd)
        {
            if (_cache.TryGetValue(plan.CacheKey, out var existing))
            {
                if (existing.State == ModuleState.Loaded) return ModuleLoadResult.Success(existing);
                if (existing.State == ModuleState.Loading)
                    return ModuleLoadResult.Failure(new CircularImportError(posStart, posEnd,
                        $"Circular import detected when assembling package '{plan.DottedPath}'"));
            }

            var st = new SymbolTable();
            var ext = new ExtensionRegistry();
            var module = new LoadedModule(plan.CacheKey, st, ext); // state = Loading
            _cache[plan.CacheKey] = module;
            _loadingChain.Add(plan.CacheKey);
            try
            {
                // 1. Virtual members — categorised built-ins under this package.
                var builtins = _functionStoreProvider();
                foreach (var name in StdLibrary.PackageMembers(plan.DottedPath))
                {
                    if (st.GetEntry(name) != null) continue;
                    var entry = builtins.GetEntry(name);
                    if (entry?.Value == null) continue;
                    st.Set(name, entry.Value, isLet: false, declaredType: null, isStaticallyTyped: false, isPublic: true);
                }

                // 2. Physical `.ra` files in the package directory (recursive).
                if (plan.PhysicalDir != null)
                {
                    foreach (var file in VirtualFs.EnumerateRaFiles(plan.PhysicalDir, recursive: true))
                    {
                        var childResult = Load(ModuleSpecifier.FromStringLiteral(file), currentFile, interpreter, posStart, posEnd);
                        if (!childResult.Ok)
                        {
                            module.State = ModuleState.Failed;
                            _cache.Remove(plan.CacheKey);
                            return childResult;
                        }
                        var child = childResult.Module!;
                        foreach (var kvp in child.EnumerateExports())
                        {
                            if (st.GetEntry(kvp.Key) != null) continue;
                            var se = child.SymbolTable.GetEntry(kvp.Key);
                            st.Set(kvp.Key, kvp.Value,
                                isLet: se?.IsLet ?? false,
                                declaredType: se?.DeclaredType,
                                isStaticallyTyped: se?.IsStaticallyTyped ?? false,
                                isPublic: true);
                        }
                        var mergeErr = RaLanguage.Interpreter.Visitors.Imports.ImportNodeVisitor
                            .MergeExtensions(ext, child.Extensions, posStart, posEnd);
                        if (mergeErr != null)
                        {
                            module.State = ModuleState.Failed;
                            _cache.Remove(plan.CacheKey);
                            return ModuleLoadResult.Failure(mergeErr);
                        }
                    }
                }

                module.State = ModuleState.Loaded;
                module.LoadedAt = DateTime.UtcNow;
                return ModuleLoadResult.Success(module);
            }
            catch (Exception ex)
            {
                module.State = ModuleState.Failed;
                _cache.Remove(plan.CacheKey);
                return ModuleLoadResult.Failure(new ModuleLoadError(posStart, posEnd,
                    $"Unexpected error while assembling std package '{plan.DottedPath}': {ex.Message}"));
            }
            finally
            {
                if (_loadingChain.Count > 0 && _loadingChain[_loadingChain.Count - 1] == plan.CacheKey)
                    _loadingChain.RemoveAt(_loadingChain.Count - 1);
            }
        }

        private static void FreezeFunctionClosures(SymbolTable table, Context moduleContext)
        {
            foreach (var key in table.GetLocalKeys())
            {
                var entry = table.GetEntry(key);
                if (entry?.Value is BaseFunctionValue bfn)
                {
                    bfn.FreezeBindingContext(moduleContext);
                }
            }
        }

        private static Error? ExecuteModule(AstNode root, Context ctx, IInterpreter interpreter)
        {
            // Module bodies are evaluated at import time, before any user
            // `await` can fire. We collapse the ValueTask synchronously here:
            // top-level module statements should not themselves suspend, and
            // bottling them off through GetAwaiter().GetResult() keeps the
            // import API sync without infecting every caller. M24: drive each
            // statement through IrExpressionEvaluator (compile-once VM run)
            // instead of the AST visitor dispatch.
            if (root is ScopeNode scope)
            {
                foreach (var stmt in scope.Nodes)
                {
                    var result = RaLanguage.Interpreter.Runtime.IrExpressionEvaluator
                        .EvaluateStatementBlocking(stmt, ctx, interpreter);
                    if (result.Error != null) return result.Error;
                }
                return null;
            }

            var single = RaLanguage.Interpreter.Runtime.IrExpressionEvaluator
                .EvaluateStatementBlocking(root, ctx, interpreter);
            return single.Error;
        }

        private static RuntimeResult AwaitSync(System.Threading.Tasks.ValueTask<RuntimeResult> task)
        {
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.AsTask().GetAwaiter().GetResult();
        }
    }
}

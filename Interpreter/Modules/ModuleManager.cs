using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
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
        private readonly ModuleResolver _resolver;
        private readonly Func<SymbolTable> _builtinsProvider;

        public ModuleResolver Resolver => _resolver;

        public ModuleManager(ModuleResolver resolver, Func<SymbolTable> builtinsProvider)
        {
            _resolver = resolver;
            _builtinsProvider = builtinsProvider;
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

            string source;
            try
            {
                source = File.ReadAllText(absolute);
            }
            catch (Exception ex)
            {
                return ModuleLoadResult.Failure(
                    new ModuleLoadError(posStart, posEnd,
                        $"Failed to read module file '{absolute}': {ex.Message}"));
            }

            var builtins = _builtinsProvider();
            var moduleSymbolTable = new SymbolTable(builtins);
            var moduleExtensions = new ExtensionRegistry();
            var module = new LoadedModule(absolute, moduleSymbolTable, moduleExtensions);

            _cache[absolute] = module;
            _loadingChain.Add(absolute);

            try
            {
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

                var moduleContext = new Context(
                    displayName: absolute,
                    parent: null,
                    parentEntryPos: null,
                    extensions: moduleExtensions);
                moduleContext.SymbolTable = moduleSymbolTable;

                DeriveTransformer.Apply(parseResult.Node);

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
            // import API sync without infecting every caller.
            if (root is ScopeNode scope)
            {
                foreach (var stmt in scope.Nodes)
                {
                    var result = AwaitSync(interpreter.Visit(stmt, ctx));
                    if (result.Error != null) return result.Error;
                }
                return null;
            }

            var single = AwaitSync(interpreter.Visit(root, ctx));
            return single.Error;
        }

        private static RuntimeResult AwaitSync(System.Threading.Tasks.ValueTask<RuntimeResult> task)
        {
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.AsTask().GetAwaiter().GetResult();
        }
    }
}

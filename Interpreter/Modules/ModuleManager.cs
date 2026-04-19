using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using System.Collections.Concurrent;

namespace RaLanguage.Interpreter.Modules
{
    public class LoadedModule
    {
        public string AbsolutePath { get; }
        public SymbolTable SymbolTable { get; }
        public ExtensionRegistry Extensions { get; set; }
        public bool IsLoaded { get; private set; }
        public DateTime LoadedAt { get; private set; }

        public LoadedModule(string absolutePath)
        {
            AbsolutePath = absolutePath;
            SymbolTable = new SymbolTable();
            Extensions = new ExtensionRegistry();
            IsLoaded = false;
            LoadedAt = DateTime.MinValue;
        }

        public void MarkAsLoaded()
        {
            IsLoaded = true;
            LoadedAt = DateTime.Now;
        }
    }

    public class ModuleManager
    {
        private readonly ConcurrentDictionary<string, LoadedModule> _moduleCache = new();
        private readonly string _basePath;

        public ModuleManager(string basePath)
        {
            _basePath = Path.GetFullPath(basePath);
        }

        public string ResolvePath(string modulePath, string currentFile)
        {
            if (Path.IsPathRooted(modulePath))
            {
                return Path.GetFullPath(modulePath);
            }

            string currentDir = Path.GetDirectoryName(Path.GetFullPath(currentFile))!;
            string resolved = Path.GetFullPath(Path.Combine(currentDir, modulePath));
            
            return resolved;
        }

        public LoadedModule GetOrCreateModule(string absolutePath)
        {
            return _moduleCache.GetOrAdd(absolutePath, path => new LoadedModule(path));
        }

        public bool IsModuleLoaded(string absolutePath)
        {
            return _moduleCache.TryGetValue(absolutePath, out var module) && module.IsLoaded;
        }

        public async Task<LoadedModule> LoadModule(string modulePath, string currentFile, IInterpreter interpreter, Context parentContext)
        {
            string absolutePath = ResolvePath(modulePath, currentFile);

            if (IsModuleLoaded(absolutePath))
            {
                return GetOrCreateModule(absolutePath);
            }

            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"Module file not found: {absolutePath}");
            }

            var module = GetOrCreateModule(absolutePath);
            string sourceCode = await File.ReadAllTextAsync(absolutePath);

            var lexer = new Lexer.Lexer(absolutePath, sourceCode);
            var (tokens, lexerError) = lexer.MakeTokens();
            
            if (lexerError != null)
            {
                throw new Exception($"Lexer error in module {absolutePath}: {lexerError.Details}");
            }

            var parser = new Parser.Parser(tokens);
            var parserResult = parser.Parse();
            
            if (parserResult.Error != null)
            {
                throw new Exception($"Parser error in module {absolutePath}: {parserResult.Error.Details}");
            }

            var moduleContext = new Context(
                displayName: Path.GetFileName(absolutePath),
                parent: null,
                parentEntryPos: null,
                extensions: parentContext.Extensions
            );

            if (parentContext.SymbolTable != null)
            {
                foreach (var key in parentContext.SymbolTable.GetLocalKeys())
                {
                    var entry = parentContext.SymbolTable.GetEntry(key);
                    if (entry != null)
                    {
                        moduleContext.SymbolTable.Set(key, entry.Value, isPublic: false);
                    }
                }
            }

            if (parserResult.Node != null)
            {
                var result = interpreter.Visit(parserResult.Node, moduleContext);

                if (result.Error != null)
                {
                    throw new Exception($"Runtime error in module {absolutePath}: {result.Error.Details}");
                }
            }

            CopySymbolsToModule(moduleContext.SymbolTable, module);

            module.Extensions = moduleContext.Extensions;
            module.MarkAsLoaded();

            return module;
        }

        public RuntimeValue? GetSymbolFromModule(string absolutePath, string symbolName)
        {            
            if (_moduleCache.TryGetValue(absolutePath, out var module))
            {
                var entry = module.SymbolTable.GetEntry(symbolName);
                
                if (entry != null)
                {
                    return entry.Value;
                }
            }

            return null;
        }

        public Dictionary<string, RuntimeValue> GetAllPublicSymbols(string absolutePath)
        {
            var symbols = new Dictionary<string, RuntimeValue>();
            
            if (_moduleCache.TryGetValue(absolutePath, out var module))
            {
                foreach (var key in module.SymbolTable.GetLocalKeys())
                {
                    var entry = module.SymbolTable.GetEntry(key);
                    if (entry != null && entry.IsPublic)
                    {
                        symbols[key] = entry.Value;
                    }
                }
            }

            return symbols;
        }

        public ExtensionRegistry GetModuleExtensions(string absolutePath)
        {
            if (_moduleCache.TryGetValue(absolutePath, out var module))
            {
                return module.Extensions;
            }
            return new ExtensionRegistry();
        }

        private void CopySymbolsToModule(SymbolTable? contextSymbolTable, LoadedModule module)
        {
            if (contextSymbolTable == null) 
            {
                return;
            }

            foreach (var key in contextSymbolTable.GetLocalKeys())
            {
                var entry = contextSymbolTable.GetEntry(key);
                if (entry != null)
                {                    
                    module.SymbolTable.Set(
                        key,
                        entry.Value.Copy(),
                        isLet: entry.IsLet,
                        declaredType: entry.DeclaredType,
                        isStaticallyTyped: entry.IsStaticallyTyped,
                        isPublic: entry.IsPublic
                    );
                }
            }            
        }

        public void ClearCache()
        {
            _moduleCache.Clear();
        }
    }
}

using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Imports
{
    public class ImportNodeVisitor : INodeVisitor
    {
        private static ModuleManager? _moduleManager;

        public static ModuleManager ModuleManager
        {
            get
            {
                if (_moduleManager == null)
                {
                    throw new InvalidOperationException(
                        "ModuleManager not initialized. Call InitializeModuleManager first.");
                }
                return _moduleManager;
            }
        }

        public static void InitializeModuleManager(string projectRoot, string stdRoot, Func<SymbolTable> builtinsProvider)
        {
            var resolver = new ModuleResolver(projectRoot, stdRoot);
            _moduleManager = new ModuleManager(resolver, builtinsProvider);
        }

        public static void ResetCache()
        {
            _moduleManager?.Clear();
        }

        public RuntimeResult Visit(AstNode node, Context context, IInterpreter interpreter)
        {
            return node.NodeType switch
            {
                AstNodeType.ImportAll => VisitImportAll((ImportAllNode)node, context, interpreter),
                AstNodeType.ImportSelective => VisitImportSelective((ImportSelectiveNode)node, context, interpreter),
                AstNodeType.ImportAlias => VisitImportAlias((ImportAliasNode)node, context, interpreter),
                _ => throw new InvalidOperationException($"Unknown import node type: {node.NodeType}")
            };
        }

        private static LoadedModule? LoadOrFail(
            ImportNode node,
            Context context,
            IInterpreter interpreter,
            out Error? error)
        {
            string currentFile = context.DisplayName ?? "main.ra";
            var loadResult = ModuleManager.Load(
                node.Specifier,
                currentFile,
                interpreter,
                node.PositionStart,
                node.PositionEnd);

            if (!loadResult.Ok)
            {
                error = loadResult.Error;
                return null;
            }

            error = null;
            return loadResult.Module;
        }

        private RuntimeResult VisitImportAll(ImportAllNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();

            var module = LoadOrFail(node, context, interpreter, out var error);
            if (module == null)
            {
                return result.Failure(error!);
            }

            foreach (var kvp in module.EnumerateExports())
            {
                if (context.SymbolTable == null) break;

                var sourceEntry = module.SymbolTable.GetEntry(kvp.Key);
                context.SymbolTable.Set(
                    kvp.Key,
                    kvp.Value,
                    isLet: sourceEntry?.IsLet ?? false,
                    declaredType: sourceEntry?.DeclaredType,
                    isStaticallyTyped: sourceEntry?.IsStaticallyTyped ?? false,
                    isPublic: true);
            }

            MergeExtensions(context.Extensions, module.Extensions);

            return result.Success(NullValue.Null
                .SetPos(node.PositionStart, node.PositionEnd)
                .SetContext(context));
        }

        private RuntimeResult VisitImportSelective(ImportSelectiveNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();

            var module = LoadOrFail(node, context, interpreter, out var error);
            if (module == null)
            {
                return result.Failure(error!);
            }

            foreach (var symbolTok in node.SymbolNames)
            {
                string symbolName = symbolTok.Value?.ToString() ?? "";

                var symbolValue = module.GetExport(symbolName);
                if (symbolValue == null)
                {
                    return result.Failure(new SymbolNotFoundError(
                        symbolTok.PositionStart, symbolTok.PositionEnd,
                        $"Symbol '{symbolName}' not found or not public in module '{node.Specifier.Display}'"));
                }

                if (context.SymbolTable == null) continue;

                var sourceEntry = module.SymbolTable.GetEntry(symbolName);
                context.SymbolTable.Set(
                    symbolName,
                    symbolValue,
                    isLet: sourceEntry?.IsLet ?? false,
                    declaredType: sourceEntry?.DeclaredType,
                    isStaticallyTyped: sourceEntry?.IsStaticallyTyped ?? false,
                    isPublic: true);
            }

            return result.Success(NullValue.Null
                .SetPos(node.PositionStart, node.PositionEnd)
                .SetContext(context));
        }

        private RuntimeResult VisitImportAlias(ImportAliasNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();

            var module = LoadOrFail(node, context, interpreter, out var error);
            if (module == null)
            {
                return result.Failure(error!);
            }

            var moduleWrapper = new ModuleWrapperValue(node.Alias, module, context);
            context.SymbolTable?.Set(node.Alias, moduleWrapper, isPublic: true);

            return result.Success(NullValue.Null
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd));
        }

        private static void MergeExtensions(ExtensionRegistry target, ExtensionRegistry source)
        {
            if (target == null || source == null || ReferenceEquals(target, source)) return;
            foreach (var kvp in source.AllMethods)
            {
                foreach (var method in kvp.Value)
                {
                    target.Register(kvp.Key, method);
                }
            }
        }
    }

    public class ModuleWrapperValue : RuntimeValue
    {
        private readonly string _moduleName;
        private readonly LoadedModule _module;
        private readonly Context _context;
        public override RuntimeValueType Type => RuntimeValueType.ModuleWrapper;
        public LoadedModule Module => _module;

        public ModuleWrapperValue(string moduleName, LoadedModule module, Context context)
        {
            _moduleName = moduleName;
            _module = module;
            _context = context;
        }

        public override RuntimeValue Copy()
        {
            return new ModuleWrapperValue(_moduleName, _module, _context);
        }

        public (RuntimeValue, Error?) Get(string key, Position posStart, Position posEnd)
        {
            var exported = _module.GetExport(key);
            if (exported != null)
            {
                return (exported.SetContext(_context).SetPos(posStart, posEnd), null);
            }

            return (NullValue.Null.SetContext(_context).SetPos(posStart, posEnd),
                new SymbolNotFoundError(posStart, posEnd,
                    $"Symbol '{key}' not found or not public in module '{_moduleName}'"));
        }

        public override string ToString() => $"<module '{_moduleName}'>";
    }
}

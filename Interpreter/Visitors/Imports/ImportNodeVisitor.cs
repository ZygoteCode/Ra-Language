using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values.Primitives;

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
                    throw new Exception("ModuleManager not initialized. Call InitializeModuleManager first.");
                }
                return _moduleManager;
            }
        }

        public static void InitializeModuleManager(string basePath)
        {
            _moduleManager = new ModuleManager(basePath);
        }

        public RuntimeResult Visit(AstNode node, Context context, IInterpreter interpreter)
        {
            return node.NodeType switch
            {
                AstNodeType.ImportAll => VisitImportAll((ImportAllNode)node, context, interpreter),
                AstNodeType.ImportSelective => VisitImportSelective((ImportSelectiveNode)node, context, interpreter),
                AstNodeType.ImportAlias => VisitImportAlias((ImportAliasNode)node, context, interpreter),
                _ => throw new System.Exception($"Unknown import node type: {node.NodeType}")
            };
        }

        private RuntimeResult VisitImportAll(ImportAllNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();
            var posStart = node.PositionStart;
            var posEnd = node.PositionEnd;

            try
            {
                var currentFile = context.DisplayName ?? "main.ra";
                var module = ModuleManager.LoadModule(node.ModulePath, currentFile, interpreter, context).Result;

                var symbols = ModuleManager.GetAllPublicSymbols(module.AbsolutePath);

                foreach (var kvp in symbols)
                {
                    context.SymbolTable.Set(kvp.Key, kvp.Value.Copy(), isPublic: true);
                }

                var extensions = ModuleManager.GetModuleExtensions(module.AbsolutePath);
                return result.Success(new NullValue().SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
            }
            catch (Exception ex)
            {
                return result.Failure(new RuntimeError(posStart, posEnd, $"Import error: {ex.Message}", context));
            }
        }

        private RuntimeResult VisitImportSelective(ImportSelectiveNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();
            var posStart = node.PositionStart;
            var posEnd = node.PositionEnd;

            try
            {
                var currentFile = context.DisplayName ?? "main.ra";
                var module = ModuleManager.LoadModule(node.ModulePath, currentFile, interpreter, context).Result;

                foreach (var symbolTok in node.SymbolNames)
                {
                    string symbolName = symbolTok.Value?.ToString() ?? "";
                    
                    var symbolValue = ModuleManager.GetSymbolFromModule(module.AbsolutePath, symbolName);
                    
                    if (symbolValue == null)
                    {
                        return result.Failure(new RuntimeError(
                            posStart, posEnd,
                            $"Symbol '{symbolName}' not found in module '{node.ModulePath}'",
                            context));
                    }

                    context.SymbolTable.Set(symbolName, symbolValue.Copy(), isPublic: true);
                }

                return result.Success(new NullValue().SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
            }
            catch (Exception ex)
            {
                return result.Failure(new RuntimeError(posStart, posEnd, $"Import error: {ex.Message}", context));
            }
        }

        private RuntimeResult VisitImportAlias(ImportAliasNode node, Context context, IInterpreter interpreter)
        {
            var result = new RuntimeResult();
            var posStart = node.PositionStart;
            var posEnd = node.PositionEnd;

            try
            {
                var currentFile = context.DisplayName ?? "main.ra";
                var module = ModuleManager.LoadModule(node.ModulePath, currentFile, interpreter, context).Result;

                var moduleWrapper = new ModuleWrapperValue(node.Alias, module, context);
                context.SymbolTable.Set(node.Alias, moduleWrapper, isPublic: true);

                return result.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }
            catch (Exception ex)
            {
                return result.Failure(new RuntimeError(posStart, posEnd, $"Import error: {ex.Message}", context));
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
            var symbol = _module.SymbolTable.Get(key);
            
            if (symbol == null)
            {
                return (new NullValue().SetContext(Context).SetPos(posStart, posEnd), new RuntimeError(
                    posStart, posEnd,
                    $"Symbol '{key}' not found in module '{_moduleName}'",
                    _context));
            }

            return (symbol.Copy().SetContext(_context).SetPos(posStart, posEnd), null);
        }

        public override string ToString() => $"<module '{_moduleName}'>";
    }
}

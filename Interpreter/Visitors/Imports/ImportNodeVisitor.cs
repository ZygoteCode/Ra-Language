using RaLanguage.Errors;
using System.Threading.Tasks;
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

        public ValueTask<RuntimeResult> Visit(AstNode node, Context context, IInterpreter interpreter)
            => new ValueTask<RuntimeResult>(Apply(node, context, interpreter));

        // Public static entry-point — shared by the AST visitor and the
        // VM's OP_NATIVE_DEFINE opcode. Avoids interpreter._visitors[]
        // dispatch when running via the VM dispatch loop.
        public static RuntimeResult Apply(AstNode node, Context context, IInterpreter interpreter)
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

        private static RuntimeResult VisitImportAll(ImportAllNode node, Context context, IInterpreter interpreter)
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

            var mergeErr = MergeExtensions(context.Extensions, module.Extensions, node.PositionStart, node.PositionEnd);
            if (mergeErr != null) return result.Failure(mergeErr);

            return result.Success(NullValue.Null
                .SetPos(node.PositionStart, node.PositionEnd)
                .SetContext(context));
        }

        private static RuntimeResult VisitImportSelective(ImportSelectiveNode node, Context context, IInterpreter interpreter)
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

        private static RuntimeResult VisitImportAlias(ImportAliasNode node, Context context, IInterpreter interpreter)
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

        private static Error? MergeExtensions(
            ExtensionRegistry target,
            ExtensionRegistry source,
            Position posStart,
            Position posEnd)
        {
            if (target == null || source == null || ReferenceEquals(target, source)) return null;

            // Sealed cross-module check: if any target is sealed in
            // the importer and the source carries entries on it, the
            // import is rejected upfront. This keeps the seal honest
            // even when modules are loaded out of order — `mod_a`
            // sealing `T` and then `mod_b` (loaded later) trying to
            // ship its own ext on `T` would otherwise silently lose
            // members at merge time. Single error per import; we
            // surface the first conflict with enough context to
            // identify the offending entry.
            foreach (var (kindLabel, sealedConflict) in EnumerateSealedConflicts(target, source))
            {
                return new RuntimeError(posStart, posEnd,
                    $"sealed extension target '{sealedConflict.target}' cannot accept new {kindLabel} '{sealedConflict.name}' from '{sealedConflict.source}'",
                    context: null!,
                    code: Errors.DiagnosticCode.RuntimeGeneric,
                    help: "remove the @sealed marker from the original declaration or move the new extension members into the same module that sealed the target");
            }

            foreach (var kvp in source.AllMethodEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterMethod(
                        kvp.Key,
                        entry.Method,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition);
                }
            }

            foreach (var kvp in source.AllPropertyEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterProperty(
                        kvp.Key,
                        entry.Descriptor,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        out _,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition);
                }
            }

            foreach (var kvp in source.AllOperatorEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterOperator(
                        kvp.Key,
                        entry.Operator,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition);
                }
            }

            foreach (var kvp in source.AllIndexerEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterIndexer(kvp.Key, new ExtensionIndexerEntry(
                        entry.Method,
                        entry.IsSetter,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition));
                }
            }

            foreach (var kvp in source.AllEventEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterEvent(
                        kvp.Key,
                        entry.Descriptor,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        out _,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition);
                }
            }

            foreach (var kvp in source.AllFieldEntries)
            {
                foreach (var entry in kvp.Value)
                {
                    if (!entry.IsEffectivelyPublic) continue;
                    target.RegisterField(
                        kvp.Key,
                        entry.Descriptor,
                        isBlockPublic: entry.IsBlockPublic,
                        isLocal: false,
                        declaringModule: entry.DeclaringModule,
                        out _,
                        targetType: entry.TargetType,
                        sourcePosition: entry.SourcePosition);
                }
            }

            foreach (var key in source.SealedTargets)
                target.MarkSealed(key);

            return null;
        }

        // Walk source's per-kind entry tables and yield any entries
        // whose target is sealed in the importer. Each tuple carries
        // (kind label, target name, member name, source location)
        // so the caller can render a precise error.
        private static IEnumerable<(string kind, (string target, string name, string source) detail)>
            EnumerateSealedConflicts(ExtensionRegistry target, ExtensionRegistry source)
        {
            foreach (var kvp in source.AllMethodEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("method", (kvp.Key, e.Method.VarNameTok?.Value?.ToString() ?? "<anon>", e.FormatSource()));
            foreach (var kvp in source.AllPropertyEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("property", (kvp.Key, e.Descriptor.Name, e.FormatSource()));
            foreach (var kvp in source.AllOperatorEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("operator", (kvp.Key, e.Operator.OperatorTok.Value?.ToString() ?? "<op>", e.FormatSource()));
            foreach (var kvp in source.AllIndexerEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("indexer", (kvp.Key, e.IsSetter ? "op_index_set" : "op_index", e.FormatSource()));
            foreach (var kvp in source.AllEventEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("event", (kvp.Key, e.Descriptor.Name, e.FormatSource()));
            foreach (var kvp in source.AllFieldEntries)
                if (target.IsSealed(kvp.Key))
                    foreach (var e in kvp.Value)
                        if (e.IsEffectivelyPublic)
                            yield return ("field", (kvp.Key, e.Descriptor.Name, e.FormatSource()));
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

using System.Collections.Concurrent;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Vm;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Runtime
{
    // M24: compile-on-demand IR evaluation for AstNode visits invoked from
    // runtime helpers and visitor static Apply chains. Replaces every
    // `interpreter.Visit(node, ctx)` call site with a compile → VM run
    // round-trip — no AST fallback, no `interpreter.Visit` indirection.
    //
    // Two entry points:
    //
    //   Evaluate(...) — expression form. Terminator is OP_HALT; the
    //   result lands in RuntimeResult.Value. Used for evaluating
    //   argument expressions, default-arg expressions, annotation arg
    //   expressions, contract predicates, and sub-expressions inside
    //   visitor Apply helpers.
    //
    //   EvaluateStatement(...) — statement form. Body compiled via the
    //   normal CompileStatementWithFallback path so OP_NATIVE_DEFINE
    //   takes care of long-tail node kinds. Trailing OP_LOAD_NULL + OP_HALT
    //   produces Value=null; explicit `ret X` inside the body emits OP_RET
    //   which preserves FuncReturnValue + FlowState.Return for the caller
    //   to propagate.
    //
    // Both entry points cache the compiled RaFunction per AstNode via a
    // ConcurrentDictionary keyed on the node + a mode discriminator.
    public static class IrExpressionEvaluator
    {
        private static readonly ConcurrentDictionary<(AstNode Node, bool Statement), RaFunction> s_cache = new();

        // M43: drop every cached (AstNode → RaFunction) compile so a
        // hot-restart of the script frees the old AST + IR memory and
        // the next run starts from a clean cache. Called from
        // ExecuteMainFile before each Run() — paired with
        // ImportNodeVisitor.ResetCache + MetadataRegistry.Global.Clear
        // for full reset.
        public static void ClearCache() => s_cache.Clear();

        public static async ValueTask<RuntimeResult> Evaluate(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: false);
            var vm = new VmExecutor(interpreter);
            var frame = new VmFrame(fn);
            return await vm.Execute(frame, context);
        }

        public static RuntimeResult EvaluateBlocking(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: false);
            var vm = new VmExecutor(interpreter);
            var frame = new VmFrame(fn);
            var task = vm.Execute(frame, context);
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }

        public static async ValueTask<RuntimeResult> EvaluateStatement(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: true);
            var vm = new VmExecutor(interpreter);
            var frame = new VmFrame(fn);
            return await vm.Execute(frame, context);
        }

        public static RuntimeResult EvaluateStatementBlocking(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: true);
            var vm = new VmExecutor(interpreter);
            var frame = new VmFrame(fn);
            var task = vm.Execute(frame, context);
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }

        private static RaFunction GetOrCompile(AstNode node, bool statement)
        {
            var key = (node, statement);
            if (s_cache.TryGetValue(key, out var cached)) return cached;
            // M24: statement-only AST node kinds (Return, Break, Continue,
            // Pass, Retry, Throw, …) cannot be lowered through
            // CompileExpression — it has no case for them and would route
            // them via OP_NATIVE_DEFINE, which has no dispatch entry for
            // these kinds (they're control-flow primitives, not visitor-
            // dispatched). Force the statement compile path so the IR
            // compiler emits the dedicated OP_RET / OP_JMP / OP_THROW etc.
            bool forceStatement = statement || IsStatementOnly(node.NodeType);
            var fn = forceStatement
                ? IrCompiler.CompileAsStatement(node, "<stmt>")
                : IrCompiler.CompileAsExpression(node, "<expr>");
            s_cache.TryAdd(key, fn);
            return fn;
        }

        private static bool IsStatementOnly(AstNodeType t)
        {
            switch (t)
            {
                case AstNodeType.Return:
                case AstNodeType.Break:
                case AstNodeType.Continue:
                case AstNodeType.Pass:
                case AstNodeType.Retry:
                case AstNodeType.Throw:
                case AstNodeType.Goto:
                case AstNodeType.Label:
                case AstNodeType.VariableDeclaration:
                case AstNodeType.VariableAssignment:
                case AstNodeType.MemberAssignment:
                case AstNodeType.ListAssignment:
                case AstNodeType.VariableDelete:
                case AstNodeType.ClassDefinition:
                case AstNodeType.StructDefinition:
                case AstNodeType.EnumDefinition:
                case AstNodeType.InterfaceDefinition:
                case AstNodeType.TraitDefinition:
                case AstNodeType.ExtensionDefinition:
                case AstNodeType.OperatorDefinition:
                case AstNodeType.AnnotationDefinition:
                case AstNodeType.NamespaceDeclaration:
                case AstNodeType.UsingNamespace:
                case AstNodeType.ImportAll:
                case AstNodeType.ImportSelective:
                case AstNodeType.ImportAlias:
                case AstNodeType.AsmBlock:
                case AstNodeType.DereferenceAssignment:
                case AstNodeType.For:
                case AstNodeType.While:
                case AstNodeType.DoWhile:
                case AstNodeType.ForEach:
                case AstNodeType.ForAwait:
                case AstNodeType.SuperFor:
                    return true;
                // Note: AnnotationApplication / Match / Switch / Try /
                // TryUnwrap / Pipeline / Borrow / Spawn / Emit / Await /
                // Yield / If / Scope are intentionally NOT statement-only —
                // they all produce a value in expression position and the
                // CompileAsExpression fallback routes them through
                // OP_NATIVE_DEFINE, which writes the visitor result into
                // the scratch slot before OP_HALT returns it.
                default:
                    return false;
            }
        }
    }
}

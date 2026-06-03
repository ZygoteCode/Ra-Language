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

        // PERF: the VmExecutor is stateless apart from the readonly
        // `_interpreter` field (the call-depth counter it uses is
        // [ThreadStatic] on the type, not per-instance). Every sub-expression
        // evaluation funnelled through here used to allocate a fresh executor;
        // since the interpreter is a per-run singleton, one cached executor per
        // thread eliminates that allocation entirely. Rebuilt only on the rare
        // event of the interpreter identity changing on this thread.
        [System.ThreadStatic] private static VmExecutor? s_vm;
        [System.ThreadStatic] private static IInterpreter? s_vmInterp;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static VmExecutor RentExecutor(IInterpreter interpreter)
        {
            var vm = s_vm;
            if (vm != null && ReferenceEquals(s_vmInterp, interpreter)) return vm;
            vm = new VmExecutor(interpreter);
            s_vm = vm;
            s_vmInterp = interpreter;
            return vm;
        }

        // M43: drop every cached (AstNode → RaFunction) compile so a
        // hot-restart of the script frees the old AST + IR memory and
        // the next run starts from a clean cache. Called from
        // ExecuteMainFile before each Run() — paired with
        // ImportNodeVisitor.ResetCache + MetadataRegistry.Global.Clear
        // for full reset.
        public static void ClearCache() => s_cache.Clear();

        // PERF: every entry point rents its frame from the per-RaFunction
        // pool (VmFrame.Rent) instead of `new VmFrame`. The same sub-expression
        // node is re-evaluated on every loop iteration, so its cached RaFunction
        // is rented and returned millions of times — pooling reuses the Slots /
        // SlotLocals arrays across cycles and drops the per-evaluation frame +
        // array allocations to near zero. The frame is returned to the pool only
        // on the success path (no error escape), mirroring VmExecutor.RunScript:
        // error back-traces capture the Parent chain and must keep a live frame.
        // PERF: sync-completion fast path. The overwhelming majority of
        // sub-expression evaluations never hit a real Ra `await`, so
        // vm.Execute returns an already-completed ValueTask. Returning the
        // result directly — instead of `await`-ing it — keeps this method
        // non-async on that path, so the JIT/AOT emits a plain call with no
        // async state-machine setup (builder + MoveNext + awaiter plumbing)
        // per evaluation. Only a genuinely-suspending Execute (Ra `await`)
        // falls through to the async continuation helper.
        public static ValueTask<RuntimeResult> Evaluate(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: false);
            var vm = RentExecutor(interpreter);
            var frame = VmFrame.Rent(fn);
            var task = vm.Execute(frame, context);
            if (task.IsCompletedSuccessfully)
            {
                var res = task.Result;
                if (res.Error == null) VmFrame.Return(frame);
                return new ValueTask<RuntimeResult>(res);
            }
            return AwaitAndReturn(task, frame);
        }

        public static RuntimeResult EvaluateBlocking(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: false);
            var vm = RentExecutor(interpreter);
            var frame = VmFrame.Rent(fn);
            var task = vm.Execute(frame, context);
            var res = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (res.Error == null) VmFrame.Return(frame);
            return res;
        }

        public static ValueTask<RuntimeResult> EvaluateStatement(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: true);
            var vm = RentExecutor(interpreter);
            var frame = VmFrame.Rent(fn);
            var task = vm.Execute(frame, context);
            if (task.IsCompletedSuccessfully)
            {
                var res = task.Result;
                if (res.Error == null) VmFrame.Return(frame);
                return new ValueTask<RuntimeResult>(res);
            }
            return AwaitAndReturn(task, frame);
        }

        // Async continuation for the rare suspending path. Awaits the
        // in-flight Execute, then returns the frame on success — same
        // discipline as the sync path.
        private static async ValueTask<RuntimeResult> AwaitAndReturn(ValueTask<RuntimeResult> task, VmFrame frame)
        {
            var res = await task.ConfigureAwait(false);
            if (res.Error == null) VmFrame.Return(frame);
            return res;
        }

        public static RuntimeResult EvaluateStatementBlocking(AstNode node, Context context, IInterpreter interpreter)
        {
            var fn = GetOrCompile(node, statement: true);
            var vm = RentExecutor(interpreter);
            var frame = VmFrame.Rent(fn);
            var task = vm.Execute(frame, context);
            var res = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (res.Error == null) VmFrame.Return(frame);
            return res;
        }

        // L6: run an ALREADY-compiled RaFunction with the given context (no
        // compile step). Used by the NamespaceDeclaration lowering — the body
        // statements are precompiled ahead of time (via CompileBodyStatement),
        // then executed here with the namespace scope chain as the context.
        // Byte-identical to the on-demand Evaluate(...) path the AST visitor
        // uses, minus the compile + cache lookup.
        public static ValueTask<RuntimeResult> RunCompiled(RaFunction fn, Context context, IInterpreter interpreter)
        {
            var vm = RentExecutor(interpreter);
            var frame = VmFrame.Rent(fn);
            var task = vm.Execute(frame, context);
            if (task.IsCompletedSuccessfully)
            {
                var res = task.Result;
                if (res.Error == null) VmFrame.Return(frame);
                return new ValueTask<RuntimeResult>(res);
            }
            return AwaitAndReturn(task, frame);
        }

        // L6: compile a single statement EXACTLY as Evaluate(statement:false)
        // would (same forceStatement decision), so a precompiled namespace body
        // statement is bytecode-identical to its on-demand compile.
        public static RaFunction CompileBodyStatement(AstNode node)
        {
            bool forceStatement = IsStatementOnly(node.NodeType);
            return forceStatement
                ? IrCompiler.CompileAsStatement(node, "<stmt>")
                : IrCompiler.CompileAsExpression(node, "<expr>");
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
                case AstNodeType.RecordDefinition:
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

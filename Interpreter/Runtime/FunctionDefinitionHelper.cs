using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Interop;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of FunctionDefinitionNodeVisitor. Constructs a
    // FunctionValue (or routes to a native DLL-imported function), wires
    // closure captures + annotations + parameter annotations, and registers
    // the binding in the current symbol table if the function has a name.
    //
    // Called by the AST visitor and by the VM's OP_DEFINE_FUNCTION opcode.
    // Both paths produce a bit-identical RuntimeResult.
    public static class FunctionDefinitionHelper
    {
        public static RuntimeResult Apply(FunctionDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string? funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNames;
            var funcValue = (FunctionValue)new FunctionValue(
                funcName,
                node.BodyNode,
                argNames,
                node.ArgTypes,
                node.IsRefParams,
                node.ParamDefaults,
                node.HasVarArgs,
                node.VarArgNameTok,
                node.VarArgType,
                node.ReturnType,
                node.ShouldAutoReturn,
                node.GenericTypeParams,
                node.WhereConstraints
            )
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            funcValue.IsAsync = node.IsAsync;
            funcValue.IsAsyncStream = node.IsAsyncStream;
            funcValue.FreezeBindingContext(context);

            TryCompileBodyToIr(funcValue, node);

            if (node.CaptureList != null)
            {
                funcValue.CaptureList = node.CaptureList;
                var capErr = funcValue.FreezeCaptures(context);
                if (capErr != null) return res.Failure(capErr);
            }

            if (node.VarNameTok != null && funcName != null)
            {
                context.SymbolTable!.Set(funcName, funcValue, isPublic: node.IsPublic);

                if (node.HasAnnotations)
                {
                    var target = new MetadataTarget(AnnotationTargetKind.Function, null, funcName);
                    funcValue.MetadataKey = target.Key;
                    var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                    if (annErr != null) return res.Failure(annErr);

                    var (nativeFn, dllErr) = DllImportBinder.TryBind(node, funcName, target.Key, context);
                    if (dllErr != null) return res.Failure(dllErr);
                    if (nativeFn != null)
                    {
                        context.SymbolTable.Set(funcName, nativeFn, isPublic: node.IsPublic);
                        var paramErr2 = RegisterParameterAnnotations(node, funcName, context, interpreter);
                        if (paramErr2 != null) return res.Failure(paramErr2);
                        return res.Success(nativeFn);
                    }
                }
                else
                {
                    funcValue.MetadataKey = MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, funcName);
                }

                var paramErr = RegisterParameterAnnotations(node, funcName, context, interpreter);
                if (paramErr != null) return res.Failure(paramErr);
            }
            return res.Success(funcValue);
        }

        // M16/M17: opportunistic IR compile of a function body. Result is
        // cached on the AST node so every caller (top-level functions, class
        // methods, struct methods, trait dispatch, extension methods) reaches
        // the same compiled bytecode without recompiling per call site.
        // Returns the cached RaFunction or null when compilation isn't
        // attempted / failed.
        // M19: telemetry for the IR-compile gate. When IR_DEBUG_FAILED_COMPILES
        // is set, every IrCompileException that would otherwise be swallowed
        // is logged with the function name + frame id + message so we can
        // map remaining AST-fallback usage to the responsible body. Live
        // counter exposed so the test sweep can assert "zero AST fallback".
        public static long IrCompileFailures;
        public static readonly System.Collections.Generic.List<string> IrCompileFailureLog = new();
        private static readonly bool s_logFailures =
            string.Equals(System.Environment.GetEnvironmentVariable("IR_DEBUG_FAILED_COMPILES"), "1", System.StringComparison.Ordinal);

        public static RaFunction? GetOrCompileBody(FunctionDefinitionNode node)
        {
            if (node.IrCompileTried) return node.CompiledBody;
            node.IrCompileTried = true;
            if (node.BodyNode == null) return null;
            if (node.FrameId < 0) return null;
            try
            {
                node.CompiledBody = IrCompiler.CompileFunction(node);
            }
            catch (IrCompileException ex)
            {
                node.CompiledBody = null;
                RecordFailure($"fn {node.VarNameTok?.Value} frame={node.FrameId}: {ex.Message}");
            }
            return node.CompiledBody;
        }

        private static void RecordFailure(string msg)
        {
            System.Threading.Interlocked.Increment(ref IrCompileFailures);
            if (s_logFailures)
            {
                lock (IrCompileFailureLog)
                {
                    if (IrCompileFailureLog.Count < 200) IrCompileFailureLog.Add(msg);
                }
                System.Console.Error.WriteLine($"[ir-compile-fail] {msg}");
            }
        }

        // M18: struct/operator/trait method shapes. Each AST node carries the
        // Resolver-populated FrameId + ParamBindings; CompileMethodShape adapts
        // them to the same IR pipeline FunctionDefinitionNode uses.
        public static RaFunction? GetOrCompileStructMethod(Parser.Nodes.Structs.StructMethodDefinitionNode m)
        {
            if (m.IrCompileTried) return m.CompiledBody;
            m.IrCompileTried = true;
            if (m.BodyNode == null || m.FrameId < 0) return null;
            if (m.IsAsync || m.IsAsyncStream) { } // async still supported via IR + OP_NATIVE_DEFINE
            try
            {
                m.CompiledBody = IrCompiler.CompileMethodShape(
                    name: m.NameTok.Value?.ToString() ?? "<method>",
                    frameId: m.FrameId,
                    arity: m.ArgNameToks.Count,
                    paramBindings: m.ParamBindings,
                    argNameToks: m.ArgNameToks,
                    body: m.BodyNode,
                    shouldAutoReturn: m.ShouldAutoReturn);
            }
            catch (IrCompileException ex) { m.CompiledBody = null; RecordFailure($"struct-method {m.NameTok.Value} frame={m.FrameId}: {ex.Message}"); }
            return m.CompiledBody;
        }

        public static RaFunction? GetOrCompileTraitMethod(Parser.Nodes.Traits.TraitMethodDefinitionNode m)
        {
            if (m.IrCompileTried) return m.CompiledBody;
            m.IrCompileTried = true;
            if (m.BodyNode == null || m.FrameId < 0) return null;
            try
            {
                m.CompiledBody = IrCompiler.CompileMethodShape(
                    name: m.NameTok?.Value?.ToString() ?? "<trait-method>",
                    frameId: m.FrameId,
                    arity: m.ArgNameToks.Count,
                    paramBindings: m.ParamBindings,
                    argNameToks: m.ArgNameToks,
                    body: m.BodyNode,
                    shouldAutoReturn: m.ShouldAutoReturn);
            }
            catch (IrCompileException ex) { m.CompiledBody = null; RecordFailure($"trait-method {m.NameTok?.Value} frame={m.FrameId}: {ex.Message}"); }
            return m.CompiledBody;
        }

        public static RaFunction? GetOrCompileOperator(Parser.Nodes.Classes.OperatorDefinitionNode op)
        {
            if (op.IrCompileTried) return op.CompiledBody;
            op.IrCompileTried = true;
            if (op.BodyNode == null || op.FrameId < 0) return null;
            var argToks = new List<RaLanguage.Lexer.Tokens.Token> { op.ArgNameTok };
            try
            {
                op.CompiledBody = IrCompiler.CompileMethodShape(
                    name: op.OperatorTok.Value?.ToString() ?? "<op>",
                    frameId: op.FrameId,
                    arity: 1,
                    paramBindings: op.ParamBindings,
                    argNameToks: argToks,
                    body: op.BodyNode,
                    shouldAutoReturn: op.ShouldAutoReturn);
            }
            catch (IrCompileException ex) { op.CompiledBody = null; RecordFailure($"operator {op.OperatorTok.Value} frame={op.FrameId}: {ex.Message}"); }
            return op.CompiledBody;
        }

        // L10: compile a PROPERTY accessor body to a RaFunction (run via the VM by
        // PropertyAccessOps). self is implicit; the named args mirror what the
        // accessor context binds — getter `field`; setter/init `value`,`field`;
        // observer `old`,`value`,`field` (same order the resolver framed). Arrow
        // `=> expr` auto-returns; block `{ }` (a ScopeNode) does not.
        public static RaFunction? GetOrCompileAccessor(Parser.Nodes.Properties.PropertyAccessorNode acc)
        {
            if (acc.IrCompileTried) return acc.CompiledBody;
            acc.IrCompileTried = true;
            if (acc.BodyNode == null || acc.FrameId < 0) return null;

            string[] argNames = acc.Kind switch
            {
                Parser.Nodes.Properties.PropertyAccessorKind.Get => new[] { "field" },
                Parser.Nodes.Properties.PropertyAccessorKind.Set => new[] { "value", "field" },
                Parser.Nodes.Properties.PropertyAccessorKind.Init => new[] { "value", "field" },
                Parser.Nodes.Properties.PropertyAccessorKind.Observe => new[] { "old", "value", "field" },
                _ => System.Array.Empty<string>()
            };
            var ps = acc.KindTok.PositionStart;
            var pe = acc.KindTok.PositionEnd;
            var argToks = new List<RaLanguage.Lexer.Tokens.Token>(argNames.Length);
            foreach (var n in argNames)
                argToks.Add(new RaLanguage.Lexer.Tokens.Token(RaLanguage.Lexer.Tokens.TokenType.IDENTIFIER, n, ps, pe));

            bool autoReturn = !(acc.BodyNode is Parser.Nodes.Special.ScopeNode);
            try
            {
                acc.CompiledBody = IrCompiler.CompileMethodShape(
                    name: $"prop_{acc.Kind}",
                    frameId: acc.FrameId,
                    arity: argToks.Count,
                    paramBindings: acc.ParamBindings,
                    argNameToks: argToks,
                    body: acc.BodyNode,
                    shouldAutoReturn: autoReturn);
            }
            catch (IrCompileException ex) { acc.CompiledBody = null; RecordFailure($"prop-accessor {acc.Kind} frame={acc.FrameId}: {ex.Message}"); }
            return acc.CompiledBody;
        }

        private static void TryCompileBodyToIr(FunctionValue funcValue, FunctionDefinitionNode node)
        {
            funcValue.CompiledBody = GetOrCompileBody(node);
        }

        public static Error? RegisterParameterAnnotations(
            FunctionDefinitionNode node,
            string ownerKey,
            Context context,
            IInterpreter interpreter)
        {
            for (int i = 0; i < node.ArgNameToks.Count; i++)
            {
                if (i >= node.ParamAnnotations.Count) break;
                var anns = node.ParamAnnotations[i];
                if (anns == null || anns.Count == 0) continue;
                var paramName = node.ArgNameToks[i].Value?.ToString() ?? "";
                var target = new MetadataTarget(AnnotationTargetKind.Parameter, ownerKey, paramName);
                var err = AnnotationProcessor.Process(anns, target, context, interpreter);
                if (err != null) return err;
            }

            if (node.HasVarArgs && node.VarArgAnnotations != null && node.VarArgAnnotations.Count > 0)
            {
                var paramName = node.VarArgNameTok?.Value?.ToString() ?? "params";
                var target = new MetadataTarget(AnnotationTargetKind.Parameter, ownerKey, paramName);
                var err = AnnotationProcessor.Process(node.VarArgAnnotations, target, context, interpreter);
                if (err != null) return err;
            }

            return null;
        }
    }
}

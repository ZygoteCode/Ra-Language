using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class FunctionValue : BaseFunctionValue
    {
        public AstNode BodyNode { get; }
        // M16: optional pre-compiled body. When non-null, ExecuteBodySync
        // dispatches through VmExecutor instead of recursing back into the
        // AST visitor pipeline. Populated by FunctionDefinitionHelper for
        // non-async, non-arrow-form functions whose body the IR compiler
        // accepted in full (no IrCompileException). Left null for functions
        // the IR can't yet lower; those keep the AST fallback path verbatim.
        public IR.RaFunction? CompiledBody;
        public List<string> ArgNames { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<bool> IsRefParams { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public bool ShouldAutoReturn { get; }
        public List<string> GenericTypeParams { get; } = new List<string>();
        public List<WhereConstraintNode> WhereConstraints { get; } = new List<WhereConstraintNode>();
        public string? MetadataKey { get; set; }
        public bool IsAsync { get; set; }
        public bool IsAsyncStream { get; set; }
        public sealed override RuntimeValueType Type => RuntimeValueType.Function;

        public FunctionValue(
            string name,
            AstNode bodyNode,
            List<string> argNames,
            List<TypeDescriptor?>? argTypes,
            List<bool>? isRefParams,
            List<AstNode?>? paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            bool shouldAutoReturn,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null
        ) : base(name)
        {
            BodyNode = bodyNode;
            ArgNames = argNames ?? new List<string>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            IsRefParams = isRefParams ?? new List<bool>();
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            ShouldAutoReturn = shouldAutoReturn;
            if (genericTypeParams != null) GenericTypeParams = genericTypeParams;
            if (whereConstraints != null) WhereConstraints = whereConstraints;
        }

        public sealed override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            return await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));
        }

        public sealed override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            if (IsAsync || IsAsyncStream)
            {
                return ExecuteAsyncDispatch(positionalArgs, namedArgs, explicitTypeArgs);
            }
            return await ExecuteBodySync(positionalArgs, namedArgs, explicitTypeArgs, null);
        }

        private RuntimeResult ExecuteAsyncDispatch(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            var res = new RuntimeResult();
            var capturedArgs = positionalArgs;
            var capturedNamed = namedArgs;
            var capturedTypeArgs = explicitTypeArgs;
            var callerCtx = Context;
            // The spawn visitor pushes a thread-local override before calling us so
            // that the child fiber inherits the spawn's parent scope rather than the
            // shared (and possibly stale) fn.Context.AsyncCtx. Outside of spawn this
            // is null and behavior is unchanged.
            var overrideAsync = RaLanguage.Interpreter.Runtime.Async.AsyncContextOverride.Current;
            var parentAsync = overrideAsync ?? callerCtx?.AsyncCtx;

            if (IsAsyncStream)
            {
                var stream = new AsyncStreamCore(8, parentAsync?.CancellationScope);
                var streamValue = new AsyncStreamValue(stream).SetContext(callerCtx).SetPos(PositionStart, PositionEnd);
                if (ReturnType != null && !ReturnType.IsTypeParameter())
                {
                    ((AsyncStreamValue)streamValue).ElementType = ReturnType;
                }
                var producer = AsyncScheduler.Schedule($"stream:{Name}", parentAsync, childAsyncCtx =>
                {
                    childAsyncCtx.InsideAsyncStream = true;
                    childAsyncCtx.CurrentStreamProducer = new RaLanguage.Interpreter.Runtime.Async.StreamProducerAdapter(stream, (AsyncStreamValue)streamValue);
                    var streamRes = SyncAwait.Get(ExecuteBodySync(capturedArgs, capturedNamed, capturedTypeArgs, childAsyncCtx));
                    stream.Close();
                    return (streamRes.Value ?? streamRes.FuncReturnValue, streamRes.Error);
                });
                stream.AttachProducer(producer);
                return res.Success(streamValue);
            }

            var task = AsyncScheduler.Schedule($"async:{Name}", parentAsync, childAsyncCtx =>
            {
                childAsyncCtx.InsideAsyncFunction = true;
                var taskRes = SyncAwait.Get(ExecuteBodySync(capturedArgs, capturedNamed, capturedTypeArgs, childAsyncCtx));
                if (taskRes.Error != null) return (null, taskRes.Error);
                var produced = taskRes.FuncReturnValue ?? taskRes.Value;
                return (produced, null);
            });

            var taskValue = new TaskValue(task);
            if (ReturnType != null && !ReturnType.IsTypeParameter())
            {
                taskValue.ElementType = ReturnType;
            }
            return res.Success(taskValue.SetContext(callerCtx).SetPos(PositionStart, PositionEnd));
        }


        private async ValueTask<RuntimeResult> ExecuteBodySync(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs, AsyncContext? asyncCtxOverride)
        {
            var res = new RuntimeResult();
            var bindings = new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);

            if (explicitTypeArgs != null && explicitTypeArgs.Count > 0)
            {
                if (explicitTypeArgs.Count != GenericTypeParams.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"wrong number of type arguments for function '{Name}': expected {GenericTypeParams.Count}, got {explicitTypeArgs.Count}",
                        Context,
                        code: DiagnosticCode.RuntimeTypeMismatch,
                        primaryLabel: $"{explicitTypeArgs.Count} type argument{(explicitTypeArgs.Count == 1 ? "" : "s")} supplied here",
                        help: $"function '{Name}' declares {GenericTypeParams.Count} generic parameter{(GenericTypeParams.Count == 1 ? "" : "s")} ({string.Join(", ", GenericTypeParams)})"));

                for (int i = 0; i < GenericTypeParams.Count; i++)
                {
                    var gname = GenericTypeParams[i];
                    var td = explicitTypeArgs[i] ?? new TypeDescriptor("any");
                    bindings[gname] = td;
                }
            }
            else
            {
                try
                {
                    var formalTypesForInference = new List<TypeDescriptor>();
                    for (int i = 0; i < ArgNames.Count; i++)
                    {
                        TypeDescriptor? ft = (i < ArgTypes.Count) ? ArgTypes[i] : null;
                        if (ft == null) ft = new TypeDescriptor("any");
                        formalTypesForInference.Add(ft);
                    }

                    var inferred = TypeSystem.InferBindingsFromArgs(formalTypesForInference, positionalArgs);
                    if (inferred != null)
                    {
                        foreach (var kv in inferred) bindings[kv.Key] = kv.Value;
                    }

                    if (namedArgs != null && namedArgs.Count > 0)
                    {
                        foreach (var kv in namedArgs)
                        {
                            var name = kv.Key;
                            var val = kv.Value;
                            int idx = ArgNames.IndexOf(name);
                            if (idx >= 0 && idx < ArgTypes.Count && ArgTypes[idx] != null)
                            {
                                var formal = ArgTypes[idx];
                                var actualDesc = TypeSystem.GetDescriptorFromRuntimeValue(val);
                                var sub = TypeSystem.UnifyGenericParameters(formal, actualDesc, new Dictionary<string, TypeDescriptor>(bindings));
                                if (sub != null)
                                {
                                    foreach (var b in sub) bindings[b.Key] = b.Value;
                                }
                            }
                        }
                    }
                }
                catch
                {

                }
            }

            if (GenericTypeParams.Count > 0)
            {
                foreach (var gname in GenericTypeParams)
                {
                    if (!bindings.ContainsKey(gname))
                    {
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                            $"generic parameter '{gname}' of function '{Name}' could not be inferred",
                            Context,
                            code: DiagnosticCode.RuntimeTypeMismatch,
                            primaryLabel: $"type of '{gname}' is unknown at this call site",
                            help: $"supply explicit type arguments, e.g. '{Name}<...>(args)'"));
                    }
                }

                var constraintErr = TypeSystem.ValidateWhereConstraints(bindings, WhereConstraints);
                if (constraintErr != null)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"'where' constraint violated when calling '{Name}': {constraintErr}",
                        Context,
                        code: DiagnosticCode.RuntimeTypeMismatch,
                        primaryLabel: "constraint check failed at this call",
                        help: "review the 'where' clause of the function and the inferred / supplied type arguments"));
            }

            List<TypeDescriptor?> instantiatedArgTypes = null;
            TypeDescriptor? instantiatedVarArgType = null;
            TypeDescriptor? instantiatedReturnType = null;
            try
            {
                if (ArgTypes != null)
                {
                    instantiatedArgTypes = new List<TypeDescriptor?>(ArgTypes.Count);
                    for (int i = 0; i < ArgTypes.Count; i++)
                    {
                        var t = ArgTypes[i];
                        instantiatedArgTypes.Add(t?.SubstituteBindings(bindings));
                    }
                }
                instantiatedVarArgType = VarArgType == null ? null : VarArgType.SubstituteBindings(bindings);
                instantiatedReturnType = ReturnType == null ? null : ReturnType.SubstituteBindings(bindings);
            }
            catch
            {
                instantiatedArgTypes = ArgTypes;
                instantiatedVarArgType = VarArgType;
                instantiatedReturnType = ReturnType;
            }

            var (execCtx, err) = await PrepareExecutionContextForCall(positionalArgs, namedArgs, ArgNames, instantiatedArgTypes, ParamDefaults, HasVarArgs, VarArgNameTok, instantiatedVarArgType);
            if (err != null)
            {
                return res.Failure(err);
            }

            if (asyncCtxOverride != null)
            {
                execCtx!.AsyncCtx = asyncCtxOverride;
            }

            foreach (var kv in bindings)
            {
                var gtv = new GenericTypeValue(kv.Key, kv.Value).SetContext(execCtx).SetPos(PositionStart, PositionEnd);
                execCtx.SymbolTable.Set(kv.Key, gtv, isLet: true, declaredType: new TypeDescriptor("type"), isStaticallyTyped: true, isPublic: false);
            }

            var interpreter = new Interpreter();
            RuntimeResult bodyRes;
            // M19: IR is the only execution path. The function-definition
            // helper has either populated CompiledBody at creation time or
            // surfaced an IrCompileException for the operator — there is no
            // AST-walk fallback. Bodies that are genuinely null (forward
            // declarations / DLL stubs) get rejected here as a runtime
            // error: they have nothing to execute.
            if (CompiledBody == null)
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"function '{Name}' has no executable body", Context));
            }
            {
                // M79: rent from per-function pool. Only return on the
                // success path — error escape captures `Parent` for
                // the traceback chain and must not pool the frame.
                var vm = new Vm.VmExecutor(interpreter);
                var frame = Vm.VmFrame.Rent(CompiledBody);
                bodyRes = await vm.Execute(frame, execCtx!);
                if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
            }
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
            {
                var retVal = bodyRes.FuncReturnValue;
                if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter())
                {
                    if (!TypeSystem.IsAssignable(execCtx, instantiatedReturnType, retVal))
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                            $"return type mismatch in function '{Name}': expected '{instantiatedReturnType}', got '{retVal.Type}'",
                            Context,
                            code: DiagnosticCode.RuntimeTypeMismatch,
                            primaryLabel: $"this 'return' yields '{retVal.Type.ToString().ToLowerInvariant()}'",
                            help: $"either change the return type annotation to '{retVal.Type.ToString().ToLowerInvariant()}' or convert the value to '{instantiatedReturnType}'"));
                }

                var retErr = ValidateReturnValue(retVal, execCtx);
                if (retErr != null) return res.Failure(retErr);

                return res.Success(retVal.SetContext(Context).SetPos(PositionStart, PositionEnd));
            }

            var value = bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
            var retValue = (ShouldAutoReturn ? value : null) ?? value;

            if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter())
            {
                if (!TypeSystem.IsAssignable(execCtx, instantiatedReturnType, retValue))
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in function '{Name}': expected '{instantiatedReturnType}', got '{retValue.Type}'", Context));
            }

            var retErr2 = ValidateReturnValue(retValue, execCtx);
            if (retErr2 != null) return res.Failure(retErr2);

            return res.Success(retValue.SetContext(Context).SetPos(PositionStart, PositionEnd));
        }

        private RaLanguage.Errors.Error? ValidateReturnValue(RuntimeValue value, RaLanguage.Interpreter.Runtime.Context execCtx)
        {
            if (string.IsNullOrEmpty(Name) || Name == "<anonymous>") return null;
            var key = MetadataTarget.BuildKey(AnnotationTargetKind.Return, null, Name);
            var verr = AnnotationValidator.ValidateTarget(key, value, $"return of '{Name}'", execCtx);
            if (verr != null) return verr;

            var fnKey = MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, Name);
            return ContractEvaluator.CheckPostconditions(fnKey, execCtx, value);
        }

        public sealed override RuntimeValue Copy()
        {
            var clone = new FunctionValue(
                Name,
                BodyNode,
                ArgNames,
                ArgTypes == null ? null : ArgTypes,
                IsRefParams,
                ParamDefaults == null ? null : ParamDefaults,
                HasVarArgs,
                VarArgNameTok,
                VarArgType,
                ReturnType,
                ShouldAutoReturn,
                GenericTypeParams,
                WhereConstraints
            ).SetContext(Context).SetPos(PositionStart, PositionEnd);
            ((FunctionValue)clone).MetadataKey = MetadataKey;
            return clone;
        }

        public sealed override string ToString() => $"<function {Name}>";
    }
}

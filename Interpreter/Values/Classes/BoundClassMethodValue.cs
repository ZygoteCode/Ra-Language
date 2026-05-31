using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class BoundClassMethodValue : BaseFunctionValue
    {
        public ClassTypeValue Definition { get; }
        public ClassInstanceValue SelfInstance { get; }
        public FunctionDefinitionNode MethodNode { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public bool IsStatic { get; }

        public BoundClassMethodValue(ClassTypeValue definition, ClassInstanceValue selfInstance, FunctionDefinitionNode methodNode, bool isStatic)
            : base(methodNode.VarNameTok?.Value?.ToString() ?? "<method>")
        {
            Definition = definition;
            SelfInstance = selfInstance;
            MethodNode = methodNode;
            IsStatic = isStatic;
        }

        protected override string? ParameterOwnerForMetadata
            => $"{Definition.ClassName}.{MethodNode.VarNameTok?.Value?.ToString() ?? "<method>"}";

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            return await ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            if (MethodNode.IsAsync || MethodNode.IsAsyncStream)
            {
                var capturedPositional = positionalArgs;
                var capturedNamed = namedArgs;
                var capturedTypeArgs = explicitTypeArgs;
                return AsyncMethodDispatch.Dispatch(
                    MethodNode.IsAsync,
                    MethodNode.IsAsyncStream,
                    Name,
                    Context,
                    PositionStart,
                    PositionEnd,
                    asyncCtxOverride => SyncAwait.Get(ExecuteSyncBody(capturedPositional, capturedNamed, capturedTypeArgs, asyncCtxOverride)));
            }
            return await ExecuteSyncBody(positionalArgs, namedArgs, explicitTypeArgs, null);
        }

        private async ValueTask<RuntimeResult> ExecuteSyncBody(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs, AsyncContext? asyncCtxOverride)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            // PERF: the execution scope is built once by
            // PrepareExecutionContextForCall below (bindRes.execCtx). The
            // previous code first allocated a SEPARATE GenerateNewContext()
            // here, bound `self` / owner / ctor / async into it, and then threw
            // it away — every consumer below uses bindRes.execCtx, which
            // re-applies all of those. That was a full Context + SymbolTable +
            // backing dictionary + a `self` SymbolEntry discarded on every
            // method call. Removed; self / owner / ctor / async are applied to
            // bindRes.execCtx after the bind (unchanged).

            // PERF: a non-generic method on a non-generic receiver needs no
            // binding map and no type-argument substitution — the declared
            // arg / vararg / return types ARE the instantiated types. Skip the
            // bindings dictionary + the instantiatedArgTypes list +
            // SubstituteBindings per formal on every ordinary method call. The
            // generic branch below is the original logic verbatim.
            bool methodIsGeneric = MethodNode.GenericTypeParams.Count > 0
                || (SelfInstance?.GenericBindings != null && SelfInstance.GenericBindings.Count > 0);

            var argNames = MethodNode.ArgNames;
            Dictionary<string, TypeDescriptor>? bindings = null;
            List<TypeDescriptor?> instantiatedArgTypes;
            TypeDescriptor? instantiatedVarArgType;
            TypeDescriptor? instantiatedReturnType;

            if (!methodIsGeneric)
            {
                instantiatedArgTypes = MethodNode.ArgTypes;
                instantiatedVarArgType = MethodNode.VarArgType;
                instantiatedReturnType = MethodNode.ReturnType;
            }
            else
            {
                bindings = new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);
                if (SelfInstance != null && SelfInstance.GenericBindings != null)
                {
                    foreach (var kv in SelfInstance.GenericBindings)
                        bindings[kv.Key] = kv.Value;
                }

                if (MethodNode.GenericTypeParams.Count > 0)
                {
                    if (explicitTypeArgs != null && explicitTypeArgs.Count > 0)
                    {
                        if (explicitTypeArgs.Count != MethodNode.GenericTypeParams.Count)
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Wrong number of type arguments for method '{Name}': expected {MethodNode.GenericTypeParams.Count}, got {explicitTypeArgs.Count}", Context));

                        for (int i = 0; i < MethodNode.GenericTypeParams.Count; i++)
                        {
                            var td = explicitTypeArgs[i] ?? new TypeDescriptor("any");
                            bindings[MethodNode.GenericTypeParams[i]] = td;
                        }
                    }
                    else
                    {
                        var formalTypesForInference = new List<TypeDescriptor>();
                        for (int i = 0; i < MethodNode.ArgNameToks.Count; i++)
                        {
                            TypeDescriptor? ft = (i < MethodNode.ArgTypes.Count) ? MethodNode.ArgTypes[i] : null;
                            if (ft == null) ft = new TypeDescriptor("any");
                            formalTypesForInference.Add(ft);
                        }

                        var inferred = TypeSystem.InferBindingsFromArgs(formalTypesForInference, positionalArgs);
                        if (inferred != null)
                        {
                            foreach (var kv in inferred) bindings[kv.Key] = kv.Value;
                        }
                    }

                    var constraintErr = TypeSystem.ValidateWhereConstraints(bindings, MethodNode.WhereConstraints);
                    if (constraintErr != null)
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Where-constraint violated in method '{Name}': {constraintErr}", Context));
                }

                instantiatedArgTypes = new List<TypeDescriptor?>(MethodNode.ArgTypes.Count);
                for (int i = 0; i < MethodNode.ArgTypes.Count; i++)
                {
                    var t = MethodNode.ArgTypes[i];
                    instantiatedArgTypes.Add(t?.SubstituteBindings(bindings));
                }
                instantiatedVarArgType = MethodNode.VarArgType == null ? null : MethodNode.VarArgType.SubstituteBindings(bindings);
                instantiatedReturnType = MethodNode.ReturnType == null ? null : MethodNode.ReturnType.SubstituteBindings(bindings);
            }

            var compiled = Runtime.FunctionDefinitionHelper.GetOrCompileBody(MethodNode);

            // PERF: direct-slot method dispatch — the OOP twin of the function
            // fast path. Bind `self` into frame slot 0 (Resolver-reserved; read
            // by name via OP_LOAD_GLOBAL "self") and each argument into its
            // ParamSlots offset, run with a frame-backed scope, and skip the
            // per-call SymbolTable dictionary + first-read lookups +
            // PrepareExecutionContextForCall. Gated to leaf, synchronous,
            // non-generic, non-constructor instance methods with no nested
            // closures (Children + FuncDefRefs empty ⇒ no closure can capture
            // self / a parameter and outlive the pooled frame), exact positional
            // arity, and no annotations. The [self]+args name/slot arrays are
            // cached on the compiled body (built once).
            if (!methodIsGeneric
                && compiled != null
                && !IsStatic
                && SelfInstance != null
                && !MethodNode.IsConstructor
                && !MethodNode.IsAsync && !MethodNode.IsAsyncStream
                && compiled.Children.Length == 0
                && compiled.FuncDefRefs.Length == 0
                && !MethodNode.HasVarArgs
                && (namedArgs == null || namedArgs.Count == 0)
                && argNames.Count == positionalArgs.Count
                && compiled.ParamSlots.Length == argNames.Count
                && MetadataRegistry.Global.IsEmpty)
            {
                var pslots = compiled.ParamSlots;
                bool eligible = true;
                for (int i = 0; i < pslots.Length; i++) { if (pslots[i] < 0) { eligible = false; break; } }
                if (eligible)
                {
                    var fbNames = compiled._methodFrameNames;
                    var fbSlots = compiled._methodFrameSlots;
                    if (fbNames == null || fbSlots == null)
                    {
                        var nm = new string[pslots.Length + 1];
                        var sl = new int[pslots.Length + 1];
                        nm[0] = "self"; sl[0] = 0;
                        for (int i = 0; i < pslots.Length; i++) { nm[i + 1] = argNames[i]; sl[i + 1] = pslots[i]; }
                        compiled._methodFrameNames = nm;
                        compiled._methodFrameSlots = sl;
                        fbNames = nm; fbSlots = sl;
                    }

                    var execCtxF = GenerateNewContext();
                    execCtxF.CurrentClassMethodOwner = Definition;
                    if (asyncCtxOverride != null) execCtxF.AsyncCtx = asyncCtxOverride;
                    var frameF = Vm.VmFrame.Rent(compiled);
                    var slotLocalsF = frameF.SlotLocals;
                    if (slotLocalsF.Length > 0)
                        slotLocalsF[0] = new RaLanguage.Interpreter.Runtime.SymbolEntry(SelfInstance, true, false, Definition.SelfTypeDescriptor, true, RaLanguage.Parser.Nodes.Variables.VariableDeclarationType.VARIABLE);
                    for (int i = 0; i < pslots.Length; i++)
                    {
                        var v = positionalArgs[i];
                        v.SetContext(execCtxF);
                        var expectedT = (instantiatedArgTypes != null && i < instantiatedArgTypes.Count) ? instantiatedArgTypes[i] : null;
                        if (expectedT != null && !expectedT.IsTypeParameter() && !TypeSystem.IsAssignable(execCtxF, expectedT, v))
                        {
                            Vm.VmFrame.Return(frameF);
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for argument '{argNames[i]}': expected '{expectedT}', got '{v.Type}'", Context));
                        }
                        int slot = pslots[i];
                        if ((uint)slot < (uint)slotLocalsF.Length)
                            slotLocalsF[slot] = new RaLanguage.Interpreter.Runtime.SymbolEntry(v, false, true, null, false, RaLanguage.Parser.Nodes.Variables.VariableDeclarationType.VARIABLE);
                    }
                    execCtxF.SymbolTable.AttachFrameParams(fbNames, fbSlots, slotLocalsF);

                    var interpreterF = new Interpreter();
                    var vmF = new Vm.VmExecutor(interpreterF);
                    var bodyResF = await vmF.Execute(frameF, execCtxF);
                    if (bodyResF.Error == null) Vm.VmFrame.Return(frameF);
                    if (bodyResF.Error != null) return res.Failure(bodyResF.Error);

                    if (bodyResF.FuncReturnValue != null)
                    {
                        var retValF = bodyResF.FuncReturnValue;
                        if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter()
                            && !TypeSystem.IsAssignable(execCtxF, instantiatedReturnType, retValF))
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}': expected '{instantiatedReturnType}', got '{retValF.Type}'", Context));
                        return res.Success(retValF);
                    }
                    var retValueF = MethodNode.ShouldAutoReturn
                        ? (bodyResF.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                        : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
                    if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter()
                        && !TypeSystem.IsAssignable(execCtxF, instantiatedReturnType, retValueF))
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}': expected '{instantiatedReturnType}', got '{retValueF.Type}'", Context));
                    return res.Success(retValueF);
                }
            }

            var bindRes = await PrepareExecutionContextForCall(
                positionalArgs,
                namedArgs,
                argNames,
                instantiatedArgTypes,
                MethodNode.ParamDefaults,
                MethodNode.HasVarArgs,
                MethodNode.VarArgNameTok,
                instantiatedVarArgType);

            if (bindRes.error != null)
                return res.Failure(bindRes.error);

            if (!IsStatic && SelfInstance != null)
            {
                bindRes.execCtx.SymbolTable.Set(
                    "self",
                    SelfInstance,
                    isLet: true,
                    declaredType: Definition.SelfTypeDescriptor,
                    isStaticallyTyped: true,
                    isPublic: false
                );
            }

            bindRes.execCtx!.CurrentClassMethodOwner = Definition;

            if (MethodNode.IsConstructor)
            {
                bindRes.execCtx!.IsInConstructor = true;
            }

            if (bindings != null)
            {
                foreach (var kv in bindings)
                {
                    var gtv = new Primitives.GenericTypeValue(kv.Key, kv.Value).SetContext(bindRes.execCtx).SetPos(PositionStart, PositionEnd);
                    bindRes.execCtx.SymbolTable.Set(kv.Key, gtv, isLet: true, declaredType: new TypeDescriptor("type"), isStaticallyTyped: true, isPublic: false);
                }
            }

            if (asyncCtxOverride != null)
            {
                bindRes.execCtx!.AsyncCtx = asyncCtxOverride;
            }

            RuntimeResult bodyRes;
            if (compiled == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"class method '{Name}' has no executable body", Context));
            {
                // M79: pool rent + return on success only.
                var vm = new Vm.VmExecutor(interpreter);
                var frame = Vm.VmFrame.Rent(compiled);
                bodyRes = await vm.Execute(frame, bindRes.execCtx!);
                if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
            }
            if (bodyRes.Error != null)
                return res.Failure(bodyRes.Error);

            if (MethodNode.IsConstructor && bodyRes.FuncReturnValue != null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, "Constructors cannot return a value", Context));

            if (bodyRes.FuncReturnValue != null)
            {
                if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter() &&
                    !TypeSystem.IsAssignable(bindRes.execCtx!, instantiatedReturnType, bodyRes.FuncReturnValue))
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}': expected '{instantiatedReturnType}', got '{bodyRes.FuncReturnValue.Type}'", Context));
                }

                var retErr = ValidateReturn(bodyRes.FuncReturnValue, bindRes.execCtx!);
                if (retErr != null) return res.Failure(retErr);

                return res.Success(bodyRes.FuncReturnValue);
            }

            var retValue = MethodNode.ShouldAutoReturn
                ? (bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter() &&
                !TypeSystem.IsAssignable(bindRes.execCtx!, instantiatedReturnType, retValue))
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}': expected '{instantiatedReturnType}', got '{retValue.Type}'", Context));
            }

            var retErr2 = ValidateReturn(retValue, bindRes.execCtx!);
            if (retErr2 != null) return res.Failure(retErr2);

            return res.Success(retValue);
        }

        private RaLanguage.Errors.Error? ValidateReturn(RuntimeValue value, RaLanguage.Interpreter.Runtime.Context execCtx)
        {
            // PERF: no @returns validator / @ensures / @invariant can fire when
            // nothing was ever registered — skip the three BuildKey string
            // allocations + registry probes on every method return.
            if (MetadataRegistry.Global.IsEmpty) return null;
            var methodName = MethodNode.VarNameTok?.Value?.ToString() ?? Name;
            var key = MetadataTarget.BuildKey(AnnotationTargetKind.Return, Definition.ClassName, methodName);
            var verr = AnnotationValidator.ValidateTarget(key, value, $"return of '{Definition.ClassName}.{Name}'", execCtx);
            if (verr != null) return verr;

            var methodKey = MetadataTarget.BuildKey(
                MethodNode.IsConstructor ? AnnotationTargetKind.Constructor : AnnotationTargetKind.Method,
                Definition.ClassName,
                methodName);
            var postErr = ContractEvaluator.CheckPostconditions(methodKey, execCtx, value);
            if (postErr != null) return postErr;

            var classKey = MetadataTarget.BuildKey(AnnotationTargetKind.Class, null, Definition.ClassName);
            var invErr = ContractEvaluator.CheckInvariants(classKey, execCtx);
            if (invErr != null) return invErr;

            return null;
        }

        public override RuntimeValue Copy()
            => new BoundClassMethodValue(Definition, SelfInstance, MethodNode, IsStatic)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<bound method {Definition.ClassName}.{Name}>";
    }
}
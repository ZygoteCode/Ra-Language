using RaLanguage.Errors.Types;
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

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            return ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
        }

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
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
                    asyncCtxOverride => ExecuteSyncBody(capturedPositional, capturedNamed, capturedTypeArgs, asyncCtxOverride));
            }
            return ExecuteSyncBody(positionalArgs, namedArgs, explicitTypeArgs, null);
        }

        private RuntimeResult ExecuteSyncBody(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs, AsyncContext? asyncCtxOverride)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var execCtx = GenerateNewContext();
            if (asyncCtxOverride != null) execCtx.AsyncCtx = asyncCtxOverride;

            execCtx.CurrentClassMethodOwner = Definition;

            if (!IsStatic && SelfInstance != null)
            {
                execCtx.SymbolTable.Set(
                    "self",
                    SelfInstance,
                    isLet: true,
                    declaredType: new TypeDescriptor(Definition.ClassName),
                    isStaticallyTyped: true,
                    isPublic: false);
            }

            if (MethodNode.IsConstructor)
            {
                execCtx.IsInConstructor = true;
            }

            var bindings = new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);
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

            var argNames = MethodNode.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();

            var instantiatedArgTypes = MethodNode.ArgTypes.Select(t => t == null ? null : t.SubstituteBindings(bindings)).ToList();
            var instantiatedVarArgType = MethodNode.VarArgType == null ? null : MethodNode.VarArgType.SubstituteBindings(bindings);
            var instantiatedReturnType = MethodNode.ReturnType == null ? null : MethodNode.ReturnType.SubstituteBindings(bindings);

            var bindRes = PrepareExecutionContextForCall(
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
                    declaredType: new TypeDescriptor(Definition.ClassName),
                    isStaticallyTyped: true,
                    isPublic: false
                );
            }

            bindRes.execCtx!.CurrentClassMethodOwner = Definition;

            if (MethodNode.IsConstructor)
            {
                bindRes.execCtx!.IsInConstructor = true;
            }

            foreach (var kv in bindings)
            {
                var gtv = new Primitives.GenericTypeValue(kv.Key, kv.Value).SetContext(bindRes.execCtx).SetPos(PositionStart, PositionEnd);
                bindRes.execCtx.SymbolTable.Set(kv.Key, gtv, isLet: true, declaredType: new TypeDescriptor("type"), isStaticallyTyped: true, isPublic: false);
            }

            if (asyncCtxOverride != null)
            {
                bindRes.execCtx!.AsyncCtx = asyncCtxOverride;
            }

            var bodyRes = interpreter.Visit(MethodNode.BodyNode, bindRes.execCtx!);
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
                ? (bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd))
                : new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

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
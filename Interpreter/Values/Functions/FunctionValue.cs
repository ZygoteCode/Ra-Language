using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
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

        public sealed override RuntimeResult Execute(List<RuntimeValue> args)
        {
            return ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));
        }

        public sealed override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            var res = new RuntimeResult();
            var bindings = new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);

            if (explicitTypeArgs != null && explicitTypeArgs.Count > 0)
            {
                if (explicitTypeArgs.Count != GenericTypeParams.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Wrong number of type arguments for function '{Name}': expected {GenericTypeParams.Count}, got {explicitTypeArgs.Count}", Context));

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
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Generic parameter '{gname}' of function '{Name}' could not be resolved; provide explicit type arguments", Context));
                    }
                }

                var constraintErr = TypeSystem.ValidateWhereConstraints(bindings, WhereConstraints);
                if (constraintErr != null)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Where-constraint violated in function '{Name}': {constraintErr}", Context));
            }

            List<TypeDescriptor?> instantiatedArgTypes = null;
            TypeDescriptor? instantiatedVarArgType = null;
            TypeDescriptor? instantiatedReturnType = null;
            try
            {
                instantiatedArgTypes = ArgTypes?.Select(t => t == null ? null : t.SubstituteBindings(bindings)).ToList();
                instantiatedVarArgType = VarArgType == null ? null : VarArgType.SubstituteBindings(bindings);
                instantiatedReturnType = ReturnType == null ? null : ReturnType.SubstituteBindings(bindings);
            }
            catch
            {
                instantiatedArgTypes = ArgTypes;
                instantiatedVarArgType = VarArgType;
                instantiatedReturnType = ReturnType;
            }

            var (execCtx, err) = PrepareExecutionContextForCall(positionalArgs, namedArgs, ArgNames, instantiatedArgTypes, ParamDefaults, HasVarArgs, VarArgNameTok, instantiatedVarArgType);
            if (err != null)
            {
                return res.Failure(err);
            }

            foreach (var kv in bindings)
            {
                var gtv = new GenericTypeValue(kv.Key, kv.Value).SetContext(execCtx).SetPos(PositionStart, PositionEnd);
                execCtx.SymbolTable.Set(kv.Key, gtv, isLet: true, declaredType: new TypeDescriptor("type"), isStaticallyTyped: true, isPublic: false);
            }

            var interpreter = new Interpreter();
            var bodyRes = interpreter.Visit(BodyNode, execCtx!);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
            {
                var retVal = bodyRes.FuncReturnValue;
                if (instantiatedReturnType != null && !instantiatedReturnType.IsTypeParameter())
                {
                    if (!TypeSystem.IsAssignable(execCtx, instantiatedReturnType, retVal))
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in function '{Name}': expected '{instantiatedReturnType}', got '{retVal.Type}'", Context));
                }

                var retErr = ValidateReturnValue(retVal, execCtx);
                if (retErr != null) return res.Failure(retErr);

                return res.Success(retVal.SetContext(Context).SetPos(PositionStart, PositionEnd));
            }

            var value = bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);
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

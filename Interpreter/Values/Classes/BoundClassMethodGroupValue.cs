using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class BoundClassMethodGroupValue : BaseFunctionValue
    {
        public ClassTypeValue Definition { get; }
        public ClassInstanceValue SelfInstance { get; }
        public List<FunctionDefinitionNode> Candidates { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundClassMethodGroupValue(
            ClassTypeValue definition,
            ClassInstanceValue selfInstance,
            List<FunctionDefinitionNode> candidates
        ) : base(candidates.Count > 0
                ? (candidates[0].VarNameTok?.Value?.ToString() ?? "<method>")
                : "<method>")
        {
            Definition = definition;
            SelfInstance = selfInstance;
            Candidates = candidates ?? new List<FunctionDefinitionNode>();
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        // M28.2 exposes the overload-selection step so OP_CALL can prime its
        // per-PC inline cache with the resolved FunctionDefinitionNode. Returns
        // null when no candidate matches; callers fall through to the normal
        // `ExecuteWithNamedArgs` failure path. Pure read — does not execute
        // any body, does not mutate Context or SelfInstance.
        public FunctionDefinitionNode? PickOverload(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            // Single-candidate fast path. Skip the LINQ FirstOrDefault and the
            // per-call HashSet allocation inside CanBindSignature.
            if (Candidates.Count == 1)
            {
                var only = Candidates[0];
                if (only == null || only.IsAbstract || only.BodyNode == null) return null;
                return CanBindSignature(only, positionalArgs, namedArgs, Context) ? only : null;
            }
            return Candidates.FirstOrDefault(c =>
                c != null &&
                !c.IsAbstract &&
                c.BodyNode != null &&
                CanBindSignature(c, positionalArgs, namedArgs, Context));
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var selected = PickOverload(positionalArgs, namedArgs);

            if (selected == null)
            {
                return res.Failure(new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"No matching overload found for '{Name}'",
                    Context));
            }

            // Async methods route through BoundClassMethodValue, which knows how
            // to wrap the body in a scheduler-bound fiber and return a TaskValue.
            // We could duplicate the logic here, but delegating keeps a single
            // execution path for both sync and async methods.
            if (selected.IsAsync || selected.IsAsyncStream)
            {
                var bound = new BoundClassMethodValue(Definition, SelfInstance, selected, isStatic: false)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);
                return await ((BoundClassMethodValue)bound).ExecuteWithNamedArgs(positionalArgs, namedArgs);
            }

            var execCtx = GenerateNewContext();

            execCtx.CurrentClassMethodOwner = Definition;

            execCtx.SymbolTable.Set(
                "self",
                SelfInstance,
                isLet: true,
                declaredType: new TypeDescriptor(Definition.ClassName),
                isStaticallyTyped: true,
                isPublic: false);

            var bindError = await BindArgumentsIntoContext(
                selected,
                execCtx,
                positionalArgs,
                namedArgs);

            if (bindError != null)
            {
                return res.Failure(bindError);
            }

            // NullValue.SetContext is a sealed no-op (NullValue is a true singleton),
            // so execCtx would always be null. Pass execCtx directly.
            RuntimeResult bodyRes;
            var compiled = selected is RaLanguage.Parser.Nodes.Functions.FunctionDefinitionNode fdn
                ? Runtime.FunctionDefinitionHelper.GetOrCompileBody(fdn)
                : null;
            if (compiled == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"overloaded method '{Name}' has no IR-compiled body", Context));
            {
                // M79: pool rent + return on success only.
                var vm = new Vm.VmExecutor(interpreter);
                var frame = Vm.VmFrame.Rent(compiled);
                bodyRes = await vm.Execute(frame, execCtx);
                if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
            }
            if (bodyRes.Error != null)
            {
                return res.Failure(bodyRes.Error);
            }

            if (bodyRes.FuncReturnValue != null)
            {
                if (selected.ReturnType != null && !TypeSystem.IsAssignable(execCtx, selected.ReturnType, bodyRes.FuncReturnValue))
                {
                    return res.Failure(new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Return type mismatch in method '{Name}'",
                        Context));
                }

                var retErr = ValidateReturn(selected, bodyRes.FuncReturnValue, execCtx);
                if (retErr != null) return res.Failure(retErr);

                return res.Success(bodyRes.FuncReturnValue);
            }

            var retValue = selected.ShouldAutoReturn
                ? (bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (selected.ReturnType != null && !TypeSystem.IsAssignable(execCtx, selected.ReturnType, retValue))
            {
                return res.Failure(new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"Return type mismatch in method '{Name}'",
                    Context));
            }

            var retErr2 = ValidateReturn(selected, retValue, execCtx);
            if (retErr2 != null) return res.Failure(retErr2);

            return res.Success(retValue);
        }

        private RaLanguage.Errors.Error? ValidateReturn(FunctionDefinitionNode selected, RuntimeValue value, Context execCtx)
        {
            var methodName = selected.VarNameTok?.Value?.ToString() ?? Name;
            var key = MetadataTarget.BuildKey(AnnotationTargetKind.Return, Definition.ClassName, methodName);
            var verr = AnnotationValidator.ValidateTarget(key, value, $"return of '{Definition.ClassName}.{methodName}'", execCtx);
            if (verr != null) return verr;

            var methodKey = MetadataTarget.BuildKey(
                selected.IsConstructor ? AnnotationTargetKind.Constructor : AnnotationTargetKind.Method,
                Definition.ClassName,
                methodName);
            var postErr = ContractEvaluator.CheckPostconditions(methodKey, execCtx, value);
            if (postErr != null) return postErr;

            var classKey = MetadataTarget.BuildKey(AnnotationTargetKind.Class, null, Definition.ClassName);
            var invErr = ContractEvaluator.CheckInvariants(classKey, execCtx);
            if (invErr != null) return invErr;

            return null;
        }

        private bool CanBindSignature(
            FunctionDefinitionNode method,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            Context context)
        {
            var argNames = method.ArgNames;
            int formalCount = argNames.Count;

            if (!method.HasVarArgs && positionalArgs.Count > formalCount)
                return false;

            var assigned = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < positionalArgs.Count && i < formalCount; i++)
            {
                assigned.Add(argNames[i]);

                var expected = i < method.ArgTypes.Count ? method.ArgTypes[i] : null;
                if (expected != null && !TypeSystem.IsAssignable(context, expected, positionalArgs[i]))
                    return false;
            }

            foreach (var kv in namedArgs)
            {
                if (method.HasVarArgs && method.VarArgNameTok != null &&
                    string.Equals(kv.Key, method.VarArgNameTok.Value.ToString(), StringComparison.Ordinal))
                {
                    if (kv.Value.Type != RuntimeValueType.List)
                        return false;

                    if (method.VarArgType != null)
                    {
                        var list = (ListValue)kv.Value;
                        foreach (var el in list.Elements)
                        {
                            if (!TypeSystem.IsAssignable(context, method.VarArgType, el))
                                return false;
                        }
                    }

                    continue;
                }

                if (!argNames.Contains(kv.Key, StringComparer.Ordinal))
                    return false;

                if (!assigned.Add(kv.Key))
                    return false;

                int index = argNames.IndexOf(kv.Key);
                var expected = index >= 0 && index < method.ArgTypes.Count ? method.ArgTypes[index] : null;
                if (expected != null && !TypeSystem.IsAssignable(context, expected, kv.Value))
                    return false;
            }

            for (int i = 0; i < formalCount; i++)
            {
                var name = argNames[i];
                if (assigned.Contains(name))
                    continue;

                if (i >= method.ParamDefaults.Count || method.ParamDefaults[i] == null)
                    return false;
            }

            if (method.HasVarArgs && method.VarArgType != null)
            {
                for (int i = formalCount; i < positionalArgs.Count; i++)
                {
                    if (!TypeSystem.IsAssignable(context, method.VarArgType, positionalArgs[i]))
                        return false;
                }
            }

            return true;
        }

        private async ValueTask<RuntimeError?> BindArgumentsIntoContext(
            FunctionDefinitionNode method,
            Context execCtx,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            var interpreter = new Interpreter();

            var argNames = method.ArgNames;
            var finalAssigned = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            var extras = new List<RuntimeValue>();

            int formalCount = argNames.Count;

            for (int i = 0; i < positionalArgs.Count; i++)
            {
                if (i < formalCount)
                {
                    var name = argNames[i];

                    if (finalAssigned.ContainsKey(name))
                    {
                        return new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"Argument for parameter '{name}' provided multiple times",
                            Context);
                    }

                    finalAssigned[name] = positionalArgs[i];
                }
                else
                {
                    if (!method.HasVarArgs)
                    {
                        return new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"{positionalArgs.Count - formalCount} too many args passed into {Name}",
                            Context);
                    }

                    extras.Add(positionalArgs[i]);
                }
            }

            foreach (var kv in namedArgs)
            {
                if (method.HasVarArgs && method.VarArgNameTok != null &&
                    string.Equals(kv.Key, method.VarArgNameTok.Value.ToString(), StringComparison.Ordinal))
                {
                    if (kv.Value.Type != RuntimeValueType.List)
                    {
                        return new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"Variadic named argument '{kv.Key}' must be a list",
                            Context);
                    }

                    extras.AddRange(((ListValue)kv.Value).Elements);
                    continue;
                }

                if (!argNames.Contains(kv.Key, StringComparer.Ordinal))
                {
                    return new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Unknown named argument '{kv.Key}'",
                        Context);
                }

                if (finalAssigned.ContainsKey(kv.Key))
                {
                    return new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Argument for parameter '{kv.Key}' provided multiple times",
                        Context);
                }

                finalAssigned[kv.Key] = kv.Value;
            }

            for (int i = 0; i < formalCount; i++)
            {
                var name = argNames[i];
                if (finalAssigned.ContainsKey(name))
                    continue;

                AstNode? defAst = i < method.ParamDefaults.Count ? method.ParamDefaults[i] : null;
                if (defAst == null)
                {
                    return new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Missing required argument '{name}' for method '{Name}'",
                        Context);
                }

                var defRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(defAst, execCtx, interpreter);
                if (defRes.Error != null)
                    return (RuntimeError) defRes.Error;

                finalAssigned[name] = defRes.Value ?? NullValue.Null.SetContext(execCtx).SetPos(defAst.PositionStart, defAst.PositionEnd);
            }

            for (int i = 0; i < formalCount; i++)
            {
                var name = argNames[i];
                var actual = finalAssigned[name];
                var expected = i < method.ArgTypes.Count ? method.ArgTypes[i] : null;

                if (expected != null && !TypeSystem.IsAssignable(execCtx, expected, actual))
                {
                    return new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Type mismatch for argument '{name}'",
                        Context);
                }
            }

            if (method.HasVarArgs && method.VarArgType != null)
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    if (!TypeSystem.IsAssignable(execCtx, method.VarArgType, extras[i]))
                    {
                        return new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"Type mismatch for variadic argument '{method.VarArgNameTok?.Value?.ToString() ?? "params"}'",
                            Context);
                    }
                }
            }

            foreach (var kv in finalAssigned)
            {
                var value = kv.Value;
                value.SetContext(execCtx);
                execCtx.SymbolTable.Set(kv.Key, value);
            }

            if (method.HasVarArgs)
            {
                var varArgList = new ListValue(extras)
                    .SetContext(execCtx)
                    .SetPos(PositionStart, PositionEnd);

                execCtx.SymbolTable.Set(method.VarArgNameTok?.Value?.ToString() ?? "params", varArgList);
            }

            {
                var owner = $"{Definition.ClassName}.{method.VarNameTok?.Value?.ToString() ?? Name}";
                var keys = new List<string>(finalAssigned.Keys);
                foreach (var k in keys)
                {
                    var paramKey = MetadataTarget.BuildKey(AnnotationTargetKind.Parameter, owner, k);
                    var (newVal, verr) = AnnotationValidator.CoerceAndValidate(paramKey, finalAssigned[k], $"parameter '{k}'", execCtx);
                    if (verr != null) return (RuntimeError)verr;
                    if (!ReferenceEquals(newVal, finalAssigned[k]))
                    {
                        finalAssigned[k] = newVal;
                        newVal.SetContext(execCtx);
                        execCtx.SymbolTable.Set(k, newVal);
                    }
                }

                if (method.HasVarArgs)
                {
                    var varname = method.VarArgNameTok?.Value?.ToString() ?? "params";
                    var paramKey = MetadataTarget.BuildKey(AnnotationTargetKind.Parameter, owner, varname);
                    var listVal = execCtx.SymbolTable.Get(varname);
                    if (listVal != null)
                    {
                        var (newVal, verr) = AnnotationValidator.CoerceAndValidate(paramKey, listVal, $"variadic '{varname}'", execCtx);
                        if (verr != null) return (RuntimeError)verr;
                        if (!ReferenceEquals(newVal, listVal))
                        {
                            execCtx.SymbolTable.Set(varname, newVal);
                        }
                    }
                }
            }

            return null;
        }

        public override RuntimeValue Copy()
            => new BoundClassMethodGroupValue(Definition, SelfInstance, Candidates)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<bound method {Name}>";
    }
}
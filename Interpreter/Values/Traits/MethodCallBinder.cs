using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Traits
{
    public static class MethodCallBinder
    {
        public static bool CanBind(ICallableMethodDefinition method, List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, Context context)
        {
            var argNames = method.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();
            var assigned = new HashSet<string>(StringComparer.Ordinal);

            if (!method.HasVarArgs && positionalArgs.Count > argNames.Count)
                return false;

            for (int i = 0; i < positionalArgs.Count && i < argNames.Count; i++)
                assigned.Add(argNames[i]);

            foreach (var kv in namedArgs)
            {
                if (method.HasVarArgs && method.VarArgNameTok != null &&
                    string.Equals(kv.Key, method.VarArgNameTok.Value.ToString(), StringComparison.Ordinal))
                {
                    if (kv.Value.Type != RuntimeValueType.List)
                        return false;

                    continue;
                }

                if (!argNames.Contains(kv.Key, StringComparer.Ordinal))
                    return false;

                if (!assigned.Add(kv.Key))
                    return false;
            }

            for (int i = 0; i < argNames.Count; i++)
            {
                if (assigned.Contains(argNames[i]))
                    continue;

                if (i >= method.ParamDefaults.Count || method.ParamDefaults[i] == null)
                    return false;
            }

            for (int i = 0; i < argNames.Count; i++)
            {
                RuntimeValue? actual = null;

                if (i < positionalArgs.Count)
                    actual = positionalArgs[i];
                else if (namedArgs.TryGetValue(argNames[i], out var named))
                    actual = named;

                if (actual == null)
                    continue;

                var expected = i < method.ArgTypes.Count ? method.ArgTypes[i] : null;
                if (expected != null && !TypeSystem.IsAssignable(context, expected, actual))
                    return false;
            }

            if (method.HasVarArgs && method.VarArgType != null)
            {
                for (int i = argNames.Count; i < positionalArgs.Count; i++)
                {
                    if (!TypeSystem.IsAssignable(context, method.VarArgType, positionalArgs[i]))
                        return false;
                }
            }

            return true;
        }

        public static (Context? execCtx, Error? error) BindIntoContext(
            ICallableMethodDefinition method,
            Context execCtx,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            string ownerTypeName)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var argNames = method.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();
            var finalAssigned = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            var extras = new List<RuntimeValue>();

            int formalCount = argNames.Count;

            for (int i = 0; i < positionalArgs.Count; i++)
            {
                if (i < formalCount)
                    finalAssigned[argNames[i]] = positionalArgs[i];
                else
                    extras.Add(positionalArgs[i]);
            }

            foreach (var kv in namedArgs)
            {
                if (method.HasVarArgs && method.VarArgNameTok != null &&
                    string.Equals(kv.Key, method.VarArgNameTok.Value.ToString(), StringComparison.Ordinal))
                {
                    if (kv.Value.Type != RuntimeValueType.List)
                        return (null, new RuntimeError(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd, $"Variadic named argument '{kv.Key}' must be a list", execCtx));

                    extras.AddRange(((ListValue)kv.Value).Elements);
                    continue;
                }

                if (finalAssigned.ContainsKey(kv.Key))
                    return (null, new RuntimeError(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd, $"Argument for '{kv.Key}' provided multiple times", execCtx));

                finalAssigned[kv.Key] = kv.Value;
            }

            for (int i = 0; i < formalCount; i++)
            {
                var argName = argNames[i];
                if (finalAssigned.ContainsKey(argName))
                    continue;

                var def = i < method.ParamDefaults.Count ? method.ParamDefaults[i] : null;
                if (def == null)
                    return (null, new RuntimeError(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd, $"Missing required argument '{argName}'", execCtx));

                var defRes = interpreter.Visit(def, execCtx);
                if (defRes.Error != null) return (null, defRes.Error);
                finalAssigned[argName] = defRes.Value ?? new NullValue().SetContext(execCtx).SetPos(def.PositionStart, def.PositionEnd);
            }

            for (int i = 0; i < formalCount; i++)
            {
                var argName = argNames[i];
                var actual = finalAssigned[argName];
                var expected = i < method.ArgTypes.Count ? method.ArgTypes[i] : null;

                if (expected != null && !TypeSystem.IsAssignable(execCtx, expected, actual))
                    return (null, new RuntimeError(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd, $"Type mismatch for argument '{argName}'", execCtx));
            }

            for (int i = 0; i < formalCount; i++)
            {
                var argValue = finalAssigned[argNames[i]];
                argValue.SetContext(execCtx);
                execCtx.SymbolTable.Set(argNames[i], argValue);
            }

            if (method.HasVarArgs)
            {
                var listVal = new ListValue(extras).SetContext(execCtx).SetPos(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd);

                if (method.VarArgType != null)
                {
                    foreach (var e in extras)
                    {
                        if (!TypeSystem.IsAssignable(execCtx, method.VarArgType, e))
                            return (null, new RuntimeError(method.NameTok.Value.PositionStart, method.NameTok.Value.PositionEnd, $"Type mismatch in varargs", execCtx));
                    }
                }

                execCtx.SymbolTable.Set(method.VarArgNameTok?.Value?.ToString() ?? "params", listVal);
            }

            return (execCtx, null);
        }
    }

    public class BoundMethodGroupValue : BaseFunctionValue
    {
        public ClassInstanceValue Instance { get; }
        public ClassTypeValue OwnerType { get; }
        public List<ICallableMethodDefinition> Candidates { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundMethodGroupValue(string name, ClassInstanceValue instance, ClassTypeValue ownerType, List<ICallableMethodDefinition> candidates)
            : base(name)
        {
            Instance = instance;
            OwnerType = ownerType;
            Candidates = candidates;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var selected = Candidates.FirstOrDefault(c => c.HasBody && MethodCallBinder.CanBind(c, positionalArgs, namedArgs, Context));
            if (selected == null)
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching overload found for '{Name}'", Context));
            }

            var execCtx = GenerateNewContext();
            execCtx.CurrentClassMethodOwner = OwnerType;
            execCtx.SymbolTable.Set(
                "self",
                Instance,
                isLet: true,
                declaredType: new TypeDescriptor(OwnerType.ClassName),
                isStaticallyTyped: true,
                isPublic: false);

            var bindRes = MethodCallBinder.BindIntoContext(selected, execCtx, positionalArgs, namedArgs, OwnerType.ClassName);
            if (bindRes.error != null)
                return res.Failure(bindRes.error);

            var bodyRes = interpreter.Visit(selected.BodyNode!, bindRes.execCtx!);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
                return res.Success(bodyRes.FuncReturnValue);

            var retValue = selected.ShouldAutoReturn
                ? (bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd))
                : new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
            => new BoundMethodGroupValue(Name, Instance, OwnerType, Candidates)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<bound method {Name}>";
    }
}
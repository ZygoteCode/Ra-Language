using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    public abstract class BaseFunctionValue : RuntimeValue
    {
        public string Name { get; }
        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BaseFunctionValue(string name)
        {
            Name = name ?? "<anonymous>";
        }

        public Context GenerateNewContext()
        {
            var newCtx = new Context(Name, Context, PositionStart);
            newCtx.SymbolTable = new SymbolTable(newCtx.Parent?.SymbolTable);
            return newCtx;
        }

        public RuntimeResult CheckArgs(List<string> argNames, List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            if (args.Count > argNames.Count)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{args.Count - argNames.Count} too many args passed into {Name}", Context));

            if (args.Count < argNames.Count)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));

            return res.Success(null);
        }

        public void PopulateArgs(List<string> argNames, List<RuntimeValue> args, Context execCtx)
        {
            for (int i = 0; i < args.Count; i++)
            {
                var argValue = args[i];
                argValue.SetContext(execCtx);
                execCtx.SymbolTable.Set(argNames[i], argValue);
            }
        }

        public virtual RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            return ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
        }

        public virtual RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            return Execute(positionalArgs);
        }

        public (Context? execCtx, Error? error) PrepareExecutionContextForCall(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<string> formalNames,
            List<TypeDescriptor?>? argTypes,
            List<AstNode?>? paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType)
        {
            var execCtx = GenerateNewContext();

            var finalAssigned = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            var extras = new List<RuntimeValue>();

            int formalCount = formalNames?.Count ?? 0;

            for (int i = 0; i < positionalArgs.Count; i++)
            {
                if (i < formalCount)
                {
                    var name = formalNames[i];
                    if (finalAssigned.ContainsKey(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Argument for parameter '{name}' provided multiple times", Context));
                    }
                    finalAssigned[name] = positionalArgs[i];
                }
                else
                {
                    if (!hasVarArgs)
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"{positionalArgs.Count - formalCount} too many args passed into {Name}", Context));

                    extras.Add(positionalArgs[i]);
                }
            }

            if (namedArgs != null)
            {
                foreach (var kv in namedArgs)
                {
                    var name = kv.Key;
                    var value = kv.Value;

                    if (hasVarArgs && varArgNameTok != null && name == varArgNameTok.Value.ToString())
                    {
                        if (value.Type != RuntimeValueType.List)
                        {
                            return (null, new RuntimeError(PositionStart, PositionEnd, $"Variadic named argument '{name}' must be a list", Context));
                        }

                        ListValue provided = (ListValue)value;
                        extras.AddRange(provided.Elements);
                        continue;
                    }

                    if (!formalNames.Contains(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Unknown named argument '{name}'", Context));
                    }

                    if (finalAssigned.ContainsKey(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Argument for parameter '{name}' provided multiple times", Context));
                    }

                    finalAssigned[name] = value;
                }
            }

            foreach (var kv in finalAssigned)
            {
                var v = kv.Value;
                v.SetContext(execCtx);
                execCtx.SymbolTable.Set(kv.Key, v);
            }

            if (paramDefaults != null)
            {
                var interpreter = new Interpreter();

                for (int i = 0; i < formalNames.Count; i++)
                {
                    var name = formalNames[i];
                    if (finalAssigned.ContainsKey(name)) continue;

                    AstNode? defAst = i < paramDefaults.Count ? paramDefaults[i] : null;
                    if (defAst != null)
                    {
                        var innerRes = interpreter.Visit(defAst, execCtx);
                        if (innerRes.Error != null) return (null, innerRes.Error);
                        var val = innerRes.Value;
                        if (val == null) val = new RaLanguage.Interpreter.Values.Primitives.NullValue().SetContext(execCtx).SetPos(defAst.PositionStart, defAst.PositionEnd);
                        val.SetContext(execCtx);
                        execCtx.SymbolTable.Set(name, val);
                        finalAssigned[name] = val;
                    }
                }
            }

            for (int i = 0; i < formalNames.Count; i++)
            {
                var name = formalNames[i];
                if (!finalAssigned.ContainsKey(name))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, $"Missing required argument '{name}' for function '{Name}'", Context));
                }
            }

            if (argTypes != null)
            {
                for (int i = 0; i < formalNames.Count; i++)
                {
                    var expected = i < argTypes.Count ? argTypes[i] : null;
                    if (expected != null)
                    {
                        if (expected.IsTypeParameter()) continue;

                        var actual = finalAssigned[formalNames[i]];
                        if (!TypeSystem.IsAssignable(execCtx, expected, actual))
                        {
                            return (null, new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for argument '{formalNames[i]}': expected '{expected}', got '{actual.Type}'", Context));
                        }
                    }
                }
            }

            if (hasVarArgs)
            {
                var listVal = new ListValue(extras).SetContext(execCtx).SetPos(PositionStart, PositionEnd);

                if (varArgType != null)
                {
                    if (!varArgType.IsTypeParameter())
                    {
                        foreach (var e in extras)
                        {
                            if (!TypeSystem.IsAssignable(execCtx, varArgType, e))
                            {
                                string vname = varArgNameTok?.Value?.ToString() ?? "<vararg>";
                                return (null, new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for variadic argument '{vname}': expected '{varArgType}', got '{e.Type}'", Context));
                            }
                        }
                    }
                }

                var varname = varArgNameTok?.Value?.ToString() ?? "params";
                execCtx.SymbolTable.Set(varname, listVal);
            }

            return (execCtx, null);
        }

        public RuntimeResult CheckAndPopulateArgs(
            List<string> argNames,
            List<RuntimeValue> args,
            Context execCtx,
            List<TypeDescriptor?>? argTypes = null,
            bool hasVarArgs = false,
            Token? varArgNameTok = null,
            TypeDescriptor? varArgType = null)
        {
            var res = new RuntimeResult();
            if (!hasVarArgs)
            {
                if (args.Count > argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{args.Count - argNames.Count} too many args passed into {Name}", Context));

                if (args.Count < argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));
            }
            else
            {
                if (args.Count < argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));
            }

            if (argTypes != null)
            {
                for (int i = 0; i < argNames.Count; i++)
                {
                    var expected = (i < argTypes.Count) ? argTypes[i] : null;
                    if (expected != null)
                    {
                        if (!expected.IsTypeParameter() && !TypeSystem.IsAssignable(execCtx, expected, args[i]))
                        {
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"Type mismatch for argument '{argNames[i]}': expected '{expected}', got '{args[i].Type}'", Context));
                        }
                    }
                }

                if (hasVarArgs && varArgType != null && !varArgType.IsTypeParameter())
                {
                    for (int i = argNames.Count; i < args.Count; i++)
                    {
                        if (!TypeSystem.IsAssignable(execCtx, varArgType, args[i]))
                        {
                            string varArgName = varArgNameTok?.Value?.ToString() ?? "<varargs>";
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"Type mismatch for variadic argument '{varArgName}': expected '{varArgType}', got '{args[i].Type}'", Context));
                        }
                    }
                }
            }

            for (int i = 0; i < argNames.Count; i++)
            {
                var argValue = args[i];
                argValue.SetContext(execCtx);
                execCtx.SymbolTable.Set(argNames[i], argValue);
            }

            if (hasVarArgs)
            {
                var extras = new List<RuntimeValue>();
                for (int i = argNames.Count; i < args.Count; i++)
                {
                    var a = args[i];
                    a.SetContext(execCtx);
                    extras.Add(a);
                }

                var listVal = new ListValue(extras)
                    .SetContext(execCtx)
                    .SetPos(PositionStart, PositionEnd);

                var varName = varArgNameTok?.Value?.ToString() ?? "params";
                execCtx.SymbolTable.Set(varName, listVal);
            }

            return res.Success(null);
        }
    }

    internal static class TypeDescriptorExtensions
    {
        public static bool IsTypeParameter(this TypeDescriptor? td)
        {
            if (td == null) return false;
            try
            {
                return td != null && td.IsTypeParameter;
            }
            catch { }
            return !string.IsNullOrEmpty(td.Name) && char.IsUpper(td.Name[0]) && td.GenericArgs.Count == 0;
        }

        public static TypeDescriptor? SubstituteBindings(this TypeDescriptor? td, Dictionary<string, TypeDescriptor> bindings)
        {
            if (td == null) return null;
            try
            {
                return td.Substitute(bindings);
            }
            catch { }
            if (td.IsTypeParameter() && bindings.TryGetValue(td.Name, out var bound)) return bound;
            if (td.GenericArgs == null || td.GenericArgs.Count == 0) return td;
            var newArgs = td.GenericArgs.Select(a => a.SubstituteBindings(bindings)).ToList();

            if (newArgs == null) return null;
            return new TypeDescriptor(td.Name, newArgs!);
        }
    }
}
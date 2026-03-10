using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    public abstract class BaseFunctionValue : RuntimeValue
    {
        public string Name { get; }
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
                    if (expected != null && !(expected.IsBuiltIn && expected.BuiltIn == BuiltInType.Any))
                    {
                        if (!TypeSystem.IsAssignable(expected, args[i]))
                        {
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"Type mismatch for argument '{argNames[i]}': expected '{expected}', got '{args[i].Type}'", Context));
                        }
                    }
                }

                if (hasVarArgs && varArgType != null && !(varArgType.IsBuiltIn && varArgType.BuiltIn == BuiltInType.Any))
                {
                    for (int i = argNames.Count; i < args.Count; i++)
                    {
                        if (!TypeSystem.IsAssignable(varArgType, args[i]))
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
}
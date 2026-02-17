using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;

namespace RaLanguage.Interpreter.Values.Functions
{
    public abstract class BaseFunction : RuntimeValue
    {
        public string Name { get; }
        public BaseFunction(string name)
        {
            Name = name ?? "<anonymous>";
        }

        public Context GenerateNewContext()
        {
            var newCtx = new Context(Name, Context, PosStart);
            newCtx.SymbolTable = new SymbolTable(newCtx.Parent?.SymbolTable);
            return newCtx;
        }

        public RTResult CheckArgs(List<string> argNames, List<RuntimeValue> args)
        {
            var res = new RTResult();
            if (args.Count > argNames.Count)
                return res.Failure(new RuntimeError(PosStart, PosEnd, $"{args.Count - argNames.Count} too many args passed into {Name}", Context));

            if (args.Count < argNames.Count)
                return res.Failure(new RuntimeError(PosStart, PosEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));

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

        public RTResult CheckAndPopulateArgs(List<string> argNames, List<RuntimeValue> args, Context execCtx)
        {
            var res = new RTResult();
            res.Register(CheckArgs(argNames, args));
            if (res.ShouldReturn()) return res;
            PopulateArgs(argNames, args, execCtx);
            return res.Success(null);
        }
    }
}
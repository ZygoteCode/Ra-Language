using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class BuiltInFunctionValue : BaseFunctionValue
    {
        public sealed override RuntimeValueType Type => RuntimeValueType.Function;
        public BuiltInFunctionValue(string name) : base(name) { }


        public sealed override RuntimeResult Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            var execCtx = GenerateNewContext();

            RuntimeResult methodResult;
            List<string> argNames;

            switch (Name)
            {
                case "print": argNames = new List<string> { "value" }; methodResult = ExecutePrint(execCtx, args, argNames, res); break;
                default: return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No execute_{Name} method defined", Context));
            }

            return methodResult;
        }

        private RuntimeResult ExecuteCommon(Context execCtx, List<RuntimeValue> args, List<string> argNames, RuntimeResult res, Func<Context, RuntimeResult> action)
        {
            res.Register(CheckAndPopulateArgs(argNames, args, execCtx));
            if (res.ShouldReturn()) return res;
            var ret = res.Register(action(execCtx));
            if (res.ShouldReturn()) return res;
            return res.Success(ret);
        }

        private RuntimeResult ExecutePrint(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            Console.WriteLine(c.SymbolTable.Get("value"));
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        public sealed override RuntimeValue Copy()
        {
            return new BuiltInFunctionValue(Name).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<built-in function {Name}>";
    }
}
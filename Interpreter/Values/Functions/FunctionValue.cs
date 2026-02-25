using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class FunctionValue : BaseFunctionValue
    {
        public AstNode BodyNode { get; }
        public List<string> ArgNames { get; }
        public bool ShouldAutoReturn { get; }
        public override RuntimeValueType Type => RuntimeValueType.Function;

        public FunctionValue(string name, AstNode bodyNode, List<string> argNames, bool shouldAutoReturn)
            : base(name)
        {
            BodyNode = bodyNode;
            ArgNames = argNames;
            ShouldAutoReturn = shouldAutoReturn;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();
            var execCtx = GenerateNewContext();

            res.Register(CheckAndPopulateArgs(ArgNames, args, execCtx));
            if (res.ShouldReturn()) return res;

            var value = res.Register(interpreter.Visit(BodyNode, execCtx));
            if (res.ShouldReturn() && res.FuncReturnValue == null) return res;

            var retValue = (ShouldAutoReturn ? value : null) ?? res.FuncReturnValue ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);
            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
        {
            return new FunctionValue(Name, BodyNode, ArgNames, ShouldAutoReturn).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<function {Name}>";
    }
}
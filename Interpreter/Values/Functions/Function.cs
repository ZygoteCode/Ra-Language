using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class Function : BaseFunction
    {
        public AstNode BodyNode { get; }
        public List<string> ArgNames { get; }
        public bool ShouldAutoReturn { get; }

        public Function(string name, AstNode bodyNode, List<string> argNames, bool shouldAutoReturn)
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

            var retValue = (ShouldAutoReturn ? value : null) ?? res.FuncReturnValue ?? Number.Null;
            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
        {
            return new Function(Name, BodyNode, ArgNames, ShouldAutoReturn).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<function {Name}>";
    }
}
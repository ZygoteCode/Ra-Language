using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class FunctionValue : BaseFunctionValue
    {
        public AstNode BodyNode { get; }
        public List<string> ArgNames { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public bool ShouldAutoReturn { get; }
        public override RuntimeValueType Type => RuntimeValueType.Function;

        public FunctionValue(
            string name,
            AstNode bodyNode,
            List<string> argNames,
            List<TypeDescriptor?>? argTypes,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            bool shouldAutoReturn
        ) : base(name)
        {
            BodyNode = bodyNode;
            ArgNames = argNames ?? new List<string>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            ShouldAutoReturn = shouldAutoReturn;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();
            var execCtx = GenerateNewContext();

            res.Register(CheckAndPopulateArgs(ArgNames, args, execCtx, ArgTypes, HasVarArgs, VarArgNameTok, VarArgType));
            if (res.ShouldReturn()) return res;

            var value = res.Register(interpreter.Visit(BodyNode, execCtx));
            if (res.ShouldReturn() && res.FuncReturnValue == null) return res;

            var retValue = (ShouldAutoReturn ? value : null) ?? res.FuncReturnValue ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (ReturnType != null && !(ReturnType.IsBuiltIn && ReturnType.BuiltIn == BuiltInType.Any))
            {
                if (!TypeSystem.IsAssignable(ReturnType, retValue))
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"Return type mismatch in function '{Name}': expected '{ReturnType}', got '{retValue.Type}'", Context));
                }
            }

            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
        {
            return new FunctionValue(Name, BodyNode, ArgNames, ArgTypes, HasVarArgs, VarArgNameTok, VarArgType, ReturnType, ShouldAutoReturn)
                .SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<function {Name}>";
    }
}
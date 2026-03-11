using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Types;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class FunctionValue : BaseFunctionValue
    {
        public AstNode BodyNode { get; }
        public List<string> ArgNames { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<AstNode?> ParamDefaults { get; }
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
            List<AstNode?>? paramDefaults,
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
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            ShouldAutoReturn = shouldAutoReturn;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
        {
            return ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal));
        }

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            var (execCtx, err) = PrepareExecutionContextForCall(positionalArgs, namedArgs, ArgNames, ArgTypes, ParamDefaults, HasVarArgs, VarArgNameTok, VarArgType);
            if (err != null)
            {
                return res.Failure(err);
            }

            var interpreter = new Interpreter();
            var bodyRes = interpreter.Visit(BodyNode, execCtx!);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
            {
                var retVal = bodyRes.FuncReturnValue;
                if (ReturnType != null && !(ReturnType.IsBuiltIn && ReturnType.BuiltIn == BuiltInType.Any))
                {
                    if (!TypeSystem.IsAssignable(ReturnType, retVal))
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in function '{Name}': expected '{ReturnType}', got '{retVal.Type}'", Context));
                }
                return res.Success(retVal.SetContext(Context).SetPos(PositionStart, PositionEnd));
            }

            var value = bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);
            var retValue = (ShouldAutoReturn ? value : null) ?? value;

            if (ReturnType != null && !(ReturnType.IsBuiltIn && ReturnType.BuiltIn == BuiltInType.Any))
            {
                if (!TypeSystem.IsAssignable(ReturnType, retValue))
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in function '{Name}': expected '{ReturnType}', got '{retValue.Type}'", Context));
            }

            return res.Success(retValue.SetContext(Context).SetPos(PositionStart, PositionEnd));
        }

        public override RuntimeValue Copy()
        {
            return new FunctionValue(Name, BodyNode, ArgNames, ArgTypes, ParamDefaults, HasVarArgs, VarArgNameTok, VarArgType, ReturnType, ShouldAutoReturn).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<function {Name}>";
    }
}
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public class BoundStructMethodValue : BaseFunctionValue
    {
        public StructTypeValue Definition { get; }
        public StructInstanceValue SelfInstance { get; }
        public StructMethodDefinitionNode MethodNode { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundStructMethodValue(StructTypeValue definition, StructInstanceValue selfInstance, StructMethodDefinitionNode methodNode)
            : base(methodNode.NameTok.Value?.ToString() ?? "<method>")
        {
            Definition = definition;
            SelfInstance = selfInstance;
            MethodNode = methodNode;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var execCtx = GenerateNewContext();
            execCtx.SymbolTable.Set(
                "self",
                SelfInstance,
                isLet: true,
                declaredType: new TypeDescriptor(Definition.StructName),
                isStaticallyTyped: true,
                isPublic: false);

            res.Register(CheckAndPopulateArgs(
                MethodNode.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList(),
                positionalArgs,
                execCtx,
                MethodNode.ArgTypes,
                MethodNode.HasVarArgs,
                MethodNode.VarArgNameTok,
                MethodNode.VarArgType));

            if (res.ShouldReturn()) return res;

            var bodyRes = interpreter.Visit(MethodNode.BodyNode, execCtx);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
            {
                if (MethodNode.ReturnType != null && !TypeSystem.IsAssignable(execCtx, MethodNode.ReturnType, bodyRes.FuncReturnValue))
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));

                return res.Success(bodyRes.FuncReturnValue);
            }

            var retValue = MethodNode.ShouldAutoReturn
                ? (bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd))
                : new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (MethodNode.ReturnType != null && !TypeSystem.IsAssignable(execCtx, MethodNode.ReturnType, retValue))
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));

            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
        {
            return new BoundStructMethodValue(Definition, SelfInstance, MethodNode)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<bound method {Definition.StructName}.{Name}>";
    }
}
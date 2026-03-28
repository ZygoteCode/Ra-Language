using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class BoundClassMethodValue : BaseFunctionValue
    {
        public ClassTypeValue Definition { get; }
        public ClassInstanceValue SelfInstance { get; }
        public FunctionDefinitionNode MethodNode { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundClassMethodValue(ClassTypeValue definition, ClassInstanceValue selfInstance, FunctionDefinitionNode methodNode)
            : base(methodNode.VarNameTok?.Value?.ToString() ?? "<method>")
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
                declaredType: new TypeDescriptor(Definition.ClassName),
                isStaticallyTyped: true,
                isPublic: false);

            var argNames = MethodNode.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();

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

            if (MethodNode.IsConstructor && bodyRes.FuncReturnValue != null)
            {
                return res.Failure(new RuntimeError(
                    MethodNode.PositionStart,
                    MethodNode.PositionEnd,
                    "Constructors cannot return a value",
                    Context));
            }

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
            => new BoundClassMethodValue(Definition, SelfInstance, MethodNode)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<bound method {Definition.ClassName}.{Name}>";
    }
}
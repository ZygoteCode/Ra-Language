using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class BoundClassMethodValue : BaseFunctionValue
    {
        public ClassTypeValue Definition { get; }
        public ClassInstanceValue SelfInstance { get; }
        public FunctionDefinitionNode MethodNode { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public bool IsStatic { get; }

        public BoundClassMethodValue(ClassTypeValue definition, ClassInstanceValue selfInstance, FunctionDefinitionNode methodNode, bool isStatic)
            : base(methodNode.VarNameTok?.Value?.ToString() ?? "<method>")
        {
            Definition = definition;
            SelfInstance = selfInstance;
            MethodNode = methodNode;
            IsStatic = isStatic;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var execCtx = GenerateNewContext();

            if (!IsStatic && SelfInstance != null)
            {
                execCtx.SymbolTable.Set(
                    "self",
                    SelfInstance,
                    isLet: true,
                    declaredType: new TypeDescriptor(Definition.ClassName),
                    isStaticallyTyped: true,
                    isPublic: false);
            }

            if (MethodNode.IsConstructor)
            {
                execCtx.SymbolTable.Set(
                    "__in_constructor__",
                    new BooleanValue(true).SetContext(execCtx).SetPos(PositionStart, PositionEnd),
                    isLet: true,
                    declaredType: new TypeDescriptor("bool"),
                    isStaticallyTyped: true,
                    isPublic: false
                );
            }

            var argNames = MethodNode.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();

            var bindRes = PrepareExecutionContextForCall(
                positionalArgs,
                namedArgs,
                argNames,
                MethodNode.ArgTypes,
                MethodNode.ParamDefaults,
                MethodNode.HasVarArgs,
                MethodNode.VarArgNameTok,
                MethodNode.VarArgType);

            if (!IsStatic && SelfInstance != null)
            {
                bindRes.execCtx.SymbolTable.Set(
                    "self",
                    SelfInstance,
                    isLet: true,
                    declaredType: new TypeDescriptor(Definition.ClassName),
                    isStaticallyTyped: true,
                    isPublic: false
                );
            }

            if (MethodNode.IsConstructor)
            {
                bindRes.execCtx.SymbolTable.Set(
                    "__in_constructor__",
                    new BooleanValue(true).SetContext(execCtx).SetPos(PositionStart, PositionEnd),
                    isLet: true,
                    declaredType: new TypeDescriptor("bool"),
                    isStaticallyTyped: true,
                    isPublic: false);
            }

            if (bindRes.error != null)
                return res.Failure(bindRes.error);

            var bodyRes = interpreter.Visit(MethodNode.BodyNode, bindRes.execCtx!);
            if (bodyRes.Error != null)
                return res.Failure(bodyRes.Error);

            if (MethodNode.IsConstructor && bodyRes.FuncReturnValue != null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, "Constructors cannot return a value", Context));

            if (bodyRes.FuncReturnValue != null)
            {
                if (MethodNode.ReturnType != null &&
                    !TypeSystem.IsAssignable(bindRes.execCtx!, MethodNode.ReturnType, bodyRes.FuncReturnValue))
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));
                }

                return res.Success(bodyRes.FuncReturnValue);
            }

            var retValue = MethodNode.ShouldAutoReturn
                ? (bodyRes.Value ?? new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd))
                : new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (MethodNode.ReturnType != null &&
                !TypeSystem.IsAssignable(bindRes.execCtx!, MethodNode.ReturnType, retValue))
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));
            }

            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
            => new BoundClassMethodValue(Definition, SelfInstance, MethodNode, IsStatic)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<bound method {Definition.ClassName}.{Name}>";
    }
}
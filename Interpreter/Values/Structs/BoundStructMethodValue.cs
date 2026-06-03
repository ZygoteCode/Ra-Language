using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
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

        public sealed override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundStructMethodValue(StructTypeValue definition, StructInstanceValue selfInstance, StructMethodDefinitionNode methodNode)
            : base(methodNode.NameTok.Value?.ToString() ?? "<method>")
        {
            Definition = definition;
            SelfInstance = selfInstance;
            MethodNode = methodNode;
        }

        public sealed override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public sealed override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            if (MethodNode.IsAsync || MethodNode.IsAsyncStream)
            {
                var capturedPositional = positionalArgs;
                var capturedNamed = namedArgs;
                return AsyncMethodDispatch.Dispatch(
                    MethodNode.IsAsync,
                    MethodNode.IsAsyncStream,
                    Name,
                    Context,
                    PositionStart,
                    PositionEnd,
                    asyncCtxOverride => SyncAwait.Get(ExecuteSyncBody(capturedPositional, capturedNamed, asyncCtxOverride)));
            }
            return await ExecuteSyncBody(positionalArgs, namedArgs, null);
        }

        private async ValueTask<RuntimeResult> ExecuteSyncBody(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, AsyncContext? asyncCtxOverride)
        {
            var res = new RuntimeResult();

            var execCtx = GenerateNewContext();
            if (asyncCtxOverride != null) execCtx.AsyncCtx = asyncCtxOverride;
            execCtx.SymbolTable.Set(
                "self",
                SelfInstance,
                isLet: true,
                declaredType: Definition.SelfTypeDescriptor,
                isStaticallyTyped: true,
                isPublic: false);

            if (MethodNode.IsConstructor)
            {
                execCtx.IsInConstructor = true;
            }

            res.Register(CheckAndPopulateArgs(
                MethodNode.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList(),
                positionalArgs,
                execCtx,
                MethodNode.ArgTypes,
                MethodNode.HasVarArgs,
                MethodNode.VarArgNameTok,
                MethodNode.VarArgType));

            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            RuntimeResult bodyRes;
            var compiledM = Runtime.FunctionDefinitionHelper.GetOrCompileStructMethod(MethodNode);
            if (compiledM == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"struct method '{Name}' has no IR-compiled body", Context));
            {
                // M79: pool rent + return on success only.
                // PERF: pooled VM host (Interpreter + VmExecutor); returned to
                // the pool only on synchronous completion.
                var host = Vm.VmHostPool.Rent();
                var frame = Vm.VmFrame.Rent(compiledM);
                var execTask = host.Executor.Execute(frame, execCtx);
                if (execTask.IsCompletedSuccessfully)
                {
                    bodyRes = execTask.Result;
                    if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
                    Vm.VmHostPool.Return(host);
                }
                else
                {
                    bodyRes = await execTask;
                    if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
                }
            }
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
            {
                if (MethodNode.ReturnType != null && !TypeSystem.IsAssignable(execCtx, MethodNode.ReturnType, bodyRes.FuncReturnValue))
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));

                return res.Success(bodyRes.FuncReturnValue);
            }

            var retValue = MethodNode.ShouldAutoReturn
                ? (bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

            if (MethodNode.ReturnType != null && !TypeSystem.IsAssignable(execCtx, MethodNode.ReturnType, retValue))
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Return type mismatch in method '{Name}'", Context));

            return res.Success(retValue);
        }

        public sealed override RuntimeValue Copy()
        {
            return new BoundStructMethodValue(Definition, SelfInstance, MethodNode)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<bound method {Definition.StructName}.{Name}>";
    }
}
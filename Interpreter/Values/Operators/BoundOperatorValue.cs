using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Operators
{
    public class BoundOperatorValue : BaseFunctionValue
    {
        public RuntimeValue Instance { get; }
        public TokenType OperatorType { get; }
        public string ParameterTypeName { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        // M18: optional reference to the AST OperatorDefinitionNode so we can
        // route through the cached IR compile (CompiledBody) when available.
        // Older callers that don't have the node still work — they just take
        // the AST visit path.
        public OperatorDefinitionNode? OpNode { get; }

        public override RuntimeValueType Type => RuntimeValueType.Function;
        public override bool IsCopy => false;
        public override RuntimeValue Copy() => this;

        public BoundOperatorValue(
            RuntimeValue instance,
            TokenType operatorType,
            string parameterTypeName,
            TypeDescriptor? returnType,
            AstNode bodyNode,
            bool shouldAutoReturn,
            OperatorDefinitionNode? opNode = null) : base($"operator_{operatorType}")
        {
            Instance = instance;
            OperatorType = operatorType;
            ParameterTypeName = parameterTypeName;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            OpNode = opNode;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            if (positionalArgs.Count != 1 && namedArgs.Count != 1)
            {
                return res.Failure(new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"Operator expects exactly 1 argument, got {positionalArgs.Count + namedArgs.Count}",
                    Context!));
            }

            var arg = positionalArgs.Count > 0 ? positionalArgs[0] : namedArgs.Values.First();

            var operatorContext = new Context($"operator_{OperatorType}", Context);
            operatorContext.SymbolTable.Set("self", Instance, isLet: true);
            operatorContext.SymbolTable.Set("other", arg, isLet: true);

            if (Instance is ClassInstanceValue ci && ci.GenericBindings != null)
            {
                foreach (var kv in ci.GenericBindings)
                {
                    var gtv = new GenericTypeValue(kv.Key, kv.Value).SetContext(operatorContext).SetPos(PositionStart, PositionEnd);
                    operatorContext.SymbolTable.Set(kv.Key, gtv, isLet: true, declaredType: new TypeDescriptor("type"), isStaticallyTyped: true, isPublic: false);
                }
            }

            // M20.2: arrow-form (`=> expr`) and block-form (`{ ret expr; }`)
            // operators share the same dispatch: compile once, run via VM,
            // accept either explicit return or expression value.
            // ShouldAutoReturn affected the old AST path (block returns
            // bodyRes.Value, arrow returns bodyRes.FuncReturnValue) but now
            // the IR compiler routes arrow-form to OP_RET and block-form to
            // OP_HALT, so both surface through the same FuncReturnValue ??
            // Value fallback.
            {
                var interp = new Interpreter();
                RuntimeResult bodyRes;
                var compiledOp = OpNode != null ? Runtime.FunctionDefinitionHelper.GetOrCompileOperator(OpNode) : null;
                if (compiledOp == null)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"operator {OperatorType} body has no IR-compiled body", Context!));
                // M79: pool rent + return on success only.
                var vm = new Vm.VmExecutor(interp);
                var frame = Vm.VmFrame.Rent(compiledOp);
                bodyRes = await vm.Execute(frame, operatorContext);
                if (bodyRes.Error != null) return res.Failure(bodyRes.Error);
                Vm.VmFrame.Return(frame);

                var returnValue = bodyRes.FuncReturnValue ?? bodyRes.Value;

                if (returnValue == null)
                {
                    return res.Failure(new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"Operator {OperatorType} body returned null value",
                        Context!));
                }

                return res.Success(returnValue);
            }
        }

        public sealed override string ToString() => $"<operator {OperatorType}>";
    }
}

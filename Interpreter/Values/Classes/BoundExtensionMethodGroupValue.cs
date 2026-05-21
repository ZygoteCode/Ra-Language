using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class BoundExtensionMethodGroupValue : BaseFunctionValue
    {
        public RuntimeValue Receiver { get; }
        public List<FunctionDefinitionNode> Candidates { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundExtensionMethodGroupValue(RuntimeValue receiver, List<FunctionDefinitionNode> candidates)
            : base("<extension>")
        {
            Receiver = receiver;
            Candidates = candidates;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var selected = Candidates.FirstOrDefault(c => MethodCallBinder.CanBind(c, positionalArgs, namedArgs, Context));
            if (selected == null)
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching extension overload found for '{Name}'", Context));
            }

            var execCtx = GenerateNewContext();

            var selfValue = Receiver.IsCopy ? Receiver.Copy() : Receiver;
            var selfTypeName = TypeSystem.GetExtensionTargetName(Receiver);

            execCtx.SymbolTable.Set(
                "self",
                selfValue,
                isLet: true,
                declaredType: new TypeDescriptor(selfTypeName),
                isStaticallyTyped: true,
                isPublic: false);

            var bind = await MethodCallBinder.BindIntoContext(
                selected,
                execCtx,
                positionalArgs,
                namedArgs,
                selfTypeName);

            if (bind.error != null)
                return res.Failure(bind.error);

            var bodyRes = await interpreter.Visit(selected.BodyNode!, bind.execCtx!);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
                return res.Success(bodyRes.FuncReturnValue);

            var retValue = selected.ShouldAutoReturn
                ? (bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

            return res.Success(retValue);
        }

        public override RuntimeValue Copy()
            => new BoundExtensionMethodGroupValue(Receiver, Candidates)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<extension {Name}>";
    }
}
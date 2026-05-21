using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Calls;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionCallNodeVisitor : NodeVisitor<FunctionCallNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(FunctionCallNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (context.AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "function calls are not allowed in this context",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "calls disabled here",
                    help: "this expression runs in a context (e.g. an annotation argument) where calls are forbidden"));
            }

            var calleeVal = res.Register(await interpreter.Visit(node.NodeToCall, context));
            if (res.ShouldReturn()) return res;

            var argEval = await FunctionCallExecutor.EvaluateArguments(
                node.ArgNodes, context, interpreter);
            if (argEval.Result.Error != null) return res.Failure(argEval.Result.Error);

            return await FunctionCallExecutor.Invoke(
                calleeVal!,
                argEval.Positional,
                argEval.Named,
                node.GenericTypeArgs,
                node.PositionStart,
                node.PositionEnd,
                context);
        }
    }
}

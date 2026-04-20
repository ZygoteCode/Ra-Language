using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionCallNodeVisitor : NodeVisitor<FunctionCallNode>
    {
        protected sealed override RuntimeResult VisitNode(FunctionCallNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (context.AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Function calls are blocked in this context", context));
            }

            var calleeVal = res.Register(interpreter.Visit(node.NodeToCall, context));
            if (res.ShouldReturn()) return res;
            if (calleeVal == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Attempted to call a null value", context));
            }

            var positionalArgs = new List<RuntimeValue>();
            var namedArgs = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);

            if (node.ArgNodes != null)
            {
                foreach (var argNode in node.ArgNodes)
                {
                    var evaluated = res.Register(interpreter.Visit(argNode.Expr, context));
                    if (res.ShouldReturn()) return res;

                    if (argNode.NameTok != null)
                    {
                        string name = argNode.NameTok.Value.ToString() ?? "";
                        if (namedArgs.ContainsKey(name))
                        {
                            return res.Failure(new RuntimeError(argNode.PositionStart, argNode.PositionEnd, $"Duplicate named argument '{name}'", context));
                        }
                        namedArgs[name] = evaluated;
                    }
                    else
                    {
                        positionalArgs.Add(evaluated);
                    }
                }
            }

            if (calleeVal.Type == RuntimeValueType.BaseFunction || calleeVal.Type == RuntimeValueType.Function)
            {
                var func = (BaseFunctionValue)calleeVal;
                RuntimeValue? callResult = null;
                var fnExecRes = func.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                var fnReturn = res.Register(fnExecRes);
                if (res.ShouldReturn()) return res;

                if (fnReturn == null)
                {
                    callResult = new NullValue()
                        .SetContext(context)
                        .SetPos(node.PositionStart, node.PositionEnd);
                }
                else
                {
                    callResult = fnReturn;
                }

                var outVal = callResult.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
                return res.Success(outVal);
            }

            var execRes = calleeVal.Execute(positionalArgs);
            var execReturn = res.Register(execRes);
            if (res.ShouldReturn()) return res;

            if (execReturn == null)
            {
                var nullVal = new NullValue()
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd);
                return res.Success(nullVal);
            }

            var finalVal = execReturn.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
            return res.Success(finalVal);
        }
    }
}
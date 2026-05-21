using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class GotoNodeVisitor : NodeVisitor<GotoNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(GotoNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            string varName = node.VarName.Value.ToString();

            for (int i = 0; i < interpreter.Labels.Count; i++)
            {
                var label = interpreter.Labels[i];

                if (label.Item1.Equals(varName))
                {
                    res.Register(await interpreter.Visit(label.Item2, context));
                    if (res.ShouldReturn()) return res;
                    return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' label is not defined", context));
        }
    }
}
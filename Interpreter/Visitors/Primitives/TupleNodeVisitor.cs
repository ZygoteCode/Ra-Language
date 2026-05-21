using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class TupleNodeVisitor : NodeVisitor<TupleNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(TupleNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                var val = res.Register(await interpreter.Visit(elementNode, context));
                if (res.ShouldReturn()) return res;
                elements.Add(val);
            }

            return res.Success(new TupleValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
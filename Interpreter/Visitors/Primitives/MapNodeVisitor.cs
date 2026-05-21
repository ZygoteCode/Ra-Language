using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class MapNodeVisitor : NodeVisitor<MapNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(MapNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var map = new MapValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            foreach (var (keyNode, valueNode) in node.Pairs)
            {
                var keyVal = res.Register(await interpreter.Visit(keyNode, context));
                if (res.ShouldReturn()) return res;

                keyVal.SetContext(context).SetPos(keyNode.PositionStart, keyNode.PositionEnd);
                var valueVal = res.Register(await interpreter.Visit(valueNode, context));
                if (res.ShouldReturn()) return res;

                valueVal.SetContext(context).SetPos(valueNode.PositionStart, valueNode.PositionEnd);

                var (setResult, setError) = map.ListSet(keyVal, valueVal);
                if (setError != null) return res.Failure(setError);
            }

            return res.Success(map);
        }
    }
}
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class MapNodeVisitor : NodeVisitor<MapNode>
    {
        protected override RuntimeResult VisitNode(MapNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var map = new MapValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            foreach (var (keyNode, valueNode) in node.Pairs)
            {
                var keyVal = res.Register(interpreter.Visit(keyNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                keyVal.SetContext(context).SetPos(keyNode.PositionStart, keyNode.PositionEnd);
                var valueVal = res.Register(interpreter.Visit(valueNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                valueVal.SetContext(context).SetPos(valueNode.PositionStart, valueNode.PositionEnd);

                var (setResult, setError) = map.ListSet(keyVal, valueVal);
                if (setError != null) return res.Failure(setError);
            }

            return res.Success(map);
        }
    }
}
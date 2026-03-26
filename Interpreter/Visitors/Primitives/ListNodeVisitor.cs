using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class ListNodeVisitor : NodeVisitor<ListNode>
    {
        protected sealed override RuntimeResult VisitNode(ListNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                if (elementNode.NodeType == AstNodeType.Spread)
                {
                    SpreadNode spread = (SpreadNode)elementNode;
                    var val = res.Register(interpreter.Visit(spread.Expression, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    if (val.Type != RuntimeValueType.List)
                    {
                        return res.Failure(new RuntimeError(
                            spread.PositionStart,
                            spread.PositionEnd,
                            "Spread target must be an iterable (e.g. list)",
                            context));
                    }

                    ListValue l = (ListValue)val;
                    elements.AddRange(l.Elements);
                }
                else
                {
                    var val = res.Register(interpreter.Visit(elementNode, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                    elements.Add(val);
                }
            }

            return res.Success(
                new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }
    }
}
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class StringNodeVisitor : NodeVisitor<StringNode>
    {
        protected override RuntimeResult VisitNode(StringNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var sb = new System.Text.StringBuilder();

            foreach (var part in node.Parts)
            {
                if (part.NodeType == AstNodeType.StringPart)
                {
                    sb.Append(((StringTextNode)part).Text);
                }
                else
                {
                    var val = res.Register(interpreter.Visit(part, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    if (val.Type == RuntimeValueType.String)
                        sb.Append(((StringValue)val).Value ?? "");
                    else if (val == null)
                        sb.Append("null");
                    else
                        sb.Append(val.ToString() ?? "");
                }
            }

            return res.Success(new StringValue(sb.ToString()).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
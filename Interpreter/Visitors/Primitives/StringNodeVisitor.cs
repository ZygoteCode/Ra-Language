using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Utilities;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class StringNodeVisitor : NodeVisitor<StringNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(StringNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            // Constant-fold: a string with no interpolation parts evaluates to the same value
            // on every visit. Cache it on the AST node after the first build.
            var cached = node.CachedValue;
            if (cached != null)
            {
                return res.Success(cached.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            var parts = node.Parts;
            bool allLiteral = true;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].NodeType != AstNodeType.StringPart)
                {
                    allLiteral = false;
                    break;
                }
            }

            if (allLiteral)
            {
                // Fast-path constant string: no StringBuilder needed when there is only one
                // literal segment, which is the common case for ordinary string literals.
                string text;
                if (parts.Count == 1)
                {
                    text = ((StringTextNode)parts[0]).Text;
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < parts.Count; i++)
                    {
                        sb.Append(((StringTextNode)parts[i]).Text);
                    }
                    text = sb.ToString();
                }

                var val = new StringValue(text);
                node.CachedValue = val;
                return res.Success(val.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            // Interpolated string: must re-evaluate every visit because embedded expressions
            // depend on dynamic state.
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.NodeType == AstNodeType.StringPart)
                {
                    builder.Append(((StringTextNode)part).Text);
                }
                else
                {
                    var v = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(part, context, interpreter));
                    if (res.ShouldReturn()) return res;

                    if (v == null)
                        builder.Append("null");
                    else
                        builder.Append(StringConversionUtility.ConvertToString(v));
                }
            }

            return res.Success(new StringValue(builder.ToString()).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

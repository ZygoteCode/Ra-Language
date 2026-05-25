using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class SetNodeVisitor : NodeVisitor<SetNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(SetNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(SetNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var elements = new HashSet<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                var val = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(elementNode, context, interpreter));
                if (res.ShouldReturn()) return res;

                bool exists = false;

                foreach (var value in elements)
                {
                    if (val.Equals(value))
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists) continue;
                elements.Add(val);
            }

            return res.Success(
                new SetValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }
    }
}
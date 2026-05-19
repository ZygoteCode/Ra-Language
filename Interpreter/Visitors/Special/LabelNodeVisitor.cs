using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class LabelNodeVisitor : NodeVisitor<LabelNode>
    {
        protected sealed override RuntimeResult VisitNode(LabelNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value.ToString();
            bool alreadyExists = false;
            var index = -1;

            for (int i = 0; i < interpreter.Labels.Count; i++)
            {
                var label = interpreter.Labels[i];

                if (label.Item1.Equals(varName))
                {
                    alreadyExists = true;
                    index = i;
                    break;
                }
            }

            if (alreadyExists) interpreter.Labels.RemoveAt(index);
            interpreter.Labels.Add((varName, node.Statements));
            res.Register(interpreter.Visit(node.Statements, context));
            if (res.ShouldReturn()) return res;
            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
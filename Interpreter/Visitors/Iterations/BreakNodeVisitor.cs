using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Iterations;

namespace RaLanguage.Interpreter.Visitors.Iterations
{
    public class BreakNodeVisitor : NodeVisitor<BreakNode>
    {
        protected sealed override RuntimeResult VisitNode(BreakNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().SuccessBreak();
        }
    }
}
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Iterations;

namespace RaLanguage.Interpreter.Visitors.Iterations
{
    public class ContinueNodeVisitor : NodeVisitor<ContinueNode>
    {
        protected sealed override RuntimeResult VisitNode(ContinueNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().SuccessContinue();
        }
    }
}
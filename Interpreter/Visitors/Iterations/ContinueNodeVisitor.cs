using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Iterations;

namespace RaLanguage.Interpreter.Visitors.Iterations
{
    public class ContinueNodeVisitor : NodeVisitor<ContinueNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ContinueNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(ContinueNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().SuccessContinue();
        }
    }
}
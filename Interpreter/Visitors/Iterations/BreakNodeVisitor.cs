using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Iterations;

namespace RaLanguage.Interpreter.Visitors.Iterations
{
    public class BreakNodeVisitor : NodeVisitor<BreakNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(BreakNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(BreakNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().SuccessBreak();
        }
    }
}
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    public abstract class NodeVisitor<TNode> : INodeVisitor where TNode : AstNode
    {
        public ValueTask<RuntimeResult> Visit(AstNode node, Context context, IInterpreter interpreter)
        {
            return VisitNode((TNode)node, context, interpreter);
        }

        protected abstract ValueTask<RuntimeResult> VisitNode(TNode node, Context context, IInterpreter interpreter);
    }
}

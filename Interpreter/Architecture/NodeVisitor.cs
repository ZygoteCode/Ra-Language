using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    public abstract class NodeVisitor<TNode> : INodeVisitor where TNode : AstNode
    {
        public RuntimeResult Visit(AstNode node, Context context, IInterpreter interpreter)
        {
            return VisitNode((TNode)node, context, interpreter);
        }

        protected abstract RuntimeResult VisitNode(TNode node, Context context, IInterpreter interpreter);
    }
}
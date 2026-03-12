using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionCallNode : AstNode
    {
        public AstNode NodeToCall { get; }
        public List<ArgumentNode> ArgNodes { get; }
        public List<TypeDescriptor?>? GenericTypeArgs { get; }

        public FunctionCallNode(AstNode nodeToCall, List<ArgumentNode> argNodes, List<TypeDescriptor?>? genericTypeArgs = null)
            : base(AstNodeType.FunctionCall)
        {
            NodeToCall = nodeToCall;
            ArgNodes = argNodes ?? new List<ArgumentNode>();
            GenericTypeArgs = genericTypeArgs;

            PositionStart = nodeToCall.PositionStart;

            if (ArgNodes.Count > 0) PositionEnd = ArgNodes[ArgNodes.Count - 1].Expr.PositionEnd;
            else if (GenericTypeArgs != null && GenericTypeArgs.Count > 0) PositionEnd = nodeToCall.PositionEnd;
            else PositionEnd = nodeToCall.PositionEnd;
        }
    }
}
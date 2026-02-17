namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionCallNode : AstNode
    {
        public AstNode NodeToCall { get; }
        public List<AstNode> ArgNodes { get; }

        public FunctionCallNode(AstNode nodeToCall, List<AstNode> argNodes)
        {
            NodeToCall = nodeToCall;
            ArgNodes = argNodes;
            PositionStart = nodeToCall.PositionStart;

            if (argNodes.Count > 0) PositionEnd = argNodes[argNodes.Count - 1].PositionEnd;
            else PositionEnd = nodeToCall.PositionEnd;
        }
    }
}
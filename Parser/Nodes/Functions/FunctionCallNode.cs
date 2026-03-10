namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionCallNode : AstNode
    {
        public AstNode NodeToCall { get; }
        public List<ArgumentNode> ArgNodes { get; }

        public FunctionCallNode(AstNode nodeToCall, List<ArgumentNode> argNodes) : base(AstNodeType.FunctionCall)
        {
            NodeToCall = nodeToCall;
            ArgNodes = argNodes;
            PositionStart = nodeToCall.PositionStart;
            PositionEnd = argNodes.Count > 0 ? argNodes[argNodes.Count - 1].PositionEnd : nodeToCall.PositionEnd;
        }
    }
}
namespace RaLanguage.Parser.Nodes.Functions
{
    public class CallNode : AstNode
    {
        public AstNode NodeToCall { get; }
        public List<AstNode> ArgNodes { get; }

        public CallNode(AstNode nodeToCall, List<AstNode> argNodes)
        {
            NodeToCall = nodeToCall;
            ArgNodes = argNodes;
            PosStart = nodeToCall.PosStart;

            if (argNodes.Count > 0) PosEnd = argNodes[argNodes.Count - 1].PosEnd;
            else PosEnd = nodeToCall.PosEnd;
        }
    }
}
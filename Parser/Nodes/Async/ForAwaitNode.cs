using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Async
{
    public class ForAwaitNode : AstNode
    {
        public Token VarNameToken { get; }
        public AstNode StreamNode { get; }
        public AstNode BodyNode { get; }
        public bool ShouldReturnNull { get; }

        public ForAwaitNode(Token varNameToken, AstNode streamNode, AstNode bodyNode, bool shouldReturnNull) : base(AstNodeType.ForAwait)
        {
            VarNameToken = varNameToken;
            StreamNode = streamNode;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
        }
    }
}

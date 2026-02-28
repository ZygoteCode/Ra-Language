using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Statements
{
    public class ForEachNode : AstNode
    {
        public Token VarNameToken { get; }
        public AstNode CollectionNode { get; }
        public AstNode BodyNode { get; }
        public bool ShouldReturnNull { get; }

        public ForEachNode(Token varNameToken, AstNode collectionNode, AstNode bodyNode, bool shouldReturnNull) : base(AstNodeType.ForEach)
        {
            VarNameToken = varNameToken;
            CollectionNode = collectionNode;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
        }
    }
}
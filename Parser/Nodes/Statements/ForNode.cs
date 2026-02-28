using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Statements
{
    public class ForNode : AstNode
    {
        public Token VarNameTok { get; }
        public AstNode StartValueNode { get; }
        public AstNode EndValueNode { get; }
        public AstNode? StepValueNode { get; }
        public AstNode BodyNode { get; }
        public bool ShouldReturnNull { get; }

        public ForNode(Token varNameTok, AstNode startValueNode, AstNode endValueNode, AstNode? stepValueNode, AstNode bodyNode, bool shouldReturnNull) : base(AstNodeType.For)
        {
            VarNameTok = varNameTok;
            StartValueNode = startValueNode;
            EndValueNode = endValueNode;
            StepValueNode = stepValueNode;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = bodyNode.PositionEnd;
        }
    }
}
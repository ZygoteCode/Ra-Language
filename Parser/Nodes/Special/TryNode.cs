using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Special
{
    public sealed class TryNode : AstNode
    {
        public AstNode TryBody { get; }
        public Token? CatchVarTok { get; }
        public AstNode? CatchBody { get; }
        public AstNode? FinallyBody { get; }

        public TryNode(AstNode tryBody, Token? catchVarTok, AstNode? catchBody, AstNode? finallyBody) : base(AstNodeType.Try)
        {
            TryBody = tryBody;
            CatchVarTok = catchVarTok;
            CatchBody = catchBody;
            FinallyBody = finallyBody;

            PositionStart = tryBody.PositionStart;
            PositionEnd = (finallyBody ?? catchBody ?? tryBody).PositionEnd;
        }
    }
}
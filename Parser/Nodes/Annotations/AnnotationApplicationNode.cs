using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Annotations
{
    public class AnnotationApplicationNode : AstNode
    {
        public Token NameTok { get; }
        public string Name => NameTok.Value?.ToString() ?? string.Empty;
        public List<AstNode> PositionalArgs { get; }
        public List<(Token NameTok, AstNode Value)> NamedArgs { get; }

        public AnnotationApplicationNode(
            Token nameTok,
            List<AstNode> positionalArgs,
            List<(Token, AstNode)> namedArgs,
            Position positionStart,
            Position positionEnd
        ) : base(AstNodeType.AnnotationApplication)
        {
            NameTok = nameTok;
            PositionalArgs = positionalArgs ?? new List<AstNode>();
            NamedArgs = namedArgs ?? new List<(Token, AstNode)>();
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

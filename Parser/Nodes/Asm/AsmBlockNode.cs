using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Asm
{
    public sealed class AsmTextPartNode : AstNode
    {
        public string Text { get; }

        public AsmTextPartNode(string text, Position posStart, Position posEnd) : base(AstNodeType.AsmTextPart)
        {
            Text = text;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }

    public sealed class AsmInterpPartNode : AstNode
    {
        public AstNode Expr { get; }
        public string? TypeHint { get; }

        public AsmInterpPartNode(AstNode expr, string? typeHint, Position posStart, Position posEnd) : base(AstNodeType.AsmInterpPart)
        {
            Expr = expr;
            TypeHint = typeHint;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }

    public sealed class AsmBlockNode : AstNode
    {
        public List<AstNode> Parts { get; }
        public List<string> ReturnTypes { get; set; } = new List<string>();

        public AsmBlockNode(List<AstNode> parts, Position posStart, Position posEnd) : base(AstNodeType.AsmBlock)
        {
            Parts = parts;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Csharp
{
    public sealed class CsharpTextPartNode : AstNode
    {
        public string Text { get; }

        public CsharpTextPartNode(string text, Position posStart, Position posEnd) : base(AstNodeType.CsharpTextPart)
        {
            Text = text;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }

    public sealed class CsharpInterpPartNode : AstNode
    {
        public AstNode Expr { get; }
        public string? TypeHint { get; }

        public CsharpInterpPartNode(AstNode expr, string? typeHint, Position posStart, Position posEnd) : base(AstNodeType.CsharpInterpPart)
        {
            Expr = expr;
            TypeHint = typeHint;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }

    public sealed class CsharpBlockNode : AstNode
    {
        public List<AstNode> Parts { get; }

        public string? ReturnType { get; set; }

        public List<string> Usings { get; set; } = new List<string>();

        public List<string> References { get; set; } = new List<string>();

        public CsharpBlockNode(List<AstNode> parts, Position posStart, Position posEnd) : base(AstNodeType.CsharpBlock)
        {
            Parts = parts;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

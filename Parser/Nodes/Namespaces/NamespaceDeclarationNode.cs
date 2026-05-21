using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Namespaces
{
    public sealed class NamespaceDeclarationNode : AstNode
    {
        public IReadOnlyList<Token> Segments { get; }
        public AstNode Body { get; }
        public bool IsFileScoped { get; }

        public string QualifiedName
        {
            get
            {
                var parts = new string[Segments.Count];
                for (int i = 0; i < Segments.Count; i++)
                    parts[i] = Segments[i].Value?.ToString() ?? "";
                return string.Join(".", parts);
            }
        }

        public NamespaceDeclarationNode(
            IReadOnlyList<Token> segments,
            AstNode body,
            bool isFileScoped,
            Position positionStart,
            Position positionEnd) : base(AstNodeType.NamespaceDeclaration)
        {
            Segments = segments;
            Body = body;
            IsFileScoped = isFileScoped;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Namespaces
{
    public class UsingNamespaceNode : AstNode
    {
        public IReadOnlyList<Token> Segments { get; }
        public Token? AliasTok { get; }

        public bool HasAlias => AliasTok.HasValue;
        public string? Alias => AliasTok?.Value?.ToString();

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

        public UsingNamespaceNode(
            IReadOnlyList<Token> segments,
            Token? aliasTok,
            Position positionStart,
            Position positionEnd) : base(AstNodeType.UsingNamespace)
        {
            Segments = segments;
            AliasTok = aliasTok;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

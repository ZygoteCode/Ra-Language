using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Operations
{
    // Created by `*expr`. The inner expression must evaluate to a reference / borrow
    // RuntimeValue. The visitor reads (and, when used as an assignment target, writes)
    // the referenced storage.
    public sealed class DereferenceNode : AstNode
    {
        public AstNode Target { get; }

        public DereferenceNode(AstNode target, Position posStart, Position posEnd)
            : base(AstNodeType.Dereference)
        {
            Target = target;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

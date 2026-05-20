using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Operations
{
    // Created by `&expr` (shared borrow) or `&mut expr` (exclusive borrow).
    // Target must resolve to a place expression — a variable, a field, an indexed
    // element, etc. The visitor is responsible for verifying this and producing a
    // BorrowValue (a RuntimeValue that retains a back-pointer to the borrowed entry).
    public class BorrowNode : AstNode
    {
        public AstNode Target { get; }
        public bool IsMutable { get; }
        public string? Lifetime { get; }

        public BorrowNode(AstNode target, bool isMutable, Position posStart, Position posEnd, string? lifetime = null)
            : base(AstNodeType.Borrow)
        {
            Target = target;
            IsMutable = isMutable;
            Lifetime = lifetime;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

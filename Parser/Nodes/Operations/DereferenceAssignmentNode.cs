using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    // `*ref = value` / `*ref += value` — write through a borrow / reference.
    // RefTarget evaluates to an IReferenceValue at runtime; the visitor then
    // assigns through it, observing borrow mutability (BorrowValue blocks writes
    // through a shared `&` borrow, etc.).
    public class DereferenceAssignmentNode : AstNode
    {
        public AstNode RefTarget { get; }
        public Token AssignmentToken { get; }
        public AstNode ValueNode { get; }

        public DereferenceAssignmentNode(AstNode refTarget, Token assignmentToken, AstNode valueNode, Position posStart, Position posEnd)
            : base(AstNodeType.DereferenceAssignment)
        {
            RefTarget = refTarget;
            AssignmentToken = assignmentToken;
            ValueNode = valueNode;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

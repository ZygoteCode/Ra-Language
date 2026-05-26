using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    // `expr with { name1: value1, name2: value2 }`
    //
    // Returns a shallow copy of the receiver record instance with the
    // listed primary fields replaced. Receiver MUST evaluate to a
    // record instance at runtime; mismatched names, mismatched types,
    // or duplicate keys raise a runtime error with a position pinned
    // on the offending pair.
    public sealed class WithExpressionNode : AstNode
    {
        public AstNode Receiver { get; }
        public List<(Token NameTok, AstNode Value)> Updates { get; }

        public WithExpressionNode(AstNode receiver, List<(Token, AstNode)> updates) : base(AstNodeType.WithExpression)
        {
            Receiver = receiver;
            Updates = updates;
            PositionStart = receiver.PositionStart;
            PositionEnd = updates.Count > 0 ? updates[^1].Item2.PositionEnd : receiver.PositionEnd;
        }
    }
}

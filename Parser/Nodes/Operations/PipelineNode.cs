using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Operations
{
    // F# / Elixir style pipeline `value |> callable`. The left-hand expression
    // becomes the implicit first positional argument of the right-hand call.
    // The shape stays explicit (LeftNode + RightNode + op token) so error
    // reporting can point at the operator and so future extensions (placeholder
    // `_`, async pipelines, partial application) can fold into this node
    // without disturbing call sites.
    public sealed class PipelineNode : AstNode
    {
        public AstNode LeftNode { get; }
        public AstNode RightNode { get; }
        public Lexer.Tokens.Token PipeToken { get; }

        public PipelineNode(AstNode leftNode, AstNode rightNode, Lexer.Tokens.Token pipeToken)
            : base(AstNodeType.Pipeline)
        {
            LeftNode = leftNode;
            RightNode = rightNode;
            PipeToken = pipeToken;
            PositionStart = leftNode.PositionStart;
            PositionEnd = rightNode.PositionEnd;
        }
    }
}

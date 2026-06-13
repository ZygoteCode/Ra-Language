using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class ListAccessNode : AstNode
    {
        public AstNode Target { get; }

        // The first (and, for the common case, only) index. Kept so every
        // existing single-index consumer reads `.Index` unchanged.
        public AstNode Index { get; }

        // All indices, in order. Count == 1 is the native single-index fast
        // path (`OP_LIST_GET`); Count > 1 is a multi-parameter indexer
        // (`obj[a, b]`) which lowers to an `op_index(a, b, …)` method call.
        public IReadOnlyList<AstNode> Indices { get; }

        public bool IsMulti => Indices.Count > 1;

        public ListAccessNode(AstNode target, AstNode index, Position positionStart, Position positionEnd) : base(AstNodeType.ListAccess)
        {
            Target = target;
            Index = index;
            Indices = new AstNode[] { index };
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }

        public ListAccessNode(AstNode target, IReadOnlyList<AstNode> indices, Position positionStart, Position positionEnd) : base(AstNodeType.ListAccess)
        {
            Target = target;
            Indices = indices;
            Index = indices[0];
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

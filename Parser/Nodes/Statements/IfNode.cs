namespace RaLanguage.Parser.Nodes.Statements
{
    public sealed class IfNode : AstNode
    {
        public List<(AstNode Condition, AstNode Expr, bool ShouldReturnNull)> Cases { get; }
        public (AstNode Expr, bool ShouldReturnNull)? ElseCase { get; }

        // One-bit-per-branch cache of `AstScopeAnalysis.NeedsFreshScope(body)`. The
        // analysis only depends on the AST shape (which is immutable post-parse), so
        // a single computation per branch is sufficient. Encoded with a separate
        // `_needsScopeComputed` bitmask so we can distinguish "not yet computed"
        // from "false". Index N+1 == else-branch when ElseCase is set. 32 branches
        // covers any realistic if/elif/elif/.../else chain; pathologically long
        // chains fall through to the uncached path.
        private uint _needsScopeBits;
        private uint _needsScopeComputed;

        public bool BranchNeedsScope(int index, AstNode body)
        {
            if (index >= 32) return AstScopeAnalysis.NeedsFreshScope(body);

            uint mask = 1u << index;
            if ((_needsScopeComputed & mask) != 0)
                return (_needsScopeBits & mask) != 0;

            bool needs = AstScopeAnalysis.NeedsFreshScope(body);
            if (needs) _needsScopeBits |= mask;
            _needsScopeComputed |= mask;
            return needs;
        }

        public IfNode(List<(AstNode, AstNode, bool)> cases, (AstNode, bool)? elseCase) : base(AstNodeType.If)
        {
            Cases = cases;
            ElseCase = elseCase;
            PositionStart = cases[0].Item1.PositionStart;

            if (elseCase != null)
                PositionEnd = elseCase.Value.Item1.PositionEnd;
            else
                PositionEnd = cases[cases.Count - 1].Item2.PositionEnd;
        }
    }
}
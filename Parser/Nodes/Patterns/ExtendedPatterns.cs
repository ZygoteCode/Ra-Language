using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Patterns
{
    // Disjunction. Each alternative is tried in order; bindings introduced by
    // a failing alternative are rolled back before the next alternative is
    // tried. Every alternative must bind the same set of names (enforced by
    // the analyzer; the runtime tolerates a mismatch but the body's reference
    // to an unbound name will fail at the symbol-table lookup).
    public sealed class OrPatternNode : PatternNode
    {
        public List<PatternNode> Alternatives { get; }
        public OrPatternNode(List<PatternNode> alternatives, Position s, Position e) : base(s, e)
        {
            Alternatives = alternatives;
        }
    }

    // Numeric / string-ordered range. Lo and Hi are pure literal AstNodes
    // (NumberNode / StringNode / unary-minus over NumberNode). Either bound
    // can be null for an open-ended range. IsInclusive applies to the high
    // bound (matches Rust's '..=').
    //   1..10     -> Lo=1, Hi=10, IsInclusive=false
    //   1..=10    -> Lo=1, Hi=10, IsInclusive=true
    //   ..10      -> Lo=null, Hi=10, IsInclusive=false
    //   ..=10     -> Lo=null, Hi=10, IsInclusive=true
    //   5..       -> Lo=5, Hi=null, IsInclusive=false (semantically open above)
    public sealed class RangePatternNode : PatternNode
    {
        public AstNode? Lo { get; }
        public AstNode? Hi { get; }
        public bool IsInclusive { get; }
        public RangePatternNode(AstNode? lo, AstNode? hi, bool isInclusive, Position s, Position e) : base(s, e)
        {
            Lo = lo;
            Hi = hi;
            IsInclusive = isInclusive;
        }
    }

    // Comparison-against-literal at pattern position: '< 5', '>= 0', '!= -1'.
    // Op is the comparison token type (LT / LTE / GT / GTE / EE / NE).
    // Operand is a pure literal AstNode evaluated once at engine entry.
    public sealed class RelationalPatternNode : PatternNode
    {
        public TokenType Op { get; }
        public AstNode Operand { get; }
        public RelationalPatternNode(TokenType op, AstNode operand, Position s, Position e) : base(s, e)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 'Pattern as name' — bind the whole scrutinee at this position to 'name'
    // after the inner pattern succeeds. Compositional: any pattern can be
    // aliased. Pre-existing TypePatternNode keeps its own optional binder for
    // backward compatibility; new code can equivalently write 'is T as v'
    // which the parser still routes to TypePatternNode (faster path).
    public sealed class AliasPatternNode : PatternNode
    {
        public PatternNode Inner { get; }
        public string BinderName { get; }
        public AliasPatternNode(PatternNode inner, string binderName, Position s, Position e) : base(s, e)
        {
            Inner = inner;
            BinderName = binderName;
        }
    }

    // Map destructuring: '{ "key1": pat1, "key2": pat2, .. }'. HasOpenRest
    // when '..' appears as the last entry — extra keys are then ignored.
    // Without '..' the pattern is closed (key set equality required); an
    // open-rest is the idiomatic choice for partial-shape checks.
    public sealed class MapPatternNode : PatternNode
    {
        public List<(AstNode Key, PatternNode Value)> Entries { get; }
        public bool HasOpenRest { get; }
        public MapPatternNode(List<(AstNode, PatternNode)> entries, bool hasOpenRest, Position s, Position e) : base(s, e)
        {
            Entries = entries;
            HasOpenRest = hasOpenRest;
        }
    }

    // Negation: 'not P' matches iff P fails to match. The inner pattern
    // may not introduce bindings (they would have nothing to refer to
    // since the inner walk did not succeed); the parser rejects bindings
    // inside a 'not' pattern with a clear error.
    public sealed class NotPatternNode : PatternNode
    {
        public PatternNode Inner { get; }
        public NotPatternNode(PatternNode inner, Position s, Position e) : base(s, e)
        {
            Inner = inner;
        }
    }

    // Intersection: 'P1 & P2' matches iff BOTH P1 and P2 match. Bindings
    // from both sides are committed to the arm scope; if both sides bind
    // the same name, the right alternative wins (it appears later in
    // source order and the bindings list is order-dependent).
    public sealed class AndPatternNode : PatternNode
    {
        public List<PatternNode> Conjuncts { get; }
        public AndPatternNode(List<PatternNode> conjuncts, Position s, Position e) : base(s, e)
        {
            Conjuncts = conjuncts;
        }
    }
}

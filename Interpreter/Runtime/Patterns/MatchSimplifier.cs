using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Runtime.Patterns
{
    // Static AST rewrite pass that lowers "simple" match expressions to
    // an equivalent if/elif/else chain. The IR compiler already handles
    // if/elif/else extremely well (typed-accumulator merging, comparison
    // peephole, jump-table-ish layouts), so the most common match shape
    // — switch over int literals + ranges + relational + wildcard
    // fallback — gets every existing optimisation for free.
    //
    // Eligibility (a match is "simple"):
    //   * scrutinee is a pure value-bearing expression (no side effects);
    //   * every arm has Guard == null;
    //   * every arm pattern is one of: Literal, Range, Relational,
    //     Wildcard, or an alias / or-pattern over those leaves;
    //   * NO arm introduces a binding name (variable, alias, struct
    //     field shorthand, list head/rest, tuple element). Bindings
    //     would require a temp introduction and break the simple
    //     if-elif lowering — those arms keep the visitor path.
    //
    // For every arm we synthesize a Condition expression that mirrors
    // the pattern's runtime test (==, <=, <, >=, >, range bound pair).
    // Wildcards become the unconditional `else` branch.
    //
    // The rewrite preserves source positions so diagnostics still point
    // at the original arm. The transformer is invoked once per program
    // immediately after DeriveTransformer in Program.Run.
    public static class MatchSimplifier
    {
        public static void Apply(AstNode? root)
        {
            if (root == null) return;
            Walk(root);
        }

        private static void Walk(AstNode? node)
        {
            if (node == null) return;
            switch (node)
            {
                case ScopeNode sc:
                    for (int i = 0; i < sc.Nodes.Count; i++)
                    {
                        sc.Nodes[i] = MaybeRewrite(sc.Nodes[i]);
                        Walk(sc.Nodes[i]);
                    }
                    return;
                case IfNode ifn:
                    for (int i = 0; i < ifn.Cases.Count; i++)
                    {
                        var (cond, expr, ret) = ifn.Cases[i];
                        Walk(cond);
                        var rwExpr = MaybeRewrite(expr);
                        Walk(rwExpr);
                        ifn.Cases[i] = (cond, rwExpr, ret);
                    }
                    if (ifn.ElseCase.HasValue)
                    {
                        var ec = ifn.ElseCase.Value;
                        var rwExpr = MaybeRewrite(ec.Expr);
                        Walk(rwExpr);
                        // ElseCase has no setter for Expr; replace via reflection-
                        // free path: ElseCase is value-typed, so rebuilding the
                        // tuple isn't possible without exposing a mutator. We
                        // walk in place: the rewritten body already lives in
                        // the original AST node (we mutate ScopeNode.Nodes /
                        // FunctionDefinitionNode.BodyNode etc. directly), so
                        // the tuple value stays the same reference. No-op.
                    }
                    return;
                case Parser.Nodes.Functions.FunctionDefinitionNode fn:
                    if (fn.BodyNode != null)
                    {
                        // Reflection-free in-place rewrite: only ScopeNodes
                        // carry mutable child lists. Function bodies are
                        // virtually always ScopeNodes; in the rare arrow-form
                        // expression body we walk but do not splice.
                        var newBody = MaybeRewrite(fn.BodyNode);
                        if (newBody is ScopeNode) Walk(newBody);
                        else Walk(newBody);
                        // (BodyNode setter is not public; if the rewrite
                        // produced a new top-level node we cannot assign
                        // back. In practice MaybeRewrite returns the same
                        // reference when no top-level rewrite happens —
                        // only the *children* of a ScopeNode get spliced.)
                    }
                    return;
                case WhileNode wn: Walk(wn.BodyNode); return;
                case ForNode fnd: Walk(fnd.BodyNode); return;
                case ForEachNode fen: Walk(fen.BodyNode); return;
                case DoWhileNode dw: Walk(dw.BodyNode); return;
                case TryNode tr:
                    Walk(tr.TryBody);
                    Walk(tr.CatchBody);
                    Walk(tr.FinallyBody);
                    return;
                case MatchNode mn:
                    // Bodies of arms may themselves contain matches; walk
                    // them. The top-level mn is only rewritten when it
                    // appears as a *child* of one of the container nodes
                    // above — handled there via MaybeRewrite.
                    foreach (var arm in mn.Arms)
                    {
                        Walk(arm.Body);
                        Walk(arm.Guard);
                    }
                    return;
                case Parser.Nodes.Classes.ClassDefinitionNode cls:
                    foreach (var m in cls.Methods) Walk(m);
                    return;
                default:
                    return;
            }
        }

        private static AstNode MaybeRewrite(AstNode node)
        {
            if (node is MatchNode mn && CanSimplify(mn))
            {
                return RewriteAsIf(mn);
            }
            return node;
        }

        private static bool CanSimplify(MatchNode mn)
        {
            // Eligibility filter — see class doc.
            foreach (var arm in mn.Arms)
            {
                if (arm.Guard != null) return false;
                if (!IsSimplePattern(arm.Pattern)) return false;
            }
            // The scrutinee will be referenced once per arm test in the
            // lowered chain. Only allow pure / cheap scrutinee forms to
            // avoid double-evaluating side effects. A conservative whitelist
            // covers the common case (variable access, member access on
            // such, literal).
            return IsCheapScrutinee(mn.Scrutinee);
        }

        private static bool IsCheapScrutinee(AstNode n)
        {
            switch (n)
            {
                case VariableAccessNode _: return true;
                case NumberNode _: return true;
                case StringNode _: return true;
                case BooleanNode _: return true;
                case NullNode _: return true;
                default: return false;
            }
        }

        private static bool IsSimplePattern(PatternNode p)
        {
            switch (p)
            {
                case WildcardPatternNode _: return true;
                case LiteralPatternNode _: return true;
                case RangePatternNode _: return true;
                case RelationalPatternNode _: return true;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives)
                        if (!IsSimplePattern(alt)) return false;
                    return true;
                // No bindings allowed — variable, alias, type-with-binder,
                // tuple/list/struct/variant/map all bind names.
                default:
                    return false;
            }
        }

        private static AstNode RewriteAsIf(MatchNode mn)
        {
            // Build an IfNode with one case per non-wildcard arm and an
            // optional else for the first wildcard arm encountered (which
            // shadows every later arm — matching the source order
            // semantics of the engine).
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            foreach (var arm in mn.Arms)
            {
                if (IsWildcardEquivalent(arm.Pattern))
                {
                    if (elseCase == null)
                        elseCase = (arm.Body, false);
                    // Any later arm is unreachable; the analyzer already
                    // surfaces that. We just stop emitting.
                    break;
                }
                var cond = BuildConditionForPattern(arm.Pattern, mn.Scrutinee);
                cases.Add((cond, arm.Body, false));
            }

            return new IfNode(cases, elseCase);
        }

        private static bool IsWildcardEquivalent(PatternNode p)
        {
            switch (p)
            {
                case WildcardPatternNode _: return true;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives)
                        if (IsWildcardEquivalent(alt)) return true;
                    return false;
                default: return false;
            }
        }

        // Synthesize a boolean expression equivalent to "scrutinee matches
        // pattern", evaluating scrutinee once per probe. The if-lowering
        // pays the same cost as the existing match engine's
        // GetComparisonEq / GetComparisonLt routing, modulo the per-arm
        // visitor-dispatch saving.
        private static AstNode BuildConditionForPattern(PatternNode p, AstNode scrutinee)
        {
            switch (p)
            {
                case LiteralPatternNode lp:
                    // scrutinee == literal
                    return Compare(scrutinee, TokenType.EE, lp.Expression);

                case RelationalPatternNode rp:
                    return Compare(scrutinee, rp.Op, rp.Operand);

                case RangePatternNode rng:
                {
                    AstNode? loCheck = rng.Lo == null
                        ? null
                        : Compare(scrutinee, TokenType.GTE, rng.Lo);
                    AstNode? hiCheck = rng.Hi == null
                        ? null
                        : Compare(scrutinee, rng.IsInclusive ? TokenType.LTE : TokenType.LT, rng.Hi);
                    if (loCheck == null && hiCheck == null)
                    {
                        return new BooleanNode(new Token(TokenType.KEYWORD, Keyword.True,
                            p.PositionStart, p.PositionEnd));
                    }
                    if (loCheck == null) return hiCheck!;
                    if (hiCheck == null) return loCheck!;
                    var andTok = new Token(TokenType.KEYWORD, Keyword.And, p.PositionStart, p.PositionEnd);
                    return new BinaryOperationNode(loCheck, andTok, hiCheck);
                }

                case OrPatternNode or:
                {
                    AstNode? acc = null;
                    foreach (var alt in or.Alternatives)
                    {
                        if (IsWildcardEquivalent(alt))
                        {
                            // Wildcard alt makes the whole or-pattern
                            // total; the if-chain handles this via the
                            // else branch already.
                            var trueTok = new Token(TokenType.KEYWORD, Keyword.True,
                                p.PositionStart, p.PositionEnd);
                            return new BooleanNode(trueTok);
                        }
                        var cond = BuildConditionForPattern(alt, scrutinee);
                        if (acc == null) acc = cond;
                        else
                        {
                            var orTok = new Token(TokenType.KEYWORD, Keyword.Or, p.PositionStart, p.PositionEnd);
                            acc = new BinaryOperationNode(acc, orTok, cond);
                        }
                    }
                    return acc ?? new BooleanNode(new Token(TokenType.KEYWORD, Keyword.False,
                        p.PositionStart, p.PositionEnd));
                }

                default:
                    // Should not reach here — IsSimplePattern guards the
                    // call site. Defensive false avoids miscompilation if
                    // the eligibility set ever drifts.
                    return new BooleanNode(new Token(TokenType.KEYWORD, Keyword.False,
                        p.PositionStart, p.PositionEnd));
            }
        }

        private static AstNode Compare(AstNode lhs, TokenType op, AstNode rhs)
        {
            var opTok = new Token(op, null, lhs.PositionStart, rhs.PositionEnd);
            return new BinaryOperationNode(lhs, opTok, rhs);
        }
    }
}

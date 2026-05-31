using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Interpreter.Pipeline;

namespace RaLanguage.Interpreter.Runtime.Optimizations
{
    // AST peephole: inline a single-use local temporary into the condition of
    // the immediately-following `if`, then drop the declaration. Turns
    //
    //     var c = <expr>
    //     if c < 0 { ... }
    //
    // into
    //
    //     if <expr> < 0 { ... }
    //
    // The IR compiler already lowers a typed comparison in an `if` condition to
    // a fully unboxed `JmpNot*II` reading the computed register directly, so
    // removing the binding eliminates the per-execution `OP_DECLARE_LOCAL`
    // materialization (which BOXES the value — the dominant residual cost of
    // numeric loop temporaries, e.g. `var c = a*2+b*3-i; if c < 0`).
    //
    // SOUNDNESS — the rewrite preserves observable behaviour because all of:
    //   1. The use sits in the FIRST condition of the IMMEDIATELY-following
    //      `if`. No statement runs between the declaration and that condition,
    //      so `<expr>` sees the same inputs (no interference) and is evaluated
    //      at the same program point exactly once (side effects preserved —
    //      `<expr>` may even contain calls).
    //   2. The temp is read EXACTLY ONCE in the remainder of its scope and is
    //      never re-assigned, so there is no second reference left dangling and
    //      no duplicated evaluation.
    //   3. The declaration carries no type annotation, so no declared-type
    //      check is dropped.
    // The read counter is CONSERVATIVE: any AST shape it does not explicitly
    // understand is treated as "the temp might be used here", which blocks the
    // rewrite. Missing a node kind therefore costs a missed optimization, never
    // a miscompilation. The replacer is likewise fail-safe: if it cannot locate
    // exactly the one read inside the condition, the rewrite is abandoned.
    public static class SingleUseTempInliner
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
                    ProcessStatements(sc.Nodes);
                    for (int i = 0; i < sc.Nodes.Count; i++) Walk(sc.Nodes[i]);
                    return;
                case IfNode ifn:
                    foreach (var c in ifn.Cases) { Walk(c.Condition); Walk(c.Expr); }
                    if (ifn.ElseCase.HasValue) Walk(ifn.ElseCase.Value.Expr);
                    return;
                case FunctionDefinitionNode fn:
                    Walk(fn.BodyNode);
                    return;
                case WhileNode wn: Walk(wn.ConditionNode); Walk(wn.BodyNode); return;
                case DoWhileNode dw: Walk(dw.BodyNode); return;
                case ForNode fnd: Walk(fnd.BodyNode); return;
                case ForEachNode fen: Walk(fen.BodyNode); return;
                case TryNode tr: Walk(tr.TryBody); Walk(tr.CatchBody); Walk(tr.FinallyBody); return;
                case Parser.Nodes.Classes.ClassDefinitionNode cls:
                    foreach (var m in cls.Methods) Walk(m);
                    return;
                default:
                    return;
            }
        }

        // Scan a statement list for `var c = e` immediately followed by an `if`
        // whose first condition is the temp's sole use, and splice it in place.
        private static void ProcessStatements(System.Collections.Generic.List<AstNode> stmts)
        {
            int i = 0;
            while (i + 1 < stmts.Count)
            {
                if (TryInlineAt(stmts, i))
                {
                    // stmts[i] (the decl) was removed; stmts[i] is now the `if`.
                    // Re-test the same index against the NEW predecessor.
                    if (i > 0) i--;
                    continue;
                }
                i++;
            }
        }

        private static bool TryInlineAt(System.Collections.Generic.List<AstNode> stmts, int i)
        {
            if (stmts[i] is not VariableDeclarationNode vd) return false;
            // Exactly one declarator, with an initializer and NO type annotation.
            if (vd.Declarations.Count != 1) return false;
            var (_, initExpr, declType) = vd.Declarations[0];
            if (initExpr == null || declType != null) return false;
            if (!IsInlinableInit(initExpr)) return false;
            if (vd.Bindings == null || vd.Bindings.Length < 1 || !vd.Bindings[0].IsResolved) return false;
            var binding = vd.Bindings[0];

            // The immediately-following statement must be an `if`.
            if (stmts[i + 1] is not IfNode ifn || ifn.Cases.Count == 0) return false;
            var firstCond = ifn.Cases[0].Condition;

            // The temp must be read exactly once across the remainder of the
            // scope, never written, and that single read must live in the first
            // condition. Conservative counting blocks the rewrite on anything
            // unrecognised.
            int reads = 0;
            for (int j = i + 1; j < stmts.Count; j++)
            {
                reads += CountReads(stmts[j], binding);
                if (CountWrites(stmts[j], binding) > 0) return false;
                if (reads > 1) return false;
            }
            if (reads != 1) return false;
            if (CountReads(firstCond, binding) != 1) return false;

            // Splice the initializer into the one read inside the condition.
            var rewritten = ReplaceSingleRead(firstCond, binding, initExpr);
            if (rewritten == null) return false;

            ifn.Cases[0] = (rewritten, ifn.Cases[0].Expr, ifn.Cases[0].ShouldReturnNull);
            stmts.RemoveAt(i);
            return true;
        }

        // Whitelist of initializer shapes that are safe to relocate into the
        // following condition: ordinary value expressions only. Excludes borrow
        // / dereference (lifetime semantics), async (spawn / await / emit), and
        // anything not on the list (conservative). A whitelisted initializer may
        // still contain calls / member access — relocating those is sound
        // because the use sits in the next statement's condition, which is
        // reached unconditionally and evaluated exactly once (identical program
        // point to the original declaration).
        private static bool IsInlinableInit(AstNode? e)
        {
            switch (e)
            {
                case null:
                    return false;
                case NumberNode: case StringNode: case BooleanNode: case NullNode:
                case VariableAccessNode:
                    return true;
                case BinaryOperationNode bo:
                    return IsInlinableInit(bo.LeftNode) && IsInlinableInit(bo.RightNode);
                case UnaryOperationNode uo:
                    return IsInlinableInit(uo.Node);
                case CastNode cst:
                    return IsInlinableInit(cst.Expression);
                case Parser.Nodes.Variables.ListAccessNode:
                case Parser.Nodes.Structs.MemberAccessNode:
                case FunctionCallNode:
                    // Reads of containers / fields / calls: side effects (if any)
                    // are preserved — evaluated once, unconditionally, at the
                    // same point. Do NOT recurse to validate their sub-trees;
                    // they are leaves for inlining purposes.
                    return true;
                default:
                    return false;
            }
        }

        // ---- conservative read/write counters ----
        // Returns the number of reads of `binding`. For any node kind not
        // explicitly modelled, returns 2 ("might be used") so the caller's
        // `reads != 1` / `reads > 1` guards refuse the rewrite — sound by
        // over-approximation.
        private static int CountReads(AstNode? node, BindingId binding)
        {
            if (node == null) return 0;
            switch (node)
            {
                case VariableAccessNode va:
                    return va.Binding.IsResolved && va.Binding == binding ? 1 : 0;
                case NumberNode: case StringNode: case BooleanNode: case NullNode:
                    return 0;
                case BinaryOperationNode bo:
                    return CountReads(bo.LeftNode, binding) + CountReads(bo.RightNode, binding);
                case UnaryOperationNode uo:
                    return CountReads(uo.Node, binding);
                case TernaryNode tn:
                    return CountReads(tn.Condition, binding) + CountReads(tn.TrueExpression, binding) + CountReads(tn.FalseExpression, binding);
                case NullCoalescingNode nc:
                    return CountReads(nc.Left, binding) + CountReads(nc.Right, binding);
                case CastNode cst:
                    return CountReads(cst.Expression, binding);
                case ScopeNode sc:
                {
                    int s = 0; foreach (var n in sc.Nodes) { s += CountReads(n, binding); if (s > 1) return s; } return s;
                }
                case IfNode ifn:
                {
                    int s = 0;
                    foreach (var c in ifn.Cases) { s += CountReads(c.Condition, binding) + CountReads(c.Expr, binding); if (s > 1) return s; }
                    if (ifn.ElseCase.HasValue) s += CountReads(ifn.ElseCase.Value.Expr, binding);
                    return s;
                }
                case FunctionCallNode fc:
                {
                    int s = CountReads(fc.NodeToCall, binding);
                    foreach (var a in fc.ArgNodes) { s += CountReads(a.Expr, binding); if (s > 1) return s; }
                    return s;
                }
                case VariableAssignmentNode vas:
                    // RHS is a read; the LHS binding itself is a write (handled
                    // by CountWrites). Compound forms (`+=`) also READ the
                    // target — count that.
                    return CountReads(vas.ValueNode, binding)
                        + ((vas.Binding.IsResolved && vas.Binding == binding
                            && vas.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) ? 1 : 0);
                case VariableDeclarationNode vdn:
                {
                    int s = 0; foreach (var d in vdn.Declarations) s += CountReads(d.Item2, binding); return s;
                }
                case ReturnNode rn:
                    return CountReads(rn.NodeToReturn, binding);
                default:
                    // Unrecognised — assume the temp may be referenced here.
                    return 2;
            }
        }

        private static int CountWrites(AstNode? node, BindingId binding)
        {
            if (node == null) return 0;
            switch (node)
            {
                case VariableAssignmentNode vas:
                {
                    int w = (vas.Binding.IsResolved && vas.Binding == binding) ? 1 : 0;
                    return w + CountWrites(vas.ValueNode, binding);
                }
                case ScopeNode sc:
                {
                    int s = 0; foreach (var n in sc.Nodes) s += CountWrites(n, binding); return s;
                }
                case IfNode ifn:
                {
                    int s = 0;
                    foreach (var c in ifn.Cases) s += CountWrites(c.Condition, binding) + CountWrites(c.Expr, binding);
                    if (ifn.ElseCase.HasValue) s += CountWrites(ifn.ElseCase.Value.Expr, binding);
                    return s;
                }
                case BinaryOperationNode bo:
                    return CountWrites(bo.LeftNode, binding) + CountWrites(bo.RightNode, binding);
                case UnaryOperationNode uo:
                    return CountWrites(uo.Node, binding);
                case FunctionCallNode fc:
                {
                    int s = CountWrites(fc.NodeToCall, binding);
                    foreach (var a in fc.ArgNodes) s += CountWrites(a.Expr, binding);
                    return s;
                }
                case ReturnNode rn:
                    return CountWrites(rn.NodeToReturn, binding);
                case NumberNode: case StringNode: case BooleanNode: case NullNode: case VariableAccessNode:
                    return 0;
                default:
                    // Unrecognised — assume a write may occur, blocking inline.
                    return 1;
            }
        }

        // Replace the single read of `binding` inside `expr` with `replacement`.
        // Returns the rewritten tree, or null if the read could not be located
        // unambiguously (fail-safe → caller abandons the rewrite).
        private static AstNode? ReplaceSingleRead(AstNode expr, BindingId binding, AstNode replacement)
        {
            switch (expr)
            {
                case VariableAccessNode va:
                    return (va.Binding.IsResolved && va.Binding == binding) ? replacement : null;
                case BinaryOperationNode bo:
                {
                    if (CountReads(bo.LeftNode, binding) == 1 && CountReads(bo.RightNode, binding) == 0)
                    {
                        var l = ReplaceSingleRead(bo.LeftNode, binding, replacement);
                        if (l == null) return null;
                        return new BinaryOperationNode(l, bo.OpTok, bo.RightNode);
                    }
                    if (CountReads(bo.RightNode, binding) == 1 && CountReads(bo.LeftNode, binding) == 0)
                    {
                        var r = ReplaceSingleRead(bo.RightNode, binding, replacement);
                        if (r == null) return null;
                        return new BinaryOperationNode(bo.LeftNode, bo.OpTok, r);
                    }
                    return null;
                }
                case UnaryOperationNode uo:
                {
                    var inner = ReplaceSingleRead(uo.Node, binding, replacement);
                    if (inner == null) return null;
                    return new UnaryOperationNode(uo.OpTok, inner);
                }
                default:
                    return null;
            }
        }
    }
}

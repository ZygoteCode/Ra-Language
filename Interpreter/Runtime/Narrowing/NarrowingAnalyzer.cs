using System.Collections.Generic;
using System.Linq;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Narrowing
{
    // Compile-time analysis pass that reasons about declared types and the
    // refinements induced by `is`-tests / match type-patterns. Two
    // user-visible outputs:
    //
    //   1. `is`-test diagnostics — calls out impossible tests
    //      (`x: int; x is string` ⇒ never true), and trivially-true ones
    //      (`x: int; x is int` ⇒ always true). These do not block
    //      compilation but help users catch dead branches early.
    //
    //   2. Union match-exhaustiveness — when the scrutinee of a `match` is
    //      a variable whose declared type is `T1 | T2 | … | Tn`, every
    //      member must be covered by a `case is Ti -> …` arm (or a
    //      wildcard / bare-binding fallback). Reports missing alternatives
    //      with the exact set of types still uncovered.
    //
    // The pass is conservative: it never asserts narrowing in code we
    // cannot statically prove. When the scrutinee's declared type is
    // unknown (no `let x: T = …` in scope) we emit nothing rather than
    // guess. This keeps the analyzer's recommendations actionable and
    // free of false positives.
    public static class NarrowingAnalyzer
    {
        public static List<StaticAnalyzerDiagnostic> Analyze(AstNode? root)
        {
            var diags = new List<StaticAnalyzerDiagnostic>();
            if (root == null) return diags;
            var state = new State();
            state.PushScope();
            Walk(root, state, diags);
            state.PopScope();
            return diags;
        }

        // Per-pass mutable state: a stack of scopes, each holding the
        // declared TypeDescriptors that came into existence inside that
        // scope. Lookup walks the stack top-down so the closest binding
        // wins (shadowing semantics).
        private sealed class State
        {
            public readonly List<Dictionary<string, TypeDescriptor>> Scopes = new();

            public void PushScope() => Scopes.Add(new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal));
            public void PopScope() => Scopes.RemoveAt(Scopes.Count - 1);

            public void Declare(string name, TypeDescriptor type)
            {
                if (Scopes.Count == 0) return;
                Scopes[Scopes.Count - 1][name] = type;
            }

            public TypeDescriptor? Lookup(string name)
            {
                for (int i = Scopes.Count - 1; i >= 0; i--)
                    if (Scopes[i].TryGetValue(name, out var t)) return t;
                return null;
            }

            // Reassignment to a name shadows whatever was known. Because Ra
            // assignments are duck-typed at the source — we can't recover
            // the new RHS's type statically without a full inference pass —
            // we forget the prior refinement to avoid asserting a stale
            // claim. Future expansion (full flow analysis) can replace this
            // with a per-block join.
            public void Invalidate(string name)
            {
                for (int i = Scopes.Count - 1; i >= 0; i--)
                {
                    if (Scopes[i].ContainsKey(name)) { Scopes[i].Remove(name); return; }
                }
            }
        }

        // -------------------- AST walk --------------------

        private static void Walk(AstNode? node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (node == null) return;
            switch (node)
            {
                case ScopeNode scope:
                    state.PushScope();
                    foreach (var n in scope.Nodes) Walk(n, state, diags);
                    state.PopScope();
                    break;

                case VariableDeclarationNode vd:
                    foreach (var (nameTok, init, declType) in vd.Declarations)
                    {
                        if (declType != null)
                        {
                            var name = nameTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(name))
                                state.Declare(name!, declType);
                        }
                        Walk(init, state, diags);
                    }
                    break;

                case VariableAssignmentNode va:
                    // Mutation: drop any prior refinement we held for the
                    // target name. The RHS expression may still contain
                    // sub-expressions worth walking (e.g. nested `is`
                    // tests), so descend into it before invalidating.
                    Walk(va.ValueNode, state, diags);
                    var assignName = va.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(assignName)) state.Invalidate(assignName!);
                    break;

                case FunctionDefinitionNode fn:
                {
                    state.PushScope();
                    if (fn.ArgNames != null && fn.ArgTypes != null)
                    {
                        for (int i = 0; i < fn.ArgNames.Count && i < fn.ArgTypes.Count; i++)
                        {
                            var pname = fn.ArgNames[i];
                            var ptype = fn.ArgTypes[i];
                            if (!string.IsNullOrEmpty(pname) && ptype != null)
                                state.Declare(pname, ptype);
                        }
                    }
                    Walk(fn.BodyNode, state, diags);
                    state.PopScope();
                    break;
                }

                case ClassDefinitionNode cls:
                    foreach (var m in cls.Methods) Walk(m, state, diags);
                    break;

                case IfNode ifn:
                {
                    // `if/elif/else` is encoded as a flat list of cases plus
                    // an optional else. Every branch body gets a fresh scope
                    // so per-branch refinements don't leak into siblings.
                    foreach (var c in ifn.Cases)
                    {
                        Walk(c.Condition, state, diags);
                        state.PushScope();
                        Walk(c.Expr, state, diags);
                        state.PopScope();
                    }
                    if (ifn.ElseCase.HasValue)
                    {
                        state.PushScope();
                        Walk(ifn.ElseCase.Value.Expr, state, diags);
                        state.PopScope();
                    }
                    break;
                }

                case WhileNode wn:
                    Walk(wn.ConditionNode, state, diags);
                    state.PushScope();
                    Walk(wn.BodyNode, state, diags);
                    state.PopScope();
                    break;

                case ForNode fnd:
                {
                    state.PushScope();
                    Walk(fnd.StartValueNode, state, diags);
                    Walk(fnd.EndValueNode, state, diags);
                    Walk(fnd.BodyNode, state, diags);
                    state.PopScope();
                    break;
                }

                case MatchNode mn:
                {
                    Walk(mn.Scrutinee, state, diags);
                    CheckMatch(mn, state, diags);
                    foreach (var arm in mn.Arms)
                    {
                        state.PushScope();
                        BindPattern(arm.Pattern, mn.Scrutinee, state);
                        Walk(arm.Guard, state, diags);
                        Walk(arm.Body, state, diags);
                        state.PopScope();
                    }
                    break;
                }

                case IsTypeNode isNode:
                    Walk(isNode.Expression, state, diags);
                    CheckIsTest(isNode, state, diags);
                    break;

                case BinaryOperationNode bo:
                    Walk(bo.LeftNode, state, diags);
                    Walk(bo.RightNode, state, diags);
                    break;

                case UnaryOperationNode uo:
                    Walk(uo.Node, state, diags);
                    break;

                case TernaryNode tn:
                    Walk(tn.Condition, state, diags);
                    Walk(tn.TrueExpression, state, diags);
                    Walk(tn.FalseExpression, state, diags);
                    break;

                case CastNode cn:
                    Walk(cn.Expression, state, diags);
                    break;

                case ReturnNode rn:
                    Walk(rn.NodeToReturn, state, diags);
                    break;

                case ThrowNode th:
                    Walk(th.Expression, state, diags);
                    break;

                default:
                    // Unknown node kinds are walked structurally by their
                    // own visitor at runtime; for narrowing we have no
                    // additional invariant to extract.
                    break;
            }
        }

        // -------------------- `is`-test diagnostics --------------------

        private static void CheckIsTest(IsTypeNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            // We can only reason precisely when the LHS is a plain variable
            // access we have a declared type for. Any other LHS shape (a
            // function call, member access, …) is opaque to this pass — we
            // bail rather than risk a false positive.
            if (!(node.Expression is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;

            var declared = state.Lookup(name!);
            if (declared == null) return;
            var tested = node.TestedType;
            if (tested == null) return;

            // `is any` is always true; `is T` against a declared `any` is
            // honest narrowing — neither deserves a diagnostic.
            if (string.Equals(declared.Name, "any", System.StringComparison.Ordinal)) return;
            if (string.Equals(tested.Name, "any", System.StringComparison.Ordinal))
            {
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"type test '{name} is any' is always true",
                    node.PositionStart, node.PositionEnd));
                return;
            }

            // Impossible test: no value of `declared` could ever pass the
            // `is tested` check. Reported even for `is not` (where it'd be
            // trivially true — see below).
            if (!TypeSystem.TypesOverlap(null!, declared, tested))
            {
                if (node.Negated)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is not {tested}' is always true: '{name}' has declared type '{declared}' which is disjoint from '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
                else
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is {tested}' is always false: '{name}' has declared type '{declared}' which is disjoint from '{tested}'. Did you mean a different member of the union?",
                        node.PositionStart, node.PositionEnd));
                }
                return;
            }

            // Trivially-true test: the declared type is already a subtype
            // of the tested one — every value flowing here passes.
            if (TypeSystem.IsAssignableType(null!, tested, declared))
            {
                if (node.Negated)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is not {tested}' is always false: every value of declared type '{declared}' is already a '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
                else
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is {tested}' is always true: declared type '{declared}' is already a subtype of '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
            }
        }

        // -------------------- Type-pattern binding propagation --------------------

        private static void BindPattern(PatternNode? pattern, AstNode scrutinee, State state)
        {
            if (pattern == null) return;
            // Type patterns introduce a narrowed binding into the arm scope.
            if (pattern is TypePatternNode tpn && !string.IsNullOrEmpty(tpn.BinderName) && tpn.TestedType != null)
            {
                state.Declare(tpn.BinderName!, tpn.TestedType);
            }
            // VariablePattern over a typed scrutinee carries the scrutinee
            // type forward verbatim — useful for downstream walks but the
            // current diagnostic set doesn't consume it. Left in for clarity.
            else if (pattern is VariablePatternNode vp && scrutinee is VariableAccessNode sva)
            {
                var sname = sva.VarNameTok.Value?.ToString();
                if (!string.IsNullOrEmpty(sname))
                {
                    var t = state.Lookup(sname!);
                    if (t != null) state.Declare(vp.Name, t);
                }
            }
        }

        // -------------------- Union-match exhaustiveness --------------------

        private static void CheckMatch(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (!(node.Scrutinee is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var declared = state.Lookup(name!);
            if (declared == null || !declared.IsUnionType || declared.UnionMembers == null) return;

            // Wildcard / bare-binding short-circuits exhaustiveness: any
            // unmatched member would land in the fallback.
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (arm.Pattern is WildcardPatternNode) return;
                if (arm.Pattern is VariablePatternNode) return;
                if (arm.Pattern is TypePatternNode tpAny
                    && string.Equals(tpAny.TestedType?.Name, "any", System.StringComparison.Ordinal))
                    return;
            }

            // Collect every TestedType present in the arms. Each union
            // member must be covered by at least one collected type
            // (assignable into it).
            var covered = new List<TypeDescriptor>();
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (arm.Pattern is TypePatternNode tpn && tpn.TestedType != null)
                    covered.Add(tpn.TestedType);
            }

            if (covered.Count == 0) return;

            var missing = new List<TypeDescriptor>();
            foreach (var member in declared.UnionMembers)
            {
                bool isCovered = false;
                for (int i = 0; i < covered.Count; i++)
                {
                    if (TypeSystem.IsAssignableType(null!, covered[i], member))
                    {
                        isCovered = true;
                        break;
                    }
                }
                if (!isCovered) missing.Add(member);
            }

            if (missing.Count > 0)
            {
                var ms = string.Join(", ", missing.Select(t => "'" + t + "'"));
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"non-exhaustive match on union '{declared}': missing arm(s) for {ms}. Add 'case is {missing[0]} -> …' or a wildcard 'case _ -> …'.",
                    node.PositionStart, node.PositionEnd));
            }
        }
    }
}

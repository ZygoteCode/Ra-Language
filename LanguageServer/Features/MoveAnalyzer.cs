using System.Collections.Generic;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Conservative use-after-move detector for Ra's affine <c>let</c> bindings. This is
    /// the first slice of the move/borrow/lifetime story; it intentionally errs on the
    /// side of NO false positives:
    /// <list type="bullet">
    /// <item>Only <c>let</c> bindings whose type is a known user type (class/struct/…),
    /// since primitives are copy and collection semantics are uncertain.</item>
    /// <item>Only straight-line blocks — a block containing any control flow is skipped
    /// entirely (branches/loops can't be reasoned about linearly here).</item>
    /// <item>A binding that is borrowed (<c>&amp;x</c>), passed by <c>ref</c>, or
    /// reassigned is left alone.</item>
    /// </list>
    /// Under those guards, the second (and later) consuming read of a moved binding is
    /// reported. Full branch-aware move/borrow/lifetime checking is future work.
    /// </summary>
    public static class MoveAnalyzer
    {
        private sealed class Use
        {
            public readonly List<(int Start, int End)> Reads = new();
            public bool Unsafe; // borrowed / ref-passed / reassigned → skip
        }

        public static void Analyze(AstNode? ast, VarEnv env, TypeTable table, List<ToolingDiagnostic> sink)
        {
            Visit(ast, env, table, sink);
        }

        private static void Visit(AstNode? node, VarEnv env, TypeTable table, List<ToolingDiagnostic> sink)
        {
            switch (node)
            {
                case null: return;
                case ScopeNode b:
                    AnalyzeBlock(b, env, table, sink);
                    foreach (var n in b.Nodes) Visit(n, env, table, sink);
                    return;
                case FunctionDefinitionNode fn: Visit(fn.BodyNode, env, table, sink); return;
                case ClassDefinitionNode c: foreach (var m in c.Methods) Visit(m, env, table, sink); return;
                case StructDefinitionNode s: foreach (var m in s.Methods) Visit(m.BodyNode, env, table, sink); return;
                case RecordDefinitionNode r: foreach (var m in r.Methods) Visit(m.BodyNode, env, table, sink); return;
                case TraitDefinitionNode t: foreach (var m in t.Methods) Visit(m.BodyNode, env, table, sink); return;
                case ExtensionDefinitionNode e: foreach (var m in e.Methods) Visit(m, env, table, sink); return;
                case NamespaceDeclarationNode ns: Visit(ns.Body, env, table, sink); return;
                // Recurse control-flow bodies so nested blocks still get analyzed.
                case IfNode ifn:
                    foreach (var cs in ifn.Cases) Visit(cs.Item2, env, table, sink);
                    if (ifn.ElseCase != null) Visit(ifn.ElseCase.Value.Item1, env, table, sink);
                    return;
                case ForNode fr: Visit(fr.BodyNode, env, table, sink); return;
                case ForEachNode fe: Visit(fe.BodyNode, env, table, sink); return;
                case ForAwaitNode fa: Visit(fa.BodyNode, env, table, sink); return;
                case WhileNode wn: Visit(wn.BodyNode, env, table, sink); return;
                case DoWhileNode dwn: Visit(dwn.BodyNode, env, table, sink); return;
                case SuperForNode sfn: Visit(sfn.BodyNode, env, table, sink); return;
                case RetryNode rn: Visit(rn.BodyNode, env, table, sink); if (rn.ElseNode != null) Visit(rn.ElseNode, env, table, sink); return;
                case SwitchNode sw: foreach (var cs in sw.Cases) if (cs.Body != null) Visit(cs.Body, env, table, sink); return;
                case MatchNode m2: foreach (var arm in m2.Arms) Visit(arm.Body, env, table, sink); return;
                case TryNode tn:
                    Visit(tn.TryBody, env, table, sink);
                    if (tn.CatchBody != null) Visit(tn.CatchBody, env, table, sink);
                    if (tn.FinallyBody != null) Visit(tn.FinallyBody, env, table, sink);
                    return;
                case LabelNode lbl: Visit(lbl.Statements, env, table, sink); return;
            }
        }

        private static void AnalyzeBlock(ScopeNode block, VarEnv env, TypeTable table, List<ToolingDiagnostic> sink)
        {
            // Straight-line only: any control flow → bail (handled conservatively).
            foreach (var n in block.Nodes) if (IsControlFlow(n)) return;

            // Candidate `let` bindings of a known user type (non-copy).
            var info = new Dictionary<string, Use>(System.StringComparer.Ordinal);
            foreach (var n in block.Nodes)
            {
                if (n is not VariableDeclarationNode vd || !IsLet(vd.DeclarationType)) continue;
                foreach (var (tok, init, declared) in vd.Declarations)
                {
                    var name = tok.Value?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    var type = declared ?? TypeInference.InferType(init, env, table);
                    if (type != null && type.PrimitiveKind == Types.PrimitiveTypeKind.None && table.IsKnownType(type.Name))
                        info[name] = new Use();
                }
            }
            if (info.Count == 0) return;

            foreach (var stmt in block.Nodes) Scan(stmt, info, underBorrow: false);

            foreach (var kv in info)
            {
                var u = kv.Value;
                if (u.Unsafe || u.Reads.Count < 2) continue;
                for (int i = 1; i < u.Reads.Count; i++)
                    sink.Add(new ToolingDiagnostic(u.Reads[i].Start, u.Reads[i].End, DiagnosticSeverity.Warning,
                        $"Use of moved value '{kv.Key}'.", "RA0403"));
            }
        }

        // Recursively collect reads of tracked names; mark a name unsafe when borrowed /
        // ref-passed / reassigned. Unknown node shapes simply aren't descended (which can
        // only under-report — never a false positive).
        private static void Scan(AstNode? node, Dictionary<string, Use> info, bool underBorrow)
        {
            switch (node)
            {
                case null: return;
                case VariableAccessNode v:
                    if (info.TryGetValue(v.Name, out var u))
                    {
                        if (underBorrow) u.Unsafe = true;
                        else u.Reads.Add((v.VarNameTok.PositionStart.Idx, v.VarNameTok.PositionEnd.Idx));
                    }
                    return;
                case VariableAssignmentNode asn:
                    if (info.TryGetValue(asn.Name, out var au)) au.Unsafe = true; // reassigned → don't move-check
                    Scan(asn.ValueNode, info, underBorrow);
                    return;
                case VariableDeclarationNode vd:
                    foreach (var (_, init, _) in vd.Declarations) Scan(init, info, underBorrow);
                    return;
                case BorrowNode bn: Scan(bn.Target, info, underBorrow: true); return;
                case DereferenceNode dn: Scan(dn.Target, info, underBorrow); return;
                case DereferenceAssignmentNode dna: Scan(dna.RefTarget, info, underBorrow); Scan(dna.ValueNode, info, underBorrow); return;
                case FunctionCallNode fc:
                    Scan(fc.NodeToCall, info, underBorrow);
                    foreach (var arg in fc.ArgNodes) Scan(arg.Expr, info, underBorrow || arg.IsRef);
                    return;
                case MemberAccessNode ma: Scan(ma.TargetNode, info, underBorrow); return;
                case MemberAssignmentNode mas: Scan(mas.TargetNode, info, underBorrow); Scan(mas.ValueNode, info, underBorrow); return;
                case BinaryOperationNode bo: Scan(bo.LeftNode, info, underBorrow); Scan(bo.RightNode, info, underBorrow); return;
                case UnaryOperationNode uo: Scan(uo.Node, info, underBorrow); return;
                case ListAccessNode la: Scan(la.Target, info, underBorrow); Scan(la.Index, info, underBorrow); return;
                case ListAssignmentNode lasn: Scan(lasn.Target, info, underBorrow); Scan(lasn.Value, info, underBorrow); return;
                case CastNode cn: Scan(cn.Expression, info, underBorrow); return;
                case TernaryNode tn: Scan(tn.Condition, info, underBorrow); Scan(tn.TrueExpression, info, underBorrow); Scan(tn.FalseExpression, info, underBorrow); return;
                case NullCoalescingNode nc: Scan(nc.Left, info, underBorrow); Scan(nc.Right, info, underBorrow); return;
                case RangeNode rg: Scan(rg.Start, info, underBorrow); Scan(rg.End, info, underBorrow); if (rg.Step != null) Scan(rg.Step, info, underBorrow); return;
                case PipelineNode pn: Scan(pn.LeftNode, info, underBorrow); Scan(pn.RightNode, info, underBorrow); return;
                case ListNode ln: foreach (var el in ln.ElementNodes) Scan(el, info, underBorrow); return;
                case SetNode st: foreach (var el in st.ElementNodes) Scan(el, info, underBorrow); return;
                case TupleNode tp: foreach (var el in tp.ElementNodes) Scan(el, info, underBorrow); return;
                case MapNode mp: foreach (var (k, vv) in mp.Pairs) { Scan(k, info, underBorrow); Scan(vv, info, underBorrow); } return;
                case ReturnNode rn: if (rn.NodeToReturn != null) Scan(rn.NodeToReturn, info, underBorrow); return;
                case YieldNode yn: if (yn.Expression != null) Scan(yn.Expression, info, underBorrow); return;
                case AwaitNode an: Scan(an.Expression, info, underBorrow); return;
                case SpawnNode spn: Scan(spn.Expression, info, underBorrow); return;
                case EmitNode en: Scan(en.Expression, info, underBorrow); return;
                case StringNode sn: if (sn.Parts != null) foreach (var p in sn.Parts) Scan(p, info, underBorrow); return;
                case FormattedInterpolationNode fin: Scan(fin.Expression, info, underBorrow); return;
                case VariableDeleteNode del:
                    foreach (var tk in del.Tokens)
                    {
                        var nm = tk.Value?.ToString();
                        if (nm != null && info.TryGetValue(nm, out var du)) du.Unsafe = true; // `del x` consumes/ends it
                    }
                    return;
                // leaves / unhandled → no descent (safe: at worst under-reports)
            }
        }

        private static bool IsLet(VariableDeclarationType t) =>
            t is VariableDeclarationType.LET or VariableDeclarationType.LET_MUT or VariableDeclarationType.LET_CONST;

        private static bool IsControlFlow(AstNode n) => n is
            IfNode or ForNode or ForEachNode or ForAwaitNode or WhileNode or DoWhileNode or
            SuperForNode or RetryNode or SwitchNode or MatchNode or TryNode or LabelNode or ScopeNode;
    }
}

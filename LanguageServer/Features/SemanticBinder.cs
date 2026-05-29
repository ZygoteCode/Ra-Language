using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Lexical-scope binder for tooling. Walks the AST the same way the runtime
    /// <see cref="RaLanguage.Interpreter.Pipeline.Resolver"/> does, but records source
    /// positions instead of frame slots and resolves by pure lexical scope (so a
    /// reference inside a closure still binds to the outer declaration — exactly what
    /// go-to-definition wants). Type/function declarations are hoisted within their
    /// block so forward references resolve. Self-contained: the shared Resolver is
    /// untouched.
    /// </summary>
    public static class SemanticBinder
    {
        private sealed class Scope
        {
            public readonly Scope? Parent;
            public readonly Dictionary<string, BoundSymbol> Locals = new();
            public Scope(Scope? parent) { Parent = parent; }

            public BoundSymbol? Lookup(string name)
            {
                for (var s = this; s != null; s = s.Parent)
                    if (s.Locals.TryGetValue(name, out var sym)) return sym;
                return null;
            }
        }

        private sealed class Ctx
        {
            public readonly List<BoundSymbol> Symbols = new();
            public readonly List<BoundReference> References = new();
            public readonly List<UnresolvedRef> Unresolved = new();
            public readonly List<CallSite> Calls = new();
            public readonly List<MemberAccessSite> MemberAccesses = new();
        }

        public static SemanticModel Build(AstNode? root)
        {
            var ctx = new Ctx();
            if (root != null)
            {
                var scope = new Scope(null);
                if (root is ScopeNode block) WalkBlock(block, scope, ctx);
                else Walk(root, scope, ctx);
            }
            return new SemanticModel(ctx.Symbols, ctx.References, ctx.Unresolved, ctx.Calls, ctx.MemberAccesses);
        }

        // ---- declaration / reference helpers ----

        private static string NameOf(in Token tok) => tok.Value?.ToString() ?? string.Empty;

        private static BoundSymbol Declare(Scope scope, Ctx ctx, string name, BoundKind kind, int start, int end)
        {
            var sym = new BoundSymbol(name, kind, start, end);
            ctx.Symbols.Add(sym);
            scope.Locals[name] = sym; // latest declaration shadows earlier in the same scope
            return sym;
        }

        private static void DeclareTok(Scope scope, Ctx ctx, in Token tok, BoundKind kind)
        {
            string name = NameOf(tok);
            if (string.IsNullOrEmpty(name) || name == "_") return;
            Declare(scope, ctx, name, kind, tok.PositionStart.Idx, tok.PositionEnd.Idx);
        }

        private static void Reference(Scope scope, Ctx ctx, string name, int start, int end, bool isWrite)
        {
            if (string.IsNullOrEmpty(name)) return;
            var target = scope.Lookup(name);
            if (target == null)
            {
                // Unresolved read → candidate for an "undefined symbol" diagnostic once
                // builtins/imports/types are excluded. Writes are skipped (could be an
                // implicit declaration), and members/builtins resolve elsewhere.
                if (!isWrite) ctx.Unresolved.Add(new UnresolvedRef(name, start, end));
                return;
            }
            var r = new BoundReference(target, start, end, isWrite);
            target.References.Add(r);
            ctx.References.Add(r);
        }

        // ---- block + hoisting ----

        private static void WalkBlock(ScopeNode node, Scope parent, Ctx ctx)
        {
            var scope = new Scope(parent);
            Hoist(node, scope, ctx);
            foreach (var child in node.Nodes) Walk(child, scope, ctx);
        }

        private static void Hoist(ScopeNode node, Scope scope, Ctx ctx)
        {
            foreach (var child in node.Nodes)
            {
                switch (child)
                {
                    case FunctionDefinitionNode fn when fn.VarNameTok.HasValue && !fn.IsConstructor && !fn.IsFactory:
                        DeclareTok(scope, ctx, fn.VarNameTok.Value, BoundKind.Function);
                        break;
                    case ClassDefinitionNode c: DeclareTok(scope, ctx, c.NameTok, BoundKind.Class); break;
                    case StructDefinitionNode st: DeclareTok(scope, ctx, st.NameTok, BoundKind.Struct); break;
                    case RecordDefinitionNode rc: DeclareTok(scope, ctx, rc.NameTok, BoundKind.Record); break;
                    case EnumDefinitionNode en: DeclareTok(scope, ctx, en.NameTok, BoundKind.Enum); break;
                    case InterfaceDefinitionNode it: DeclareTok(scope, ctx, it.NameTok, BoundKind.Interface); break;
                    case TraitDefinitionNode tr: DeclareTok(scope, ctx, tr.NameTok, BoundKind.Trait); break;
                    case AnnotationDefinitionNode an: DeclareTok(scope, ctx, an.NameTok, BoundKind.Annotation); break;
                    case DelegateDefinitionNode dl: DeclareTok(scope, ctx, dl.NameTok, BoundKind.Delegate); break;
                }
            }
        }

        // ---- the walker ----

        private static void Walk(AstNode? node, Scope scope, Ctx ctx)
        {
            if (node == null) return;
            switch (node)
            {
                case ScopeNode block: WalkBlock(block, scope, ctx); return;

                // Variable flow.
                case VariableDeclarationNode decl:
                    foreach (var (tok, init, _) in decl.Declarations)
                    {
                        if (init != null) Walk(init, scope, ctx); // RHS resolves before the name is bound
                        DeclareTok(scope, ctx, tok, BoundKind.Variable);
                    }
                    return;
                case VariableAssignmentNode asn:
                    Walk(asn.ValueNode, scope, ctx);
                    Reference(scope, ctx, asn.Name, asn.PositionStart.Idx, asn.PositionStart.Idx + asn.Name.Length, isWrite: true);
                    return;
                case VariableAccessNode acc:
                    Reference(scope, ctx, acc.Name, acc.VarNameTok.PositionStart.Idx, acc.VarNameTok.PositionEnd.Idx, isWrite: false);
                    return;
                case VariableDeleteNode del:
                    foreach (var t in del.Tokens)
                        Reference(scope, ctx, NameOf(t), t.PositionStart.Idx, t.PositionEnd.Idx, isWrite: true);
                    return;

                // Frame / type sites (names already hoisted by the enclosing block).
                case FunctionDefinitionNode fn: WalkFunction(fn, scope, ctx); return;
                case ClassDefinitionNode cls: WalkClass(cls, scope, ctx); return;
                case StructDefinitionNode str: WalkStruct(str, scope, ctx); return;
                case RecordDefinitionNode rec: WalkRecord(rec, scope, ctx); return;
                case TraitDefinitionNode trait: WalkTrait(trait, scope, ctx); return;
                case ExtensionDefinitionNode ext: WalkExtension(ext, scope, ctx); return;
                case NamespaceDeclarationNode ns: Walk(ns.Body, scope, ctx); return;
                case AnnotationDefinitionNode adn:
                    foreach (var p in adn.Parameters)
                        if (p.DefaultValueNode != null) Walk(p.DefaultValueNode, scope, ctx);
                    return;

                // Loops / conditionals (introduce scopes).
                case ForNode fr:
                    Walk(fr.StartValueNode, scope, ctx); Walk(fr.EndValueNode, scope, ctx);
                    if (fr.StepValueNode != null) Walk(fr.StepValueNode, scope, ctx);
                    var forScope = new Scope(scope);
                    DeclareTok(forScope, ctx, fr.VarNameTok, BoundKind.LoopVariable);
                    Walk(fr.BodyNode, forScope, ctx);
                    return;
                case ForEachNode fe:
                    Walk(fe.CollectionNode, scope, ctx);
                    var feScope = new Scope(scope);
                    DeclareTok(feScope, ctx, fe.VarNameToken, BoundKind.LoopVariable);
                    Walk(fe.BodyNode, feScope, ctx);
                    return;
                case ForAwaitNode fa:
                    Walk(fa.StreamNode, scope, ctx);
                    var faScope = new Scope(scope);
                    DeclareTok(faScope, ctx, fa.VarNameToken, BoundKind.LoopVariable);
                    Walk(fa.BodyNode, faScope, ctx);
                    return;
                case WhileNode wn: Walk(wn.ConditionNode, scope, ctx); Walk(wn.BodyNode, scope, ctx); return;
                case DoWhileNode dwn: Walk(dwn.BodyNode, scope, ctx); Walk(dwn.ConditionNode, scope, ctx); return;
                case IfNode ifn:
                    foreach (var c in ifn.Cases) { Walk(c.Item1, scope, ctx); Walk(c.Item2, scope, ctx); }
                    if (ifn.ElseCase != null) Walk(ifn.ElseCase.Value.Item1, scope, ctx);
                    return;
                case MatchNode m:
                    Walk(m.Scrutinee, scope, ctx);
                    foreach (var arm in m.Arms)
                    {
                        var armScope = new Scope(scope);
                        BindPattern(arm.Pattern, armScope, ctx);
                        if (arm.Guard != null) Walk(arm.Guard, armScope, ctx);
                        Walk(arm.Body, armScope, ctx);
                    }
                    return;
                case SwitchNode sw:
                    Walk(sw.Expression, scope, ctx);
                    foreach (var c in sw.Cases)
                    {
                        foreach (var label in c.Labels) Walk(label, scope, ctx);
                        if (c.Body != null) Walk(c.Body, scope, ctx);
                    }
                    return;
                case TryNode tn:
                    Walk(tn.TryBody, scope, ctx);
                    if (tn.CatchBody != null)
                    {
                        var catchScope = new Scope(scope);
                        if (tn.CatchVarTok.HasValue) DeclareTok(catchScope, ctx, tn.CatchVarTok.Value, BoundKind.CatchVariable);
                        Walk(tn.CatchBody, catchScope, ctx);
                    }
                    if (tn.FinallyBody != null) Walk(tn.FinallyBody, scope, ctx);
                    return;
                case TryUnwrapNode tu: Walk(tu.Target, scope, ctx); return;
                case SuperForNode sfn:
                    var sfScope = new Scope(scope);
                    foreach (var init in sfn.InitializationNodes) Walk(init, sfScope, ctx);
                    foreach (var cond in sfn.ConditionNodes) Walk(cond, sfScope, ctx);
                    foreach (var step in sfn.StepNodes) Walk(step, sfScope, ctx);
                    Walk(sfn.BodyNode, sfScope, ctx);
                    return;
                case RetryNode rn:
                    Walk(rn.CountNode, scope, ctx);
                    if (rn.DelayNode != null) Walk(rn.DelayNode, scope, ctx);
                    Walk(rn.BodyNode, scope, ctx);
                    if (rn.ElseNode != null) Walk(rn.ElseNode, scope, ctx);
                    return;

                // Generic expression descents (no new scope).
                case BinaryOperationNode bo: Walk(bo.LeftNode, scope, ctx); Walk(bo.RightNode, scope, ctx); return;
                case UnaryOperationNode uo: Walk(uo.Node, scope, ctx); return;
                case FunctionCallNode fc:
                    if (fc.NodeToCall is VariableAccessNode callee)
                    {
                        var args = new AstNode[fc.ArgNodes.Count];
                        for (int i = 0; i < fc.ArgNodes.Count; i++) args[i] = fc.ArgNodes[i].Expr;
                        ctx.Calls.Add(new CallSite(callee.Name, fc.ArgNodes.Count,
                            callee.VarNameTok.PositionStart.Idx, callee.VarNameTok.PositionEnd.Idx, args));
                    }
                    Walk(fc.NodeToCall, scope, ctx);
                    foreach (var arg in fc.ArgNodes) Walk(arg.Expr, scope, ctx);
                    return;
                case ReturnNode ret: if (ret.NodeToReturn != null) Walk(ret.NodeToReturn, scope, ctx); return;
                case YieldNode yn: if (yn.Expression != null) Walk(yn.Expression, scope, ctx); return;
                case AwaitNode an: Walk(an.Expression, scope, ctx); return;
                case SpawnNode spn: Walk(spn.Expression, scope, ctx); return;
                case EmitNode en: Walk(en.Expression, scope, ctx); return;
                case PipelineNode pn: Walk(pn.LeftNode, scope, ctx); Walk(pn.RightNode, scope, ctx); return;
                case ListNode ln: foreach (var e in ln.ElementNodes) Walk(e, scope, ctx); return;
                case SetNode setN: foreach (var e in setN.ElementNodes) Walk(e, scope, ctx); return;
                case MapNode mapN: foreach (var (k, v) in mapN.Pairs) { Walk(k, scope, ctx); Walk(v, scope, ctx); } return;
                case TupleNode tup: foreach (var e in tup.ElementNodes) Walk(e, scope, ctx); return;
                case ListAccessNode la: Walk(la.Target, scope, ctx); Walk(la.Index, scope, ctx); return;
                case ListAssignmentNode lassign: Walk(lassign.Target, scope, ctx); Walk(lassign.Value, scope, ctx); return;
                case RangeNode rng:
                    Walk(rng.Start, scope, ctx); Walk(rng.End, scope, ctx);
                    if (rng.Step != null) Walk(rng.Step, scope, ctx);
                    return;
                case NullCoalescingNode nc: Walk(nc.Left, scope, ctx); Walk(nc.Right, scope, ctx); return;
                case TernaryNode trn:
                    Walk(trn.Condition, scope, ctx); Walk(trn.TrueExpression, scope, ctx); Walk(trn.FalseExpression, scope, ctx);
                    return;
                case CastNode cn: Walk(cn.Expression, scope, ctx); return;
                case TypeofNode tof: if (tof.Node != null) Walk(tof.Node, scope, ctx); return;
                case MemberAccessNode ma:
                    if (ma.TargetNode is VariableAccessNode maRecv)
                        ctx.MemberAccesses.Add(new MemberAccessSite(maRecv.Name, NameOf(ma.MemberTok),
                            ma.MemberTok.PositionStart.Idx, ma.MemberTok.PositionEnd.Idx));
                    Walk(ma.TargetNode, scope, ctx); // member name resolved by type, not scope
                    return;
                case MemberAssignmentNode mas: Walk(mas.TargetNode, scope, ctx); Walk(mas.ValueNode, scope, ctx); return;
                case BorrowNode bn: Walk(bn.Target, scope, ctx); return;
                case DereferenceNode dn: Walk(dn.Target, scope, ctx); return;
                case DereferenceAssignmentNode dna: Walk(dna.RefTarget, scope, ctx); Walk(dna.ValueNode, scope, ctx); return;
                case LabelNode lbl: if (lbl.Statements != null) Walk(lbl.Statements, scope, ctx); return;
                case StringNode strn: if (strn.Parts != null) foreach (var p in strn.Parts) Walk(p, scope, ctx); return;
                case FormattedInterpolationNode fin: Walk(fin.Expression, scope, ctx); return;
                case AnnotationApplicationNode aap:
                    foreach (var p in aap.PositionalArgs) Walk(p, scope, ctx);
                    foreach (var (_, v) in aap.NamedArgs) Walk(v, scope, ctx);
                    return;
            }
        }

        // ---- frame-opening walkers ----

        private static void WalkFunction(FunctionDefinitionNode fn, Scope scope, Ctx ctx)
        {
            var funcScope = new Scope(scope);
            for (int i = 0; i < fn.ArgNameToks.Count; i++)
                DeclareTok(funcScope, ctx, fn.ArgNameToks[i], BoundKind.Parameter);
            if (fn.HasVarArgs && fn.VarArgNameTok.HasValue)
                DeclareTok(funcScope, ctx, fn.VarArgNameTok.Value, BoundKind.Parameter);
            foreach (var d in fn.ParamDefaults) if (d != null) Walk(d, scope, ctx); // defaults in outer scope
            if (fn.BodyNode != null) Walk(fn.BodyNode, funcScope, ctx);
        }

        private static void WalkMethodBody(AstNode? body, IReadOnlyList<Token> argToks, Scope scope, Ctx ctx)
        {
            if (body == null) return;
            var methodScope = new Scope(scope);
            for (int i = 0; i < argToks.Count; i++) DeclareTok(methodScope, ctx, argToks[i], BoundKind.Parameter);
            Walk(body, methodScope, ctx);
        }

        private static void WalkClass(ClassDefinitionNode cls, Scope scope, Ctx ctx)
        {
            foreach (var f in cls.Fields) if (f.DefaultValueNode != null) Walk(f.DefaultValueNode, scope, ctx);
            foreach (var m in cls.Methods) WalkFunction(m, scope, ctx);
            foreach (var op in cls.Operators) WalkMethodBody(op.BodyNode, new[] { op.ArgNameTok }, scope, ctx);
            WalkProperties(cls.Properties, scope, ctx);
        }

        private static void WalkStruct(StructDefinitionNode str, Scope scope, Ctx ctx)
        {
            foreach (var f in str.Fields) if (f.DefaultValueNode != null) Walk(f.DefaultValueNode, scope, ctx);
            foreach (var m in str.Methods) WalkMethodBody(m.BodyNode, m.ArgNameToks, scope, ctx);
            foreach (var op in str.Operators) WalkMethodBody(op.BodyNode, new[] { op.ArgNameTok }, scope, ctx);
            WalkProperties(str.Properties, scope, ctx);
        }

        private static void WalkRecord(RecordDefinitionNode rec, Scope scope, Ctx ctx)
        {
            foreach (var pf in rec.PrimaryFields) if (pf.DefaultValueNode != null) Walk(pf.DefaultValueNode, scope, ctx);
            foreach (var m in rec.Methods) WalkMethodBody(m.BodyNode, m.ArgNameToks, scope, ctx);
            foreach (var op in rec.Operators) WalkMethodBody(op.BodyNode, new[] { op.ArgNameTok }, scope, ctx);
            WalkProperties(rec.Properties, scope, ctx);
        }

        private static void WalkTrait(TraitDefinitionNode trait, Scope scope, Ctx ctx)
        {
            foreach (var m in trait.Methods)
                if (m.BodyNode != null) WalkMethodBody(m.BodyNode, m.ArgNameToks, scope, ctx);
            WalkProperties(trait.Properties, scope, ctx);
        }

        private static void WalkExtension(ExtensionDefinitionNode ext, Scope scope, Ctx ctx)
        {
            foreach (var m in ext.Methods) WalkFunction(m, scope, ctx);
            foreach (var op in ext.Operators) WalkMethodBody(op.BodyNode, new[] { op.ArgNameTok }, scope, ctx);
            WalkProperties(ext.Properties, scope, ctx);
        }

        private static void WalkProperties(IReadOnlyList<PropertyDefinitionNode> properties, Scope scope, Ctx ctx)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                if (properties[i].DefaultValueNode != null) Walk(properties[i].DefaultValueNode, scope, ctx);
                foreach (var accessor in properties[i].Accessors)
                    if (accessor.BodyNode != null) Walk(accessor.BodyNode, new Scope(scope), ctx);
            }
        }

        // ---- pattern bindings ----

        private static void BindPattern(PatternNode? p, Scope scope, Ctx ctx)
        {
            switch (p)
            {
                case null: return;
                case VariablePatternNode v:
                    if (!string.IsNullOrEmpty(v.Name) && v.Name != "_")
                        Declare(scope, ctx, v.Name, BoundKind.PatternBinding, v.PositionStart.Idx, v.PositionEnd.Idx);
                    return;
                case VariantPatternNode vp:
                    if (vp.SubPatterns != null) foreach (var sub in vp.SubPatterns) BindPattern(sub, scope, ctx);
                    return;
                case TuplePatternNode tp:
                    foreach (var sub in tp.Elements) BindPattern(sub, scope, ctx);
                    return;
                case ListPatternNode lp:
                    foreach (var sub in lp.Elements) BindPattern(sub, scope, ctx);
                    if (lp.Rest != null) BindPattern(lp.Rest, scope, ctx);
                    return;
                case StructPatternNode sp:
                    foreach (var (_, sub) in sp.Fields) if (sub != null) BindPattern(sub, scope, ctx);
                    return;
                case LiteralPatternNode lit:
                    if (lit.Expression != null) Walk(lit.Expression, scope, ctx);
                    return;
            }
        }
    }
}

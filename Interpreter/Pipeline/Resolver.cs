using System;
using System.Collections.Generic;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Pipeline
{
    // Single-pass static resolver. Runs once on the post-derive AST, between
    // DeriveTransformer and the rest of the static analysis chain. Walks every
    // node, allocates a slot index for each declared name inside its owning
    // function frame, and annotates every identifier reference with the
    // BindingId / BindingKind that names the slot it ultimately reads or writes.
    //
    // What this pass enables downstream (per the audit):
    //   * Closure-capture analysis at compile time. ResolvedCaptures on every
    //     FunctionDefinitionNode lists the outer bindings the body actually
    //     references; the closure builder can pre-materialise an upvalue table
    //     without re-walking the lexical chain on every call.
    //   * O(1) variable access. BindingKind.Local + BindingId.Offset lets the
    //     runtime jump straight to a slot array entry instead of hashing the
    //     name through the SymbolTable parent chain.
    //   * LSP go-to-definition. Each BindingId is a stable handle that maps
    //     back to a (frame, declaration-position) tuple held in the resolver's
    //     frame registry.
    //
    // What this pass does NOT do:
    //   * Type checking, borrow checking, contract evaluation. Those run in
    //     their own passes (StaticAnalyzer / BorrowChecker / ContractEvaluator).
    //   * Imported-module resolution. Cross-module references stay Unresolved
    //     here so the existing module loader can bind them at module-load
    //     time. The fallback name-lookup path in visitors still works.
    //   * Reporting diagnostics. Anything the resolver can't bind is left as
    //     Unresolved; the runtime's existing "is not defined" check covers
    //     the genuinely missing names.
    public static class Resolver
    {
        // Cap the frame counter at 16 bits — the BindingId layout uses the high
        // half of a 32-bit int for the frame id. A script with > 65k function
        // definitions is implausible, but if it ever happens we fall back to
        // leaving the surplus identifiers Unresolved rather than corrupting the
        // bit layout.
        private const int MaxFrames = 0xFFFF;

        public static void Resolve(AstNode? root)
        {
            if (root == null) return;

            var s = new State();
            // Frame 0 is always the top-level script frame. Top-level names are
            // tagged Global so call sites for `print(...)` or sibling top-level
            // functions still flow through the existing GlobalSymbolTable path
            // at runtime — they just carry a static breadcrumb now.
            var topFrame = s.PushFrame("<script>", isFunctionRoot: true, isScript: true);
            if (topFrame == null) return;
            s.CurrentScope = new ScopeRecord(topFrame, parent: null);

            // Pre-scan the script frame for `del` names so any name del'd at the
            // top level is bound non-slot before it is declared / read.
            CollectDeletedNames(root, topFrame.DeletedNames);

            Walk(root, s);

            s.PopFrame();
        }

        // ---------------------------------------------------------------------
        // Walker
        // ---------------------------------------------------------------------

        private static void Walk(AstNode? node, State s)
        {
            if (node == null) return;

            switch (node)
            {
                // Scopes / blocks.
                case ScopeNode scope: WalkScope(scope, s); return;

                // Variable nodes (the things this pass actually annotates).
                case VariableDeclarationNode decl: WalkVarDecl(decl, s); return;
                case VariableAssignmentNode assign: WalkVarAssign(assign, s); return;
                case VariableAccessNode acc: WalkVarAccess(acc, s); return;
                case VariableDeleteNode del: WalkVarDelete(del, s); return;
                case SelfNode self: WalkSelf(self, s); return;

                // Frame-opening sites.
                case FunctionDefinitionNode fn: WalkFunction(fn, s); return;
                case ClassDefinitionNode cls: WalkClass(cls, s); return;
                case StructDefinitionNode str: WalkStruct(str, s); return;
                case RecordDefinitionNode rec: WalkRecord(rec, s); return;
                case TraitDefinitionNode trait: WalkTrait(trait, s); return;
                case ExtensionDefinitionNode ext: WalkExtension(ext, s); return;
                case Parser.Nodes.Namespaces.NamespaceDeclarationNode nd:
                    Walk(nd.Body, s); return;

                // Loops / conditionals.
                case ForNode fr: WalkFor(fr, s); return;
                case ForEachNode fe: WalkForEach(fe, s); return;
                case ForAwaitNode fa: WalkForAwait(fa, s); return;
                case WhileNode wn:
                    Walk(wn.ConditionNode, s); Walk(wn.BodyNode, s); return;
                case DoWhileNode dwn:
                    Walk(dwn.BodyNode, s); Walk(dwn.ConditionNode, s); return;
                case IfNode ifn: WalkIf(ifn, s); return;
                case MatchNode m: WalkMatch(m, s); return;
                case SwitchNode sw: WalkSwitch(sw, s); return;
                case TryNode tn: WalkTry(tn, s); return;
                case TryUnwrapNode tu: Walk(tu.Target, s); return;
                case SuperForNode sfn: WalkSuperFor(sfn, s); return;
                case RetryNode rn: WalkRetry(rn, s); return;

                // Generic walkers — descend into children that may contain
                // identifier references but don't introduce scopes themselves.
                case BinaryOperationNode bo:
                    Walk(bo.LeftNode, s); Walk(bo.RightNode, s); return;
                case UnaryOperationNode uo:
                    Walk(uo.Node, s); return;
                case FunctionCallNode fc:
                    Walk(fc.NodeToCall, s);
                    foreach (var arg in fc.ArgNodes) Walk(arg.Expr, s);
                    return;
                case ReturnNode ret:
                    if (ret.NodeToReturn != null) Walk(ret.NodeToReturn, s);
                    return;
                case YieldNode yn:
                    if (yn.Expression != null) Walk(yn.Expression, s);
                    return;
                case AwaitNode an: Walk(an.Expression, s); return;
                case SpawnNode spn: Walk(spn.Expression, s); return;
                case EmitNode en: Walk(en.Expression, s); return;
                case PipelineNode pn:
                    Walk(pn.LeftNode, s); Walk(pn.RightNode, s); return;
                case ListNode ln:
                    foreach (var e in ln.ElementNodes) Walk(e, s);
                    return;
                case SetNode setN:
                    foreach (var e in setN.ElementNodes) Walk(e, s);
                    return;
                case MapNode mapN:
                    foreach (var (k, v) in mapN.Pairs) { Walk(k, s); Walk(v, s); }
                    return;
                case TupleNode tup:
                    foreach (var e in tup.ElementNodes) Walk(e, s);
                    return;
                case ListAccessNode la:
                    Walk(la.Target, s); Walk(la.Index, s); return;
                case ListAssignmentNode lassign:
                    Walk(lassign.Target, s); Walk(lassign.Value, s); return;
                case RangeNode rng:
                    Walk(rng.Start, s); Walk(rng.End, s);
                    if (rng.Step != null) Walk(rng.Step, s);
                    return;
                case NullCoalescingNode nc:
                    Walk(nc.Left, s); Walk(nc.Right, s); return;
                case TernaryNode trn:
                    Walk(trn.Condition, s);
                    Walk(trn.TrueExpression, s);
                    Walk(trn.FalseExpression, s);
                    return;
                case CastNode cn:
                    Walk(cn.Expression, s); return;
                case TypeofNode tof:
                    if (tof.Node != null) Walk(tof.Node, s);
                    return;
                case IsTypeNode itn:
                    // `expr is T` — resolve the tested expression so a binding it
                    // reads (e.g. a match pattern binder `case Ok(n) -> n is T`)
                    // gets a slot-eligible BindingId, not left Unresolved. Without
                    // this the IR match compiler can't confirm the binder's slot
                    // and falls back to OP_NATIVE_DEFINE. TestedType is a type, not
                    // a value expression — nothing to walk there.
                    Walk(itn.Expression, s); return;
                case MemberAccessNode ma:
                    Walk(ma.TargetNode, s); return;
                case MemberAssignmentNode mas:
                    Walk(mas.TargetNode, s); Walk(mas.ValueNode, s); return;
                case BorrowNode bn:
                    Walk(bn.Target, s); return;
                case DereferenceNode dn:
                    Walk(dn.Target, s); return;
                case DereferenceAssignmentNode dna:
                    Walk(dna.RefTarget, s); Walk(dna.ValueNode, s); return;
                case LabelNode lbl:
                    if (lbl.Statements != null) Walk(lbl.Statements, s);
                    return;
                case StringNode strn:
                    if (strn.Parts != null)
                        foreach (var p in strn.Parts) Walk(p, s);
                    return;
                case FormattedInterpolationNode fin:
                    Walk(fin.Expression, s); return;
                case AnnotationApplicationNode aap:
                    foreach (var p in aap.PositionalArgs) Walk(p, s);
                    foreach (var (_, v) in aap.NamedArgs) Walk(v, s);
                    return;
                case AnnotationDefinitionNode adn:
                    foreach (var p in adn.Parameters)
                        if (p.DefaultValueNode != null) Walk(p.DefaultValueNode, s);
                    return;

                // Leaves: nothing to recurse into. Listed for clarity and to keep
                // the switch exhaustive against the audit's "every identifier"
                // promise.
                case NumberNode:
                case BooleanNode:
                case NullNode:
                case NameofNode:
                case RegexLiteralNode:
                case BreakNode:
                case ContinueNode:
                case PassNode:
                case GotoNode:
                case EnumAccessNode:
                case SuperNode:
                case EnumDefinitionNode:
                case InterfaceDefinitionNode:
                    return;
            }
        }

        // ---------------------------------------------------------------------
        // Deleted-name pre-scan (frame-local)
        // ---------------------------------------------------------------------

        // Collect every name that appears in a `del name` statement reachable
        // within the CURRENT frame's body, WITHOUT descending into nested
        // function/method/accessor frames (a `del` inside an inner function only
        // de-slots that inner frame's binding of the name, collected when that
        // frame is itself opened). Conditionals / loops / try / switch / match
        // bodies all execute in the same frame, so we descend into them — that is
        // what makes the de-slot correct across a loop back-edge (a read textually
        // before a `del` re-executes after it). Run once per frame at frame-open.
        private static void CollectDeletedNames(AstNode? node, HashSet<string> sink)
        {
            if (node == null) return;
            switch (node)
            {
                case VariableDeleteNode del:
                    foreach (var t in del.Tokens)
                    {
                        var n = t.Value?.ToString();
                        if (!string.IsNullOrEmpty(n)) sink.Add(n!);
                    }
                    return;

                // Frame boundaries — do NOT descend (their `del`s belong to their
                // own frames, scanned when those frames open).
                case FunctionDefinitionNode:
                case ClassDefinitionNode:
                case StructDefinitionNode:
                case RecordDefinitionNode:
                case TraitDefinitionNode:
                case ExtensionDefinitionNode:
                case EnumDefinitionNode:
                case InterfaceDefinitionNode:
                case AnnotationDefinitionNode:
                    return;

                // Same-frame structural nodes — descend into their bodies.
                case ScopeNode scope:
                    foreach (var c in scope.Nodes) CollectDeletedNames(c, sink);
                    return;
                case IfNode ifn:
                    foreach (var c in ifn.Cases) CollectDeletedNames(c.Item2, sink);
                    if (ifn.ElseCase != null) CollectDeletedNames(ifn.ElseCase.Value.Item1, sink);
                    return;
                case ForNode fr: CollectDeletedNames(fr.BodyNode, sink); return;
                case ForEachNode fe: CollectDeletedNames(fe.BodyNode, sink); return;
                case ForAwaitNode fa: CollectDeletedNames(fa.BodyNode, sink); return;
                case WhileNode wn: CollectDeletedNames(wn.BodyNode, sink); return;
                case DoWhileNode dwn: CollectDeletedNames(dwn.BodyNode, sink); return;
                case SuperForNode sfn: CollectDeletedNames(sfn.BodyNode, sink); return;
                case RetryNode rn:
                    CollectDeletedNames(rn.BodyNode, sink);
                    if (rn.ElseNode != null) CollectDeletedNames(rn.ElseNode, sink);
                    return;
                case TryNode tn:
                    CollectDeletedNames(tn.TryBody, sink);
                    if (tn.CatchBody != null) CollectDeletedNames(tn.CatchBody, sink);
                    if (tn.FinallyBody != null) CollectDeletedNames(tn.FinallyBody, sink);
                    return;
                case SwitchNode sw:
                    foreach (var c in sw.Cases) CollectDeletedNames(c.Body, sink);
                    return;
                case MatchNode m:
                    foreach (var arm in m.Arms) CollectDeletedNames(arm.Body, sink);
                    return;
                case LabelNode lbl: CollectDeletedNames(lbl.Statements, sink); return;
                case Parser.Nodes.Namespaces.NamespaceDeclarationNode nd:
                    CollectDeletedNames(nd.Body, sink);
                    return;
            }
        }

        // ---------------------------------------------------------------------
        // Scopes / blocks
        // ---------------------------------------------------------------------

        private static void WalkScope(ScopeNode scope, State s)
        {
            var saved = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
            foreach (var child in scope.Nodes) Walk(child, s);
            s.CurrentScope = saved;
        }

        private static void WalkVarDecl(VariableDeclarationNode decl, State s)
        {
            var bindings = new BindingId[decl.Declarations.Count];
            for (int i = 0; i < decl.Declarations.Count; i++)
            {
                var (tok, init, _) = decl.Declarations[i];
                // Init is evaluated in the enclosing scope BEFORE the name is
                // bound: `let x = x + 1` resolves the RHS `x` against the outer
                // binding. Matches Ra's runtime.
                if (init != null) Walk(init, s);

                var name = tok.Value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    bindings[i] = BindingId.Unresolved;
                    continue;
                }

                bindings[i] = s.AllocateLocal(name);
            }
            decl.Bindings = bindings;
        }

        private static void WalkVarAssign(VariableAssignmentNode assign, State s)
        {
            Walk(assign.ValueNode, s);
            var (id, kind) = s.LookupReference(assign.Name);
            assign.Binding = id;
            assign.BindingKind = kind;
        }

        private static void WalkVarAccess(VariableAccessNode acc, State s)
        {
            var (id, kind) = s.LookupReference(acc.Name);
            acc.Binding = id;
            acc.BindingKind = kind;
        }

        private static void WalkVarDelete(VariableDeleteNode del, State s)
        {
            var ids = new BindingId[del.Tokens.Count];
            var kinds = new BindingKind[del.Tokens.Count];
            for (int i = 0; i < del.Tokens.Count; i++)
            {
                var name = del.Tokens[i].Value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    ids[i] = BindingId.Unresolved;
                    kinds[i] = BindingKind.Unresolved;
                    continue;
                }
                var (id, kind) = s.LookupReference(name);
                ids[i] = id;
                kinds[i] = kind;
            }
            del.Bindings = ids;
            del.BindingKinds = kinds;
        }

        private static void WalkSelf(SelfNode self, State s)
        {
            // `self` always names the synthetic slot 0 of the enclosing method
            // frame. If we are not inside one, leave Unresolved so the runtime
            // can emit its existing "self only valid inside a method" error.
            var frame = s.CurrentScope?.Frame;
            while (frame != null && !frame.IsMethodFrame) frame = frame.Parent;
            if (frame == null) { self.Binding = BindingId.Unresolved; return; }
            self.Binding = new BindingId(frame.FrameId, 0);
        }

        // ---------------------------------------------------------------------
        // Functions / methods (frame-opening sites)
        // ---------------------------------------------------------------------

        private static void WalkFunction(FunctionDefinitionNode fn, State s)
        {
            // Function declaration also binds its own name in the enclosing
            // scope so siblings can call it locally.
            var name = fn.VarNameTok?.Value?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                s.AllocateLocalIfAbsent(name!);
            }

            OpenFrameForFunction(fn, s, isMethodFrame: false);
        }

        private static void OpenFrameForFunction(FunctionDefinitionNode fn, State s, bool isMethodFrame)
        {
            var frameName = fn.VarNameTok?.Value?.ToString() ?? "<anonymous>";
            var frame = s.PushFrame(frameName, isFunctionRoot: true, isScript: false, isMethodFrame: isMethodFrame);
            if (frame == null)
            {
                if (fn.BodyNode != null) Walk(fn.BodyNode, s);
                return;
            }
            frame.OwnerNode = fn;
            fn.FrameId = frame.FrameId;
            fn.ReservesSelfSlot = isMethodFrame;
            var savedScope = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(frame, parent: null);

            // Pre-scan THIS function body for `del` names (not descending into
            // nested frames) so del'd names are bound non-slot in this frame.
            if (fn.BodyNode != null) CollectDeletedNames(fn.BodyNode, frame.DeletedNames);

            // Reserve slot 0 for `self` in method frames.
            if (isMethodFrame) frame.AllocateSlot();

            // Explicit-capture pre-registration: resolve each capture's source
            // in the enclosing scope and reserve a local slot in this frame
            // so the body sees a bound name.
            if (fn.CaptureList != null)
            {
                fn.ResolvedCaptures ??= new List<ResolvedCapture>();
                foreach (var spec in fn.CaptureList)
                {
                    // Look up the source in the outer scope using the saved
                    // scope (we already switched into the inner frame above).
                    var sourceId = LookupInScope(savedScope, spec.Name);
                    fn.ResolvedCaptures.Add(new ResolvedCapture(spec.Name, sourceId, isExplicit: true));
                    s.AllocateLocalIfAbsent(spec.Name);
                }
            }

            // Parameter slots.
            var paramBindings = new BindingId[fn.ArgNameToks.Count];
            for (int i = 0; i < fn.ArgNameToks.Count; i++)
            {
                var n = fn.ArgNameToks[i].Value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(n)) { paramBindings[i] = BindingId.Unresolved; continue; }
                paramBindings[i] = s.AllocateLocal(n, kindOverride: BindingKind.Parameter);
            }
            fn.ParamBindings = paramBindings;

            if (fn.HasVarArgs && fn.VarArgNameTok.HasValue)
            {
                var n = fn.VarArgNameTok.Value.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(n)) s.AllocateLocal(n, kindOverride: BindingKind.Parameter);
            }

            // Parameter defaults are evaluated in the OUTER scope. Pop into it
            // for the duration of the walk, then return to the inner scope for
            // the body.
            if (fn.ParamDefaults.Count > 0)
            {
                var innerScope = s.CurrentScope;
                s.CurrentScope = savedScope;
                for (int i = 0; i < fn.ParamDefaults.Count; i++)
                {
                    if (fn.ParamDefaults[i] != null) Walk(fn.ParamDefaults[i]!, s);
                }
                s.CurrentScope = innerScope;
            }

            if (fn.BodyNode != null) Walk(fn.BodyNode, s);

            s.CurrentScope = savedScope;
            s.PopFrame();
        }

        private static BindingId LookupInScope(ScopeRecord? scope, string name)
        {
            var cur = scope;
            while (cur != null)
            {
                if (cur.Locals.TryGetValue(name, out var id)) return id;
                cur = cur.Parent;
            }
            return BindingId.Unresolved;
        }

        private static void WalkClass(ClassDefinitionNode cls, State s)
        {
            var className = cls.NameTok.Value?.ToString();
            if (!string.IsNullOrEmpty(className)) s.AllocateLocalIfAbsent(className!);

            // L10: a field default is framed as a self-bound, zero named-arg method
            // body (self is implicit slot 0) so a NON-CONST default can be IR-compiled
            // into an at-construction thunk. WalkMethodLikeBody resolves the body, so
            // no separate bare Walk is needed. (Const defaults are framed too; harmless
            // — IrCompiler folds them and ignores the frame.)
            foreach (var field in cls.Fields)
                if (field.DefaultValueNode != null)
                {
                    var dpb = WalkMethodLikeBody(field.DefaultValueNode, System.Array.Empty<RaLanguage.Lexer.Tokens.Token>(), s, out int dFrame);
                    field.DefaultFrameId = dFrame; field.DefaultParamBindings = dpb;
                }

            foreach (var m in cls.Methods) OpenFrameForFunction(m, s, isMethodFrame: true);

            foreach (var op in cls.Operators)
            {
                var paramBindings = WalkMethodLikeBody(op.BodyNode, new[] { op.ArgNameTok }, s, out int frameId);
                op.FrameId = frameId;
                op.ParamBindings = paramBindings;
            }

            WalkProperties(cls.Properties, s);
        }

        // M18: walk a trait body so each method's frame + param slots are
        // pinned by the Resolver, enabling IR compilation downstream.
        private static void WalkTrait(TraitDefinitionNode trait, State s)
        {
            var name = trait.NameTok.Value?.ToString();
            if (!string.IsNullOrEmpty(name)) s.AllocateLocalIfAbsent(name!);
            foreach (var m in trait.Methods)
            {
                if (m.BodyNode == null) continue; // abstract methods
                var paramBindings = WalkMethodLikeBody(m.BodyNode, m.ArgNameToks, s, out int frameId);
                m.FrameId = frameId;
                m.ParamBindings = paramBindings;
            }
        }

        // Extension methods are FunctionDefinitionNodes; reuse WalkFunction so
        // their FrameId / ParamBindings / capture analysis happens normally.
        // Operators get a method-frame walk (single arg + self); property
        // bodies and event payloads route through the regular AST walker
        // (PropertyAccessOps spins up a fresh Interpreter for each
        // dispatch, so no FrameId is required for accessor bodies).
        private static void WalkExtension(ExtensionDefinitionNode ext, State s)
        {
            foreach (var m in ext.Methods)
                WalkFunction(m, s);

            foreach (var op in ext.Operators)
            {
                var paramBindings = WalkMethodLikeBody(op.BodyNode, new[] { op.ArgNameTok }, s, out int frameId);
                op.FrameId = frameId;
                op.ParamBindings = paramBindings;
            }

            // L10: frame extension property accessor bodies (computed ext properties
            // run via the VM, same as type-member computed properties).
            WalkProperties(ext.Properties, s);
        }

        private static void WalkStruct(StructDefinitionNode str, State s)
        {
            var name = str.NameTok.Value?.ToString();
            if (!string.IsNullOrEmpty(name)) s.AllocateLocalIfAbsent(name!);

            // L10: frame each field default (self implicit slot 0) so a NON-CONST
            // default can be IR-compiled into an at-construction thunk. See WalkClass.
            foreach (var field in str.Fields)
                if (field.DefaultValueNode != null)
                {
                    var dpb = WalkMethodLikeBody(field.DefaultValueNode, System.Array.Empty<RaLanguage.Lexer.Tokens.Token>(), s, out int dFrame);
                    field.DefaultFrameId = dFrame; field.DefaultParamBindings = dpb;
                }

            foreach (var m in str.Methods)
            {
                var paramBindings = WalkMethodLikeBody(m.BodyNode, m.ArgNameToks, s, out int frameId);
                m.FrameId = frameId;
                m.ParamBindings = paramBindings;
            }

            foreach (var op in str.Operators)
            {
                var paramBindings = WalkMethodLikeBody(op.BodyNode, new[] { op.ArgNameTok }, s, out int frameId);
                op.FrameId = frameId;
                op.ParamBindings = paramBindings;
            }

            WalkProperties(str.Properties, s);
        }

        private static void WalkRecord(RecordDefinitionNode rec, State s)
        {
            var name = rec.NameTok.Value?.ToString();
            if (!string.IsNullOrEmpty(name)) s.AllocateLocalIfAbsent(name!);

            foreach (var pf in rec.PrimaryFields)
                if (pf.DefaultValueNode != null) Walk(pf.DefaultValueNode, s);

            foreach (var m in rec.Methods)
            {
                var paramBindings = WalkMethodLikeBody(m.BodyNode, m.ArgNameToks, s, out int frameId);
                m.FrameId = frameId;
                m.ParamBindings = paramBindings;
            }

            foreach (var op in rec.Operators)
            {
                var paramBindings = WalkMethodLikeBody(op.BodyNode, new[] { op.ArgNameTok }, s, out int frameId);
                op.FrameId = frameId;
                op.ParamBindings = paramBindings;
            }

            WalkProperties(rec.Properties, s);
        }

        // Helper for struct/operator method bodies which carry args+body but
        // aren't FunctionDefinitionNodes. They get a fresh method frame with
        // self at slot 0 followed by their args; only the body is walked.
        // M18: returns the param BindingId[] + frame id so the IR compiler can
        // lower the body just like a function. Callers attach to the AST node.
        private static BindingId[]? WalkMethodLikeBody(AstNode? body, IReadOnlyList<RaLanguage.Lexer.Tokens.Token> argToks, State s, out int frameIdOut)
        {
            frameIdOut = -1;
            if (body == null) return null;
            var frame = s.PushFrame("<method>", isFunctionRoot: true, isScript: false, isMethodFrame: true);
            if (frame == null) { Walk(body, s); return null; }
            var savedScope = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(frame, parent: null);
            frameIdOut = frame.FrameId;

            // Pre-scan this method-like body for `del` names so they bind non-slot.
            CollectDeletedNames(body, frame.DeletedNames);

            frame.AllocateSlot(); // self
            var paramBindings = new BindingId[argToks.Count];
            for (int i = 0; i < argToks.Count; i++)
            {
                var n = argToks[i].Value?.ToString() ?? string.Empty;
                paramBindings[i] = string.IsNullOrEmpty(n)
                    ? BindingId.Unresolved
                    : s.AllocateLocal(n!, kindOverride: BindingKind.Parameter);
            }
            Walk(body, s);

            s.CurrentScope = savedScope;
            s.PopFrame();
            return paramBindings;
        }

        // L10: frame each PROPERTY accessor body so it can be IR-compiled. self is
        // implicit (slot 0); the named bindings mirror what PropertyAccessOps sets
        // in the accessor context: getter `field`; setter/init `value`,`field`;
        // observer `old`,`value`,`field`. Auto accessors (no body) are skipped.
        private static void WalkProperties(System.Collections.Generic.IEnumerable<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode> props, State s)
        {
            foreach (var p in props)
            {
                // L10: a LAZY default initializer is framed as a self-bound, zero
                // named-arg method body (self is implicit slot 0) so it can be
                // IR-compiled into a first-touch thunk (PropertyAccessOps).
                if (p.IsLazy && p.DefaultValueNode != null)
                {
                    var dpb = WalkMethodLikeBody(p.DefaultValueNode, System.Array.Empty<RaLanguage.Lexer.Tokens.Token>(), s, out int dFrame);
                    p.DefaultFrameId = dFrame;
                    p.DefaultParamBindings = dpb;
                }

                if (p.Accessors == null) continue;
                foreach (var acc in p.Accessors)
                {
                    if (acc.BodyNode == null) continue; // auto accessor — synthesised, no body
                    var argToks = AccessorArgToks(acc);
                    var paramBindings = WalkMethodLikeBody(acc.BodyNode, argToks, s, out int frameId);
                    acc.FrameId = frameId;
                    acc.ParamBindings = paramBindings;
                }
            }
        }

        private static RaLanguage.Lexer.Tokens.Token[] AccessorArgToks(RaLanguage.Parser.Nodes.Properties.PropertyAccessorNode acc)
        {
            var ps = acc.KindTok.PositionStart;
            var pe = acc.KindTok.PositionEnd;
            RaLanguage.Lexer.Tokens.Token T(string n) =>
                new RaLanguage.Lexer.Tokens.Token(RaLanguage.Lexer.Tokens.TokenType.IDENTIFIER, n, ps, pe);
            return acc.Kind switch
            {
                RaLanguage.Parser.Nodes.Properties.PropertyAccessorKind.Get => new[] { T("field") },
                RaLanguage.Parser.Nodes.Properties.PropertyAccessorKind.Set => new[] { T("value"), T("field") },
                RaLanguage.Parser.Nodes.Properties.PropertyAccessorKind.Init => new[] { T("value"), T("field") },
                RaLanguage.Parser.Nodes.Properties.PropertyAccessorKind.Observe => new[] { T("old"), T("value"), T("field") },
                _ => System.Array.Empty<RaLanguage.Lexer.Tokens.Token>()
            };
        }

        // ---------------------------------------------------------------------
        // Loops / conditionals
        // ---------------------------------------------------------------------

        private static void WalkFor(ForNode fr, State s)
        {
            Walk(fr.StartValueNode, s);
            Walk(fr.EndValueNode, s);
            if (fr.StepValueNode != null) Walk(fr.StepValueNode, s);

            var saved = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
            var inductionName = fr.VarNameTok.Value?.ToString();
            if (!string.IsNullOrEmpty(inductionName)) s.AllocateLocal(inductionName!);
            Walk(fr.BodyNode, s);
            s.CurrentScope = saved;
        }

        private static void WalkForEach(ForEachNode fe, State s)
        {
            Walk(fe.CollectionNode, s);
            var saved = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
            var name = fe.VarNameToken.Value?.ToString();
            if (!string.IsNullOrEmpty(name)) s.AllocateLocal(name!);
            Walk(fe.BodyNode, s);
            s.CurrentScope = saved;
        }

        private static void WalkForAwait(ForAwaitNode fa, State s)
        {
            Walk(fa.StreamNode, s);
            var saved = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
            var name = fa.VarNameToken.Value?.ToString();
            if (!string.IsNullOrEmpty(name)) s.AllocateLocal(name!);
            Walk(fa.BodyNode, s);
            s.CurrentScope = saved;
        }

        private static void WalkIf(IfNode ifn, State s)
        {
            foreach (var c in ifn.Cases)
            {
                Walk(c.Item1, s);
                var saved = s.CurrentScope;
                s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
                Walk(c.Item2, s);
                s.CurrentScope = saved;
            }
            if (ifn.ElseCase != null)
            {
                var saved = s.CurrentScope;
                s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
                Walk(ifn.ElseCase.Value.Item1, s);
                s.CurrentScope = saved;
            }
        }

        private static void WalkTry(TryNode tn, State s)
        {
            Walk(tn.TryBody, s);
            if (tn.CatchBody != null)
            {
                var saved = s.CurrentScope;
                s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
                var catchName = tn.CatchVarTok?.Value?.ToString();
                if (!string.IsNullOrEmpty(catchName)) s.AllocateLocal(catchName!);
                Walk(tn.CatchBody, s);
                s.CurrentScope = saved;
            }
            if (tn.FinallyBody != null) Walk(tn.FinallyBody, s);
        }

        private static void WalkSwitch(SwitchNode sw, State s)
        {
            Walk(sw.Expression, s);
            foreach (var c in sw.Cases)
            {
                foreach (var label in c.Labels) Walk(label, s);
                if (c.Body != null)
                {
                    var saved = s.CurrentScope;
                    s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
                    Walk(c.Body, s);
                    s.CurrentScope = saved;
                }
            }
        }

        private static void WalkMatch(MatchNode m, State s)
        {
            Walk(m.Scrutinee, s);
            foreach (var arm in m.Arms)
            {
                var saved = s.CurrentScope;
                s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
                BindPatternNames(arm.Pattern, s);
                if (arm.Guard != null) Walk(arm.Guard, s);
                Walk(arm.Body, s);
                s.CurrentScope = saved;
            }
        }

        private static void BindPatternNames(PatternNode? p, State s)
        {
            if (p == null) return;
            switch (p)
            {
                case VariablePatternNode v:
                    if (!string.IsNullOrEmpty(v.Name) && v.Name != "_") s.AllocateLocalIfAbsent(v.Name);
                    break;
                case VariantPatternNode vp:
                    if (vp.SubPatterns != null)
                        foreach (var sub in vp.SubPatterns) BindPatternNames(sub, s);
                    break;
                case TuplePatternNode tp:
                    foreach (var sub in tp.Elements) BindPatternNames(sub, s);
                    break;
                case ListPatternNode lp:
                    foreach (var sub in lp.Elements) BindPatternNames(sub, s);
                    if (lp.Rest != null) BindPatternNames(lp.Rest, s);
                    break;
                case StructPatternNode sp:
                    foreach (var (field, sub) in sp.Fields)
                    {
                        if (sub != null) BindPatternNames(sub, s);
                        else if (!string.IsNullOrEmpty(field)) s.AllocateLocalIfAbsent(field);
                    }
                    break;
                case RestPatternNode rp:
                    if (!string.IsNullOrEmpty(rp.BindName)) s.AllocateLocalIfAbsent(rp.BindName!);
                    break;
                case LiteralPatternNode lit:
                    // Literal compare — no bindings, but walk the expression so
                    // any identifier it references gets resolved.
                    if (lit.Expression != null) Walk(lit.Expression, s);
                    break;
                // Extended patterns: allocate their binders so the IR lowering can
                // confirm a slot (else the binding form silently falls back to the
                // visitor). Mirrors the visitor's name-based binding set.
                case TypePatternNode tpn:
                    if (!string.IsNullOrEmpty(tpn.BinderName) && tpn.BinderName != "_")
                        s.AllocateLocalIfAbsent(tpn.BinderName!);
                    break;
                case AliasPatternNode apn:
                    BindPatternNames(apn.Inner, s);
                    if (!string.IsNullOrEmpty(apn.BinderName) && apn.BinderName != "_")
                        s.AllocateLocalIfAbsent(apn.BinderName);
                    break;
                case AndPatternNode andn:
                    foreach (var conj in andn.Conjuncts) BindPatternNames(conj, s);
                    break;
                case OrPatternNode orn:
                    foreach (var alt in orn.Alternatives) BindPatternNames(alt, s);
                    break;
                case NotPatternNode notn:
                    BindPatternNames(notn.Inner, s);
                    break;
                case MapPatternNode mpn:
                    foreach (var (key, val) in mpn.Entries) { Walk(key, s); BindPatternNames(val, s); }
                    break;
                case RangePatternNode rgn:
                    if (rgn.Lo != null) Walk(rgn.Lo, s);
                    if (rgn.Hi != null) Walk(rgn.Hi, s);
                    break;
                case RelationalPatternNode rln:
                    if (rln.Operand != null) Walk(rln.Operand, s);
                    break;
            }
        }

        private static void WalkSuperFor(SuperForNode sfn, State s)
        {
            var saved = s.CurrentScope;
            s.CurrentScope = new ScopeRecord(saved!.Frame, parent: saved);
            foreach (var init in sfn.InitializationNodes) Walk(init, s);
            foreach (var cond in sfn.ConditionNodes) Walk(cond, s);
            foreach (var step in sfn.StepNodes) Walk(step, s);
            Walk(sfn.BodyNode, s);
            s.CurrentScope = saved;
        }

        private static void WalkRetry(RetryNode r, State s)
        {
            Walk(r.CountNode, s);
            if (r.DelayNode != null) Walk(r.DelayNode, s);
            Walk(r.BodyNode, s);
            if (r.ElseNode != null) Walk(r.ElseNode, s);
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private sealed class State
        {
            public ScopeRecord? CurrentScope;

            private readonly Stack<FrameInfo> _frameStack = new Stack<FrameInfo>();
            private int _nextFrameId = 0;

            public FrameInfo? PushFrame(string displayName, bool isFunctionRoot, bool isScript, bool isMethodFrame = false)
            {
                if (_nextFrameId > MaxFrames) return null;
                var parent = _frameStack.Count > 0 ? _frameStack.Peek() : null;
                var f = new FrameInfo(_nextFrameId++, displayName, isFunctionRoot, isScript, isMethodFrame, parent);
                _frameStack.Push(f);
                return f;
            }

            public void PopFrame()
            {
                if (_frameStack.Count > 0) _frameStack.Pop();
            }

            public BindingId AllocateLocal(string name, BindingKind? kindOverride = null)
            {
                var scope = CurrentScope!;
                var frame = scope.Frame;
                int slot = frame.AllocateSlot();
                // A name del'd somewhere in this frame must never be slot-promoted
                // (the `del`'s symbol-table Remove has to be observable to every
                // read). Still consume the slot so non-del'd siblings keep their
                // slot ids, but bind the NAME to Unresolved → every reference
                // routes through OP_LOAD_GLOBAL / OP_STORE_GLOBAL, and the
                // declaration's OP_DECLARE_LOCAL skips slot registration.
                if (frame.DeletedNames.Contains(name))
                {
                    scope.Locals[name] = BindingId.Unresolved;
                    return BindingId.Unresolved;
                }
                if (slot > 0xFFFF) return BindingId.Unresolved;
                var id = new BindingId(frame.FrameId, slot);
                scope.Locals[name] = id;
                return id;
            }

            public BindingId AllocateLocalIfAbsent(string name)
            {
                var scope = CurrentScope!;
                if (scope.Locals.TryGetValue(name, out var existing)) return existing;
                return AllocateLocal(name);
            }

            // Resolve a name reference. Returns the BindingId and its Kind so
            // the caller can stamp the AST node.
            public (BindingId id, BindingKind kind) LookupReference(string name)
            {
                var scope = CurrentScope;
                if (scope == null) return (BindingId.Unresolved, BindingKind.Unresolved);

                var ownerFrame = scope.Frame;

                // Walk scope chain inside the current function frame first.
                var cur = scope;
                while (cur != null && cur.Frame == ownerFrame)
                {
                    if (cur.Locals.TryGetValue(name, out var id))
                        return (id, ownerFrame.IsScript ? BindingKind.Global : BindingKind.Local);
                    cur = cur.Parent;
                }

                // Walk up across outer function frames.
                while (cur != null)
                {
                    if (cur.Locals.TryGetValue(name, out var id))
                    {
                        if (cur.Frame.IsScript)
                            return (id, BindingKind.Global);
                        RegisterCapture(ownerFrame, name, id);
                        return (id, BindingKind.Capture);
                    }
                    cur = cur.Parent;
                }

                return (BindingId.Unresolved, BindingKind.Unresolved);
            }

            private static void RegisterCapture(FrameInfo frame, string name, BindingId source)
            {
                if (frame.CaptureIndexByName.ContainsKey(name)) return;
                int idx = frame.Captures.Count;
                frame.CaptureIndexByName[name] = idx;
                var cap = new ResolvedCapture(name, source, isExplicit: false);
                frame.Captures.Add(cap);
                if (frame.OwnerNode != null)
                {
                    frame.OwnerNode.ResolvedCaptures ??= new List<ResolvedCapture>();
                    frame.OwnerNode.ResolvedCaptures.Add(cap);
                }
            }
        }

        internal sealed class FrameInfo
        {
            public readonly int FrameId;
            public readonly string DisplayName;
            public readonly bool IsFunctionRoot;
            public readonly bool IsScript;
            public readonly bool IsMethodFrame;
            public readonly FrameInfo? Parent;

            public int NextSlot;
            public readonly List<ResolvedCapture> Captures = new List<ResolvedCapture>();
            public readonly Dictionary<string, int> CaptureIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);

            // Names that appear in a `del name` statement somewhere in THIS
            // frame's body (not descending into nested function frames). A
            // del'd name must be entirely symbol-table-backed — never promoted
            // to a frame slot — so the `del`'s symbol-table Remove is observable
            // to every read of that name (including reads textually before the
            // del that re-execute after it across a loop back-edge). AllocateLocal
            // and LookupReference force such names to (Unresolved, Unresolved),
            // routing every access through OP_LOAD_GLOBAL / OP_STORE_GLOBAL.
            // Collected once per frame at frame-open time (CollectDeletedNames).
            public readonly HashSet<string> DeletedNames = new HashSet<string>(StringComparer.Ordinal);

            // Owning FunctionDefinitionNode, if this frame was opened for one.
            // Null for the script frame and synthetic struct-method frames.
            public FunctionDefinitionNode? OwnerNode;

            public FrameInfo(int frameId, string displayName, bool isFunctionRoot, bool isScript, bool isMethodFrame, FrameInfo? parent)
            {
                FrameId = frameId;
                DisplayName = displayName;
                IsFunctionRoot = isFunctionRoot;
                IsScript = isScript;
                IsMethodFrame = isMethodFrame;
                Parent = parent;
                NextSlot = 0;
            }

            public int AllocateSlot() => NextSlot++;
        }

        internal sealed class ScopeRecord
        {
            public readonly FrameInfo Frame;
            public readonly ScopeRecord? Parent;
            public readonly Dictionary<string, BindingId> Locals = new Dictionary<string, BindingId>(StringComparer.Ordinal);

            public ScopeRecord(FrameInfo frame, ScopeRecord? parent)
            {
                Frame = frame;
                Parent = parent;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Visitors.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.IR
{
    // Lowers an AST to bytecode. Per RA_VM_MIGRATION.md §4, IR coverage
    // grows by milestone:
    //
    //   M1 — single OP_VISIT_AST pass-through.
    //   M2 — primitives, variable reads, arithmetic, comparisons, unary.
    //   M3 — if / while / do-while, break / continue / return / pass /
    //        retry, short-circuit and/or, unary minus, variable assignment,
    //        single-target variable declaration.
    //   M4 — scope-wrapped bodies (`if` / `while` / `do-while` now accept
    //        bodies with nested `var x = ...`), `for` (numeric range with
    //        optional step), shared scope opcodes (PushScope / PopScope /
    //        ClearScope / SetLocalDirect / AssignBinding) — this file.
    //   M5+ — function calls/definitions, OOP, async, ...
    //
    // The unit of fallback is the statement. Mid-statement IrCompileException
    // rolls back the tentative bytecode and emits OP_VISIT_AST for that
    // statement.
    public static class IrCompiler
    {
        private sealed class State
        {
            public readonly InstructionBuilder Code = new();
            public readonly ConstantPool Consts = new();
            public readonly NamePool Names = new();
            public readonly List<AstNode> AstRefs = new();
            public readonly List<Parser.Nodes.Operations.CastNode> CastRefs = new();
            public readonly List<Parser.Nodes.Structs.MemberAccessNode> MemberAccessRefs = new();
            public readonly List<Parser.Nodes.Structs.MemberAssignmentNode> MemberAssignRefs = new();
            public readonly List<Parser.Nodes.Variables.ListAssignmentNode> ListAssignRefs = new();
            public readonly List<Parser.Nodes.Enums.EnumAccessNode> EnumAccessRefs = new();
            public readonly List<Parser.Nodes.Special.TypeofNode> TypeofRefs = new();
            public readonly List<Parser.Nodes.Special.NameofNode> NameofRefs = new();
            public readonly List<Parser.Nodes.Operations.DereferenceNode> DerefRefs = new();
            public readonly List<Parser.Nodes.Classes.SuperNode> SuperRefs = new();
            public readonly List<Parser.Nodes.Functions.FunctionDefinitionNode> FuncDefRefs = new();
            public readonly List<AstNode> DefineRefs = new();
            // L5: flat one-shot definition descriptors (OP_DEFINE_TYPE pool).
            public readonly List<Defs.TypeDef> TypeDefs = new();
            public readonly List<ExceptionHandler> EhTable = new();
            public readonly Stack<LoopContext> Loops = new();
            public int MaxTempUsed = 1;

            // Tracks the *static* nesting depth of OP_PUSH_SCOPE emissions.
            // Incremented before emitting PushScope, decremented after
            // emitting PopScope. break / continue use this to compute the
            // number of pops they must emit before their jump (since the JMP
            // unconditionally moves PC and the pops never execute via
            // natural fall-through).
            public int ScopeDepth = 0;

            // M14: slot index → declared name. Populated every time the IR
            // emits a slot opcode (LoadLocalS / StoreLocalS) or registers a
            // declaration in DeclSlotByAstRef. The dispatch loop reads names
            // from RaFunction.SlotNames only on the cold "slot not yet
            // populated" lazy-fallback path, so building this from the
            // emitter side keeps the table accurate without an extra AST
            // sweep.
            public readonly Dictionary<int, string> SlotNames = new();
            public int MaxSlot = -1;

            // M16: the frame this State is compiling for. CompileScript sets 0
            // (top-level script); CompileFunction sets the FunctionDefinitionNode's
            // FrameId. IsSlotEligible uses this to admit slot lowering only for
            // bindings in the current frame — captures / outer-frame accesses
            // still take the OP_LOAD_GLOBAL path.
            public int FrameId = 0;

            // Active typed-int iter slots for lazy-long for-loops in scope.
            // Keyed by iter binding name → byte slot index into the parent
            // frame's `Slots[]` array. Used by `TryEmitSelfAdditiveSlot` to
            // route `sum = sum + iterName` into `AddIntoSlotI` (typed RHS
            // read direct from the long slot, skipping the boxed mirror in
            // the symbol entry). Pushed by `CompileForLazyLong` before body
            // compilation, popped after.
            public readonly Dictionary<string, byte> ActiveTypedIters = new();

            // Counter incremented every time `TryEmitSelfAdditiveSlot`
            // routes an iter-name access through `AddIntoSlotI`. The
            // owning `CompileForLazyLong` checks the delta over its body
            // to decide whether the boxed iter publish (BoxI +
            // AssignBinding) is dead.
            public int RedirectedTypedIterAccessCount = 0;

            // Typed accumulator promotions for the enclosing lazy-long
            // for-loop. Keyed by accumulator binding name → typed Int64
            // slot. `CompileForLazyLong` pre-loop emits LoadLocalS+UnboxI
            // from the binding's SymbolEntry into this slot. `TryEmit-
            // SelfAdditiveSlot` rewrites `acc = acc ± typedRhs` into a
            // pure `AddII / SubII` that touches NO symbol entry and
            // allocates zero NumberValues. Post-loop, the typed slot is
            // boxed back via BoxI+StoreLocalS so any code observing
            // `acc` after the loop sees the freshly-computed value.
            public readonly Dictionary<string, (byte LongSlot, BindingId Binding)> TypedAccumulators = new();

            // Pre-loaded typed slots holding literal int64 constants used
            // as RHS of typed-accumulator self-additives (e.g. `counter
            // = counter + 1`). `CompileForLazyLong` walks the body for
            // each accumulator, collects every distinct literal RHS, and
            // emits a single `LoadIntS64` (or `LoadConst + UnboxI` for
            // > int16 literals) into a fresh typed slot before loop_top.
            // `TryEmitSelfAdditiveSlot` then emits `AddII / SubII acc,
            // acc, literalSlot` — pure typed, zero per-iter alloc, zero
            // const-pool dispatch. Cleared after the loop's body
            // compilation finishes.
            public readonly Dictionary<long, byte> TypedAccumulatorLiterals = new();

            // Never-mutated local bindings promoted to a typed Int64
            // slot for the lifetime of the enclosing for-loop. Used as
            // the non-iter operand of typed comparison redirects:
            // `iter ⋈ max` (where `max` is a const-after-init local)
            // lowers to `LtII / GtII / EqII / etc.` reading both
            // operands as typed longs — no boxed mirror, no per-iter
            // alloc, no iter publish required.
            //
            // Pre-loop emits `LoadLocalS tempBoxed, binding; UnboxI
            // longSlot, tempBoxed` once. The typed slot stays valid
            // throughout the loop because the binding is never
            // written. Post-loop, no box-back is needed — SymbolEntry
            // already holds the original value.
            public readonly Dictionary<string, (byte LongSlot, BindingId Binding)> TypedLongBindings = new();

            // Compile-time dirty set for typed accumulators. An accumulator
            // is "dirty" when its typed slot holds a value that hasn't yet
            // been published into its boxed SymbolEntry mirror. Boxed reads
            // of a dirty accumulator emit `BoxI + StoreLocalS` to refresh
            // the entry; non-dirty boxed reads skip the publish entirely
            // (SymbolEntry already matches typed slot).
            //
            // Transitions:
            //   * Typed self-additive (AddII / SubII via `TryEmit-
            //     SelfAdditiveSlot`) → add to dirty.
            //   * Boxed read (CompileExpression VariableAccess) → publish
            //     if dirty, then remove from dirty.
            //   * Control-flow boundary (if/while/for/try compile-time
            //     epilogue) → conservatively re-add every typed acc to
            //     dirty (compile-time can't prove the runtime path).
            //
            // De-dupes multiple in-statement reads after a single write:
            // `print(sum + sum)` publishes once instead of twice;
            // `print(sum); print(sum);` publishes once for the first read
            // and skips the second.
            public readonly HashSet<string> DirtyTypedAccs = new();

            // Names of bindings whose declaration initializer is statically
            // PROVABLY NUMERIC (a number literal, a negation/bitwise-not of
            // one, or an arithmetic expression over such — never `+`, which
            // is string-overloaded). Populated once per function by
            // `CollectNumericInitBindingNames` before the body is compiled.
            //
            // The lazy-long while/for/foreach optimization promotes a loop
            // accumulator / counter to a typed Int64 slot by `UnboxI`-ing its
            // boxed value. For a non-numeric accumulator (`var out = ""`) that
            // UnboxI yields 0 and the body's `out = out + x` miscompiles to a
            // numeric AddII (result 0). The promotion gate therefore admits an
            // accumulator / while-iter ONLY when its name is in this set —
            // sound by construction (an unknown-typed binding is never
            // promoted). Benchmarks are unaffected: every hot accumulator /
            // counter is initialized from a numeric literal (`var sum = 0`).
            public readonly HashSet<string> NumericInitBindings = new(StringComparer.Ordinal);

            // PERF (O(n) string building): names whose declaration initializer
            // is provably a STRING (string literal / interpolation), used to
            // gate the loop string-accumulator promotion. `StringAccumulators`
            // maps a promoted accumulator name to its per-frame StringBuilder
            // index (the StrAcc* opcodes' imm16); `NextStrAcc` allocates those
            // indices and becomes RaFunction.StrAccCount at finalize.
            public readonly HashSet<string> StringInitBindings = new(StringComparer.Ordinal);
            public readonly Dictionary<string, int> StringAccumulators = new(StringComparer.Ordinal);
            public int NextStrAcc;
            // Names the CURRENT loop will promote to a StringBuilder. Computed
            // BEFORE the iter-publish decision so `s = s + i` (RHS = typed iter)
            // counts as a redirectable iter access (it lowers to StrAccAppendI
            // reading the typed slot directly — no boxed publish needed). The
            // actual promotion (slot alloc + StrAccBegin) still happens later.
            public readonly HashSet<string> PromotableStrAccNames = new(StringComparer.Ordinal);

            // Loop-invariant pure-expression RHS slots for typed
            // accumulators. Keyed by the AstNode of the RHS expression;
            // value is the typed Int64 slot pre-loaded once before
            // loop_top. `TryEmitSelfAdditiveSlot` emits a pure AddII /
            // SubII when the assignment's RHS AstNode matches an entry.
            // Eliminates the per-iter boxed AddIntoSlot allocation
            // (NumberValue per iter ≈ 122 bytes; 1M iters ≈ 120MB on
            // bench_invariant.ra).
            public readonly Dictionary<AstNode, byte> TypedAccumulatorExprs = new();

            // M88 (#29): when true, the enclosing lazy-counter loop is
            // statically guaranteed to execute its body at least once.
            // Loop-invariant pure-expression RHS pre-loads admitted by
            // `IsLoopInvariantPureNumericExpr` can therefore include
            // Div / Mod / Pow — any error their evaluation would raise
            // is one the original boxed dispatch would also raise on
            // the first iteration, with the same diagnostic. Set by
            // `CompileForLazyLong` when (start < end) holds for the
            // step direction, and by `CompileForEachLazyIntRange`
            // unconditionally (its caller already verifies start <=
            // end). Conditional preheader-emit paths (while-counter)
            // set it true after the runtime guard so the same admit
            // rule applies on the in-loop branch.
            public bool LoopGuaranteedToEnter = false;

            public void RegisterSlot(int slot, string? name)
            {
                if (slot > MaxSlot) MaxSlot = slot;
                if (!string.IsNullOrEmpty(name) && !SlotNames.ContainsKey(slot))
                    SlotNames[slot] = name!;
            }

            // M44: pc → source-span tracking. Populated at statement
            // / expression compile entry so runtime errors raised inside
            // the dispatch loop can resolve a real position via binary
            // search on PcSpansPc. Entries are recorded ONCE per source
            // node — the spans are coarse-grained (statement-level) but
            // dramatically better than `DummyPos` 1:1.
            public readonly List<int> PcSpanPcs = new();
            public readonly List<Errors.SourceSpan> PcSpanSpans = new();
            public void RecordPcSpan(AstNode node)
            {
                int pc = Code.Pc;
                if (PcSpanPcs.Count > 0 && PcSpanPcs[^1] == pc) return;
                PcSpanPcs.Add(pc);
                PcSpanSpans.Add(new Errors.SourceSpan(node.PositionStart, node.PositionEnd));
            }
        }

        // M87: snapshot of the typed-promotion dictionaries. Captured at
        // the entry of a nested lazy-counter compile and replayed on
        // exit so an inner loop's `Clear()` of literals / typed-bindings
        // / typed-acc-exprs does not blow away the OUTER loop's state.
        // Without this, a nested `while jj < 4 { ... }` inside an outer
        // `while ii < 3 { ... ii = ii + 1 }` would leave outer `ii`
        // mid-flight, with the post-inner `ii = ii + 1` falling through
        // to a boxed path that no longer updates `ii`'s typed slot —
        // an infinite loop.
        private readonly struct TypedPromotionSnapshot
        {
            public readonly Dictionary<string, byte> ActiveTypedIters;
            public readonly Dictionary<string, (byte LongSlot, BindingId Binding)> TypedAccumulators;
            public readonly Dictionary<long, byte> TypedAccumulatorLiterals;
            public readonly Dictionary<string, (byte LongSlot, BindingId Binding)> TypedLongBindings;
            public readonly Dictionary<AstNode, byte> TypedAccumulatorExprs;
            public readonly HashSet<string> DirtyTypedAccs;
            // String accumulator name→StrAcc-index map (NextStrAcc is NOT
            // snapshotted — it is a monotonic slot allocator == StrAccCount).
            public readonly Dictionary<string, int> StringAccumulators;

            public TypedPromotionSnapshot(State st)
            {
                ActiveTypedIters = new Dictionary<string, byte>(st.ActiveTypedIters);
                TypedAccumulators = new Dictionary<string, (byte, BindingId)>(st.TypedAccumulators);
                TypedAccumulatorLiterals = new Dictionary<long, byte>(st.TypedAccumulatorLiterals);
                TypedLongBindings = new Dictionary<string, (byte, BindingId)>(st.TypedLongBindings);
                TypedAccumulatorExprs = new Dictionary<AstNode, byte>(st.TypedAccumulatorExprs);
                DirtyTypedAccs = new HashSet<string>(st.DirtyTypedAccs);
                StringAccumulators = new Dictionary<string, int>(st.StringAccumulators, StringComparer.Ordinal);
            }

            public void RestoreInto(State st)
            {
                st.StringAccumulators.Clear();
                foreach (var kv in StringAccumulators) st.StringAccumulators[kv.Key] = kv.Value;
                st.ActiveTypedIters.Clear();
                foreach (var kv in ActiveTypedIters) st.ActiveTypedIters[kv.Key] = kv.Value;
                st.TypedAccumulators.Clear();
                foreach (var kv in TypedAccumulators) st.TypedAccumulators[kv.Key] = kv.Value;
                st.TypedAccumulatorLiterals.Clear();
                foreach (var kv in TypedAccumulatorLiterals) st.TypedAccumulatorLiterals[kv.Key] = kv.Value;
                st.TypedLongBindings.Clear();
                foreach (var kv in TypedLongBindings) st.TypedLongBindings[kv.Key] = kv.Value;
                st.TypedAccumulatorExprs.Clear();
                foreach (var kv in TypedAccumulatorExprs) st.TypedAccumulatorExprs[kv.Key] = kv.Value;
                st.DirtyTypedAccs.Clear();
                foreach (var s in DirtyTypedAccs) st.DirtyTypedAccs.Add(s);
            }
        }

        // ---- L0 parity-oracle support (RA_FULL_IR_LOWERING_PLAN §4b) ----
        //
        // When a node kind is in this set, CompileStatementWithFallback
        // refuses to lower it natively and emits OP_NATIVE_DEFINE instead
        // (provided the kind has a HasNativeDefineRoute). This lets a
        // differential harness run the SAME program twice — native vs
        // visitor-fallback — and assert byte-identical observable behaviour,
        // which is the gate that lets later phases delete a fallback safely.
        //
        // Empty in every normal compile; a single `Count != 0` check gates
        // the hot path, so there is zero cost when unused. Process-global +
        // set/cleared by the harness around each run; never touched by the
        // production CLI / archive paths.
        private static readonly HashSet<AstNodeType> s_forceFallbackKinds = new();

        public static void SetForceFallback(System.Collections.Generic.IEnumerable<AstNodeType> kinds)
        {
            s_forceFallbackKinds.Clear();
            foreach (var k in kinds) s_forceFallbackKinds.Add(k);
        }

        public static void ClearForceFallback() => s_forceFallbackKinds.Clear();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsForcedFallback(AstNodeType t)
            => s_forceFallbackKinds.Count != 0 && s_forceFallbackKinds.Contains(t);

        // Exposed so the harness can reject a requested kind that has no
        // visitor route (EmitFallback would hard-error on it).
        public static bool IsFallbackRoutable(AstNodeType t) => HasNativeDefineRoute(t);

        public static RaFunction CompileScript(AstNode root, string sourceName)
        {
            var fn = new RaFunction(sourceName ?? "<script>");
            fn.FrameId = 0;
            fn.MutatedNames = CollectMutatedNames(root);
            fn.HasImports = AstContainsImport(root);
            fn.CalleeMutatedNames = CollectCalleeMutatedNames(root);

            var st = new State();
            st.FrameId = 0;
            st.NumericInitBindings.UnionWith(CollectNumericInitBindingNames(root));
            st.StringInitBindings.UnionWith(CollectStringInitBindingNames(root));
            const byte ScratchSlot = 0;
            var statements = FlattenStatements(root);

            foreach (var stmt in statements)
            {
                CompileStatementWithFallback(stmt, st, ScratchSlot);
            }

            st.Code.Emit3(Opcode.Halt, ScratchSlot, 0, 0);

            FinalizeFn(fn, st);
            return fn;
        }

        // Walks `root` collecting every name targeted by either a
        // `VariableAssignment` (write to existing binding) or a
        // `VariableDeclaration` (shadow / create new binding). The set
        // identifies names whose `SymbolEntry.Value` may change during
        // the function's execution — LICM consumes this to decide
        // whether a `LoadLocalS` is safe to hoist out of a loop.
        //
        // Conservative across nested scopes / closures: any write
        // anywhere in the function disqualifies the name.
        private static HashSet<string> CollectMutatedNames(AstNode? root)
        {
            var names = new HashSet<string>();
            CollectMutatedNamesWalk(root, names);
            return names;
        }

        private static void CollectMutatedNamesWalk(AstNode? node, HashSet<string> names)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (!string.IsNullOrEmpty(va.Name)) names.Add(va.Name);
                    CollectMutatedNamesWalk(va.ValueNode, names);
                    return;
                }
                case AstNodeType.VariableDeclaration:
                {
                    // A `var x = ...` creates the SymbolEntry once at the
                    // statement's PC — the SE.Value never changes after
                    // that unless a later `x = ...` reassigns it (and
                    // then the VariableAssignment branch above adds the
                    // name). Declarations alone are NOT mutations, so
                    // we only walk the initializer expressions to catch
                    // any nested writes hiding inside `var y = (z = 5)`.
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    foreach (var d in vd.Declarations)
                    {
                        if (d.Item2 != null) CollectMutatedNamesWalk(d.Item2, names);
                    }
                    return;
                }
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectMutatedNamesWalk(c, names);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectMutatedNamesWalk(cs.Condition, names);
                        CollectMutatedNamesWalk(cs.Expr, names);
                    }
                    if (ifn.ElseCase.HasValue)
                        CollectMutatedNamesWalk(ifn.ElseCase.Value.Expr, names);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectMutatedNamesWalk(wn.ConditionNode, names);
                    CollectMutatedNamesWalk(wn.BodyNode, names);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectMutatedNamesWalk(dw.ConditionNode, names);
                    CollectMutatedNamesWalk(dw.BodyNode, names);
                    return;
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    // The iter variable itself is "assigned" each iter —
                    // record it so LICM treats it as mutable.
                    string? iterName = fn.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(iterName)) names.Add(iterName);
                    CollectMutatedNamesWalk(fn.StartValueNode, names);
                    CollectMutatedNamesWalk(fn.EndValueNode, names);
                    CollectMutatedNamesWalk(fn.StepValueNode, names);
                    CollectMutatedNamesWalk(fn.BodyNode, names);
                    return;
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    string? iterName = fe.VarNameToken.Value?.ToString();
                    if (!string.IsNullOrEmpty(iterName)) names.Add(iterName);
                    CollectMutatedNamesWalk(fe.CollectionNode, names);
                    CollectMutatedNamesWalk(fe.BodyNode, names);
                    return;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    CollectMutatedNamesWalk(bo.LeftNode, names);
                    CollectMutatedNamesWalk(bo.RightNode, names);
                    return;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    CollectMutatedNamesWalk(uo.Node, names);
                    return;
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    CollectMutatedNamesWalk(fc.NodeToCall, names);
                    foreach (var arg in fc.ArgNodes)
                        CollectMutatedNamesWalk(arg.Expr, names);
                    return;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    CollectMutatedNamesWalk(tn.Condition, names);
                    CollectMutatedNamesWalk(tn.TrueExpression, names);
                    CollectMutatedNamesWalk(tn.FalseExpression, names);
                    return;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    CollectMutatedNamesWalk(nc.Left, names);
                    CollectMutatedNamesWalk(nc.Right, names);
                    return;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    if (rn.NodeToReturn != null) CollectMutatedNamesWalk(rn.NodeToReturn, names);
                    return;
                }
                case AstNodeType.Throw:
                {
                    var tn = (Parser.Nodes.Statements.ThrowNode)node;
                    if (tn.Expression != null) CollectMutatedNamesWalk(tn.Expression, names);
                    return;
                }
                case AstNodeType.MemberAssignment:
                {
                    var ma = (Parser.Nodes.Structs.MemberAssignmentNode)node;
                    CollectMutatedNamesWalk(ma.TargetNode, names);
                    CollectMutatedNamesWalk(ma.ValueNode, names);
                    return;
                }
                case AstNodeType.ListAssignment:
                {
                    var la = (Parser.Nodes.Variables.ListAssignmentNode)node;
                    CollectMutatedNamesWalk(la.Target, names);
                    CollectMutatedNamesWalk(la.Value, names);
                    return;
                }
                case AstNodeType.Try:
                {
                    var tn = (Parser.Nodes.Special.TryNode)node;
                    CollectMutatedNamesWalk(tn.TryBody, names);
                    CollectMutatedNamesWalk(tn.CatchBody, names);
                    if (tn.FinallyBody != null) CollectMutatedNamesWalk(tn.FinallyBody, names);
                    // catch var is bound — treat as mutated.
                    if (tn.CatchVarTok.HasValue)
                    {
                        string? cn = tn.CatchVarTok.Value.Value?.ToString();
                        if (!string.IsNullOrEmpty(cn)) names.Add(cn);
                    }
                    return;
                }
                case AstNodeType.FunctionDefinition:
                {
                    // Function definition binds its name AND its body
                    // may capture / mutate outer names. Walk inside.
                    var fdn = (Parser.Nodes.Functions.FunctionDefinitionNode)node;
                    string? fname = fdn.VarNameTok?.Value?.ToString();
                    if (!string.IsNullOrEmpty(fname)) names.Add(fname);
                    CollectMutatedNamesWalk(fdn.BodyNode, names);
                    return;
                }
                default:
                    // Conservative: any unhandled node may contain
                    // assignments deeper down. We err on the side of
                    // safety — but since LICM only uses MutatedNames
                    // to ADMIT hoisting (a missing name → safe to
                    // hoist), missing a write would be unsafe. So we
                    // should be CAUTIOUS and treat unknown nodes as
                    // "may mutate everything" — but that's untenable
                    // (would mark every name as mutated). Trade-off:
                    // accept that less-common nodes (Borrow, Spawn,
                    // Match, etc.) don't contribute to the set, and
                    // their absence may cause LICM to mis-hoist in
                    // pathological cases. The corpus regression sweep
                    // catches such cases empirically.
                    return;
            }
        }

        // Builds the set of binding names that are statically PROVABLY
        // numeric: a name qualifies iff at least one declaration/assignment
        // gives it a provably-numeric value AND no declaration/assignment
        // ever gives it a provably-non-numeric value. Consumed by the typed-
        // accumulator promotion gate (a string accumulator must never be
        // promoted to a typed Int64 slot). Name-keyed to match the rest of
        // the accumulator machinery; conservative across scopes (a name used
        // numerically in one scope and as a string in another is excluded).
        private static HashSet<string> CollectNumericInitBindingNames(AstNode? root)
        {
            var numeric = new HashSet<string>(StringComparer.Ordinal);
            var tainted = new HashSet<string>(StringComparer.Ordinal);
            CollectNumericInitWalk(root, numeric, tainted);
            numeric.ExceptWith(tainted);
            return numeric;
        }

        private enum InitTypeClass { Numeric, NonNumeric, Unknown }

        // Classifies an initializer/RHS expression's static value type.
        private static InitTypeClass ClassifyInitExpr(AstNode? node)
        {
            if (node == null) return InitTypeClass.Unknown;
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                    return InitTypeClass.Numeric;
                case AstNodeType.String:
                case AstNodeType.Boolean:
                case AstNodeType.Null:
                case AstNodeType.List:
                case AstNodeType.Map:
                case AstNodeType.Set:
                case AstNodeType.Tuple:
                case AstNodeType.FormattedInterpolation:
                    return InitTypeClass.NonNumeric;
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    var t = uo.OpTok.Type;
                    // `!x` is boolean. `-x` / `~x` are numeric iff operand is.
                    if (t == Lexer.Tokens.TokenType.MINUS || t == Lexer.Tokens.TokenType.BITWISE_NOT)
                        return ClassifyInitExpr(uo.Node) == InitTypeClass.Numeric
                            ? InitTypeClass.Numeric : InitTypeClass.Unknown;
                    return InitTypeClass.Unknown;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    var t = bo.OpTok.Type;
                    switch (t)
                    {
                        // Strictly-numeric arithmetic / bitwise ops. NOTE: `+`
                        // is intentionally excluded — it is string-overloaded.
                        case Lexer.Tokens.TokenType.MINUS:
                        case Lexer.Tokens.TokenType.MUL:
                        case Lexer.Tokens.TokenType.DIV:
                        case Lexer.Tokens.TokenType.MODULO:
                        case Lexer.Tokens.TokenType.POW:
                        case Lexer.Tokens.TokenType.BITWISE_AND:
                        case Lexer.Tokens.TokenType.BITWISE_OR:
                        case Lexer.Tokens.TokenType.BITWISE_LEFT_SHIFT:
                        case Lexer.Tokens.TokenType.BITWISE_RIGHT_SHIFT:
                        case Lexer.Tokens.TokenType.BITWISE_LOGICAL_LEFT_SHIFT:
                        case Lexer.Tokens.TokenType.BITWISE_LOGICAL_RIGHT_SHIFT:
                        case Lexer.Tokens.TokenType.BITWISE_ROTATE_LEFT:
                        case Lexer.Tokens.TokenType.BITWISE_ROTATE_RIGHT:
                            // Numeric only when BOTH operands are provably numeric
                            // (an overloaded operator on user objects could differ).
                            return (ClassifyInitExpr(bo.LeftNode) == InitTypeClass.Numeric
                                    && ClassifyInitExpr(bo.RightNode) == InitTypeClass.Numeric)
                                ? InitTypeClass.Numeric : InitTypeClass.Unknown;
                        default:
                            return InitTypeClass.Unknown;
                    }
                }
                default:
                    return InitTypeClass.Unknown;
            }
        }

        private static void RecordInitClass(string? name, AstNode? value,
            HashSet<string> numeric, HashSet<string> tainted)
        {
            if (string.IsNullOrEmpty(name)) return;
            switch (ClassifyInitExpr(value))
            {
                case InitTypeClass.Numeric:    numeric.Add(name); break;
                case InitTypeClass.NonNumeric: tainted.Add(name); break;
            }
        }

        private static void CollectNumericInitWalk(
            AstNode? node, HashSet<string> numeric, HashSet<string> tainted)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.VariableDeclaration:
                {
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    foreach (var d in vd.Declarations)
                    {
                        RecordInitClass(d.Item1.Value?.ToString(), d.Item2, numeric, tainted);
                        if (d.Item2 != null) CollectNumericInitWalk(d.Item2, numeric, tainted);
                    }
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    // Only a plain `=` assignment redefines the value's type;
                    // a compound `+=` keeps whatever the binding already held.
                    if (va.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ)
                        RecordInitClass(va.Name, va.ValueNode, numeric, tainted);
                    CollectNumericInitWalk(va.ValueNode, numeric, tainted);
                    return;
                }
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectNumericInitWalk(c, numeric, tainted);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectNumericInitWalk(cs.Condition, numeric, tainted);
                        CollectNumericInitWalk(cs.Expr, numeric, tainted);
                    }
                    if (ifn.ElseCase.HasValue)
                        CollectNumericInitWalk(ifn.ElseCase.Value.Expr, numeric, tainted);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectNumericInitWalk(wn.ConditionNode, numeric, tainted);
                    CollectNumericInitWalk(wn.BodyNode, numeric, tainted);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectNumericInitWalk(dw.ConditionNode, numeric, tainted);
                    CollectNumericInitWalk(dw.BodyNode, numeric, tainted);
                    return;
                }
                case AstNodeType.For:
                {
                    var fnode = (Parser.Nodes.Statements.ForNode)node;
                    CollectNumericInitWalk(fnode.BodyNode, numeric, tainted);
                    return;
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    CollectNumericInitWalk(fe.BodyNode, numeric, tainted);
                    return;
                }
                case AstNodeType.Try:
                {
                    var tn = (Parser.Nodes.Special.TryNode)node;
                    CollectNumericInitWalk(tn.TryBody, numeric, tainted);
                    CollectNumericInitWalk(tn.CatchBody, numeric, tainted);
                    if (tn.FinallyBody != null) CollectNumericInitWalk(tn.FinallyBody, numeric, tainted);
                    return;
                }
                case AstNodeType.FunctionDefinition:
                {
                    var fdn = (Parser.Nodes.Functions.FunctionDefinitionNode)node;
                    CollectNumericInitWalk(fdn.BodyNode, numeric, tainted);
                    return;
                }
                case AstNodeType.NamespaceDeclaration:
                {
                    var ns = (Parser.Nodes.Namespaces.NamespaceDeclarationNode)node;
                    CollectNumericInitWalk(ns.Body, numeric, tainted);
                    return;
                }
                default:
                    return;
            }
        }

        // Names whose binding is provably a STRING throughout the function: a
        // string-literal / interpolation initializer, with every `=` assignment
        // either another string literal OR a `name = name + <expr>` self-append
        // (which preserves string-ness). Any other `=` taints the name. Mirrors
        // CollectNumericInitWalk's coverage so the gate is sound.
        private static HashSet<string> CollectStringInitBindingNames(AstNode? root)
        {
            var strs = new HashSet<string>(StringComparer.Ordinal);
            var tainted = new HashSet<string>(StringComparer.Ordinal);
            CollectStringInitWalk(root, strs, tainted);
            strs.ExceptWith(tainted);
            return strs;
        }

        private static void RecordStringInitClass(string? name, AstNode? value,
            HashSet<string> strs, HashSet<string> tainted)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (value != null
                && (value.NodeType == AstNodeType.String
                    || value.NodeType == AstNodeType.FormattedInterpolation))
            {
                strs.Add(name!);
                return;
            }
            // `name = name + …` (single or chained) keeps the binding a string
            // (string + x is always a string via StringValue.AddedTo) — the
            // canonical accumulator self-append; neutral.
            if (IsStringAppendChainShape(value, name!))
                return;
            // Anything else assigned with `=` makes the type non-string/unknown.
            tainted.Add(name!);
        }

        private static void CollectStringInitWalk(
            AstNode? node, HashSet<string> strs, HashSet<string> tainted)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.VariableDeclaration:
                {
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    foreach (var d in vd.Declarations)
                    {
                        RecordStringInitClass(d.Item1.Value?.ToString(), d.Item2, strs, tainted);
                        if (d.Item2 != null) CollectStringInitWalk(d.Item2, strs, tainted);
                    }
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ)
                        RecordStringInitClass(va.Name, va.ValueNode, strs, tainted);
                    CollectStringInitWalk(va.ValueNode, strs, tainted);
                    return;
                }
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectStringInitWalk(c, strs, tainted);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectStringInitWalk(cs.Condition, strs, tainted);
                        CollectStringInitWalk(cs.Expr, strs, tainted);
                    }
                    if (ifn.ElseCase.HasValue) CollectStringInitWalk(ifn.ElseCase.Value.Expr, strs, tainted);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectStringInitWalk(wn.ConditionNode, strs, tainted);
                    CollectStringInitWalk(wn.BodyNode, strs, tainted);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectStringInitWalk(dw.ConditionNode, strs, tainted);
                    CollectStringInitWalk(dw.BodyNode, strs, tainted);
                    return;
                }
                case AstNodeType.For:
                {
                    var fnode = (Parser.Nodes.Statements.ForNode)node;
                    CollectStringInitWalk(fnode.BodyNode, strs, tainted);
                    return;
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    CollectStringInitWalk(fe.BodyNode, strs, tainted);
                    return;
                }
                case AstNodeType.Try:
                {
                    var tn = (Parser.Nodes.Special.TryNode)node;
                    CollectStringInitWalk(tn.TryBody, strs, tainted);
                    CollectStringInitWalk(tn.CatchBody, strs, tainted);
                    if (tn.FinallyBody != null) CollectStringInitWalk(tn.FinallyBody, strs, tainted);
                    return;
                }
                case AstNodeType.FunctionDefinition:
                {
                    var fdn = (Parser.Nodes.Functions.FunctionDefinitionNode)node;
                    CollectStringInitWalk(fdn.BodyNode, strs, tainted);
                    return;
                }
                case AstNodeType.NamespaceDeclaration:
                {
                    var ns = (Parser.Nodes.Namespaces.NamespaceDeclarationNode)node;
                    CollectStringInitWalk(ns.Body, strs, tainted);
                    return;
                }
                default:
                    return;
            }
        }

        // Loop string-accumulator candidates: a string-init binding whose ONLY
        // in-loop access is a `name = name + <expr>` self-append. Then the boxed
        // `name` SymbolEntry can be left untouched during the loop (appends go
        // to a StringBuilder) and refreshed once on exit — turning O(n^2)
        // reallocating concatenation into O(n) append. Conservative: any node
        // shape the stat walk does not model counts as an opaque read, which
        // breaks the `reads == appends` equality and blocks promotion.
        private static List<(string Name, Pipeline.BindingId Binding)> CollectStringAccumulatorCandidates(AstNode body, State st)
        {
            var result = new List<(string, Pipeline.BindingId)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            GatherStringSelfAppendNames(body, names);
            foreach (var name in names)
            {
                if (!st.StringInitBindings.Contains(name)) continue;
                if (st.TypedAccumulators.ContainsKey(name)) continue;
                if (BodyDeclaresName(body, name)) continue;
                int reads = 0, writes = 0, appends = 0;
                CountStringAppendStats(body, name, ref reads, ref writes, ref appends);
                // Every read is the LHS operand of a self-append, and every
                // write IS a self-append. No other access exists.
                if (appends == 0 || reads != appends || writes != appends) continue;
                var binding = FindFirstBindingOfName(body, name);
                if (!binding.IsResolved) continue;
                if (!IsSlotEligible(binding, BindingKind.Local, st)
                    && !IsSlotEligible(binding, BindingKind.Global, st)
                    && !IsSlotEligible(binding, BindingKind.Parameter, st))
                    continue;
                result.Add((name, binding));
            }
            return result;
        }

        // Pre-compute (into `st.PromotableStrAccNames`) the set of string
        // accumulators THIS loop will promote, EXCLUDING names an enclosing loop
        // already promoted. Run before the iter-publish decision so
        // `CountRedirectableIterAccess` can treat `s = s + iter` as a
        // publish-eliding access (it lowers to StrAccAppendI). Transient — only
        // read during the owning loop's Step 1; each loop refreshes it.
        private static void PopulatePromotableStrAccNames(AstNode body, State st)
        {
            st.PromotableStrAccNames.Clear();
            foreach (var sa in CollectStringAccumulatorCandidates(body, st))
                if (!st.StringAccumulators.ContainsKey(sa.Name))
                    st.PromotableStrAccNames.Add(sa.Name);
        }

        // Is `value` a left-associative `+` chain whose LEFTMOST leaf is a
        // VariableAccess of `name`? Covers both the single `name + x` and the
        // chained `name + p1 + p2 + …` self-append. Once the leftmost operand
        // is the (string) accumulator, every `+` down the left spine is a
        // string concat, so each right-spine operand is appended in source
        // order. A non-`+` anywhere on the spine, or a leftmost leaf that is
        // not `name`, disqualifies it.
        private static bool IsStringAppendChainShape(AstNode? value, string name)
        {
            if (value is not Parser.Nodes.Operations.BinaryOperationNode bo) return false;
            var node = bo;
            while (true)
            {
                if (node.OpTok.Type != Lexer.Tokens.TokenType.PLUS) return false;
                if (node.LeftNode is Parser.Nodes.Variables.VariableAccessNode lvn)
                    return lvn.Name == name;
                if (node.LeftNode is Parser.Nodes.Operations.BinaryOperationNode inner) { node = inner; continue; }
                return false;
            }
        }

        // Ordered append parts of a `name = name + p1 + … + pn` chain (leftmost
        // leaf is `name`): the right-spine operands, in SOURCE order. Returns
        // null when `va` is not such a chain. When bindings are resolved the
        // leftmost leaf's binding must match `accBinding` (guards shadowing).
        private static List<AstNode>? GetStringAppendChainParts(
            Parser.Nodes.Variables.VariableAssignmentNode va, Pipeline.BindingId accBinding)
        {
            if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return null;
            if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return null;
            var parts = new List<AstNode>();
            var node = bo;
            while (true)
            {
                if (node.OpTok.Type != Lexer.Tokens.TokenType.PLUS) return null;
                parts.Add(node.RightNode);
                if (node.LeftNode is Parser.Nodes.Variables.VariableAccessNode lvn)
                {
                    if (lvn.Name != va.Name) return null;
                    if (lvn.Binding.IsResolved && accBinding.IsResolved && lvn.Binding != accBinding) return null;
                    parts.Reverse();
                    return parts;
                }
                if (node.LeftNode is Parser.Nodes.Operations.BinaryOperationNode inner) { node = inner; continue; }
                return null;
            }
        }

        // Emit the append of one chain part into StrAcc[accIdx]. A part that IS
        // the active typed iter goes through StrAccAppendI (typed slot, no
        // box); anything else is compiled to a temp and StrAccAppend'd (its
        // value is coerced to string exactly as StringValue.AddedTo would).
        private static void EmitStringAccumulatorPart(AstNode part, int accIdx, State st, ref byte topSlot)
        {
            if (part is VariableAccessNode rv && !string.IsNullOrEmpty(rv.Name)
                && st.ActiveTypedIters.TryGetValue(rv.Name, out byte iterSlot))
            {
                st.Code.Emit2(Opcode.StrAccAppendI, iterSlot, (ushort)accIdx);
                st.RedirectedTypedIterAccessCount++;
                return;
            }
            byte xs = AllocTemp(ref topSlot);
            CompileExpression(part, xs, st, ref topSlot);
            st.Code.Emit2(Opcode.StrAccAppend, xs, (ushort)accIdx);
        }

        // Is `va` a `name = name + … ` self-append (single or chained)?
        private static bool IsStringSelfAppend(Parser.Nodes.Variables.VariableAssignmentNode va, string name)
            => va.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ
               && va.Name == name
               && IsStringAppendChainShape(va.ValueNode, name);

        private static void GatherStringSelfAppendNames(AstNode? node, HashSet<string> outNames)
        {
            if (node == null) return;
            if (node is Parser.Nodes.Variables.VariableAssignmentNode va
                && va.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ
                && IsStringAppendChainShape(va.ValueNode, va.Name))
                outNames.Add(va.Name);
            foreach (var c in EnumerateChildStatements(node)) GatherStringSelfAppendNames(c, outNames);
        }

        // Single conservative walk counting, for `name`: total reads
        // (VariableAccess), total `=` writes, and self-appends. Unmodelled nodes
        // bump `reads` so the caller's `reads == appends` test fails (safe).
        private static void CountStringAppendStats(AstNode? node, string name,
            ref int reads, ref int writes, ref int appends)
        {
            if (node == null) return;
            switch (node)
            {
                case Parser.Nodes.Variables.VariableAccessNode va:
                    if (va.Name == name) reads++;
                    return;
                // Early-exit / no-op control-flow statements provably do not
                // read the accumulator, so they are known no-ops here (instead
                // of the conservative `default` read bump). This lets a loop
                // that builds a string and `break`s / `continue`s on a
                // condition still promote: `break` lands on the loop-exit
                // materialize target (partial accumulation preserved) and
                // `continue` skips to the iter-advance. (Previously these were
                // blocked to dodge a LICM jump-retarget miscompile on
                // `for v in lo..hi { if c { continue } … }`; that bug is now
                // fixed in LicmHoist's branch-target remap.) Pass is a no-op.
                case Parser.Nodes.Iterations.BreakNode:
                case Parser.Nodes.Iterations.ContinueNode:
                case Parser.Nodes.Operations.PassNode:
                    return;
                case NumberNode: case StringNode: case BooleanNode: case NullNode:
                    return;
                case Parser.Nodes.Operations.BinaryOperationNode bo:
                    CountStringAppendStats(bo.LeftNode, name, ref reads, ref writes, ref appends);
                    CountStringAppendStats(bo.RightNode, name, ref reads, ref writes, ref appends);
                    return;
                case Parser.Nodes.Operations.UnaryOperationNode uo:
                    CountStringAppendStats(uo.Node, name, ref reads, ref writes, ref appends);
                    return;
                case Parser.Nodes.Variables.VariableAssignmentNode vas:
                {
                    if (vas.Name == name && vas.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ)
                    {
                        writes++;
                        if (IsStringSelfAppend(vas, name)) appends++;
                    }
                    else if (vas.Name == name)
                    {
                        // compound op on the accumulator — not modelled.
                        writes++;
                    }
                    CountStringAppendStats(vas.ValueNode, name, ref reads, ref writes, ref appends);
                    return;
                }
                case Parser.Nodes.Special.ScopeNode sc:
                    foreach (var c in sc.Nodes) CountStringAppendStats(c, name, ref reads, ref writes, ref appends);
                    return;
                case Parser.Nodes.Statements.IfNode ifn:
                    foreach (var cs in ifn.Cases)
                    {
                        CountStringAppendStats(cs.Condition, name, ref reads, ref writes, ref appends);
                        CountStringAppendStats(cs.Expr, name, ref reads, ref writes, ref appends);
                    }
                    if (ifn.ElseCase.HasValue) CountStringAppendStats(ifn.ElseCase.Value.Expr, name, ref reads, ref writes, ref appends);
                    return;
                case Parser.Nodes.Functions.FunctionCallNode fc:
                    CountStringAppendStats(fc.NodeToCall, name, ref reads, ref writes, ref appends);
                    foreach (var a in fc.ArgNodes) CountStringAppendStats(a.Expr, name, ref reads, ref writes, ref appends);
                    return;
                default:
                    // Unmodelled — assume it may read `name`. Breaks reads==appends.
                    reads += 2;
                    return;
            }
        }

        // Direct child statements for the self-append-name gather (broad but
        // conservative: unhandled containers simply yield nothing).
        private static System.Collections.Generic.IEnumerable<AstNode> EnumerateChildStatements(AstNode node)
        {
            switch (node)
            {
                case Parser.Nodes.Special.ScopeNode sc:
                    foreach (var c in sc.Nodes) yield return c;
                    break;
                case Parser.Nodes.Statements.IfNode ifn:
                    foreach (var cs in ifn.Cases) { if (cs.Condition != null) yield return cs.Condition; if (cs.Expr != null) yield return cs.Expr; }
                    if (ifn.ElseCase.HasValue && ifn.ElseCase.Value.Expr != null) yield return ifn.ElseCase.Value.Expr;
                    break;
                case Parser.Nodes.Statements.ForNode f: if (f.BodyNode != null) yield return f.BodyNode; break;
                case Parser.Nodes.Statements.WhileNode w: if (w.BodyNode != null) yield return w.BodyNode; break;
                case Parser.Nodes.Statements.DoWhileNode d: if (d.BodyNode != null) yield return d.BodyNode; break;
                case Parser.Nodes.Statements.ForEachNode fe: if (fe.BodyNode != null) yield return fe.BodyNode; break;
            }
        }

        // M88: returns true iff `node` contains any kind of import /
        // namespace-using statement. Used by `LicmHoist` to gate the
        // closure-alias check: with imports active, callees may live
        // in modules whose `MutatedNames` set we cannot statically
        // inspect at compile time, so any in-loop call has to be
        // treated as a potential writer of every binding name.
        private static bool AstContainsImport(AstNode? node)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.ImportAll:
                case AstNodeType.ImportSelective:
                case AstNodeType.ImportAlias:
                case AstNodeType.UsingNamespace:
                    return true;
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) if (AstContainsImport(c)) return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (AstContainsImport(cs.Condition)) return true;
                        if (AstContainsImport(cs.Expr)) return true;
                    }
                    if (ifn.ElseCase.HasValue && AstContainsImport(ifn.ElseCase.Value.Expr)) return true;
                    return false;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    return AstContainsImport(wn.ConditionNode)
                        || AstContainsImport(wn.BodyNode);
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    return AstContainsImport(dw.ConditionNode)
                        || AstContainsImport(dw.BodyNode);
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    return AstContainsImport(fn.BodyNode);
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    return AstContainsImport(fe.BodyNode);
                }
                case AstNodeType.FunctionDefinition:
                {
                    var fdn = (Parser.Nodes.Functions.FunctionDefinitionNode)node;
                    return AstContainsImport(fdn.BodyNode);
                }
                case AstNodeType.Try:
                {
                    var tn = (Parser.Nodes.Special.TryNode)node;
                    if (AstContainsImport(tn.TryBody)) return true;
                    if (tn.CatchBody != null && AstContainsImport(tn.CatchBody)) return true;
                    if (tn.FinallyBody != null && AstContainsImport(tn.FinallyBody)) return true;
                    return false;
                }
                case AstNodeType.NamespaceDeclaration:
                {
                    var ns = (Parser.Nodes.Namespaces.NamespaceDeclarationNode)node;
                    return AstContainsImport(ns.Body);
                }
                default:
                    return false;
            }
        }

        // M88: walks the AST and builds a `name → MutatedNames`
        // dictionary covering every named function definition reachable
        // in this compilation unit (top-level + nested). LICM consults
        // this map to resolve `Call` opcodes whose function value
        // traces back to a `LoadGlobal` of a known name: if the
        // resolved callee's `MutatedNames` does NOT contain the
        // binding under consideration, the call is safe to hoist
        // past — regardless of whether the surrounding function's
        // own `MutatedNames` includes the name.
        //
        // Pre-computing the map up front (rather than lazily) keeps
        // LICM's per-Call check O(1) and avoids re-walking the AST.
        // Anonymous functions (no `VarNameTok`) and lambdas can't be
        // looked up by name and fall through to the conservative
        // path.
        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>
            CollectCalleeMutatedNames(AstNode? root)
        {
            var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(
                System.StringComparer.Ordinal);
            CollectCalleeMutatedNamesWalk(root, map);
            return map;
        }

        private static void CollectCalleeMutatedNamesWalk(
            AstNode? node,
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> map)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                {
                    var fdn = (Parser.Nodes.Functions.FunctionDefinitionNode)node;
                    string? fname = fdn.VarNameTok?.Value?.ToString();
                    if (!string.IsNullOrEmpty(fname) && !map.ContainsKey(fname))
                    {
                        map[fname!] = CollectMutatedNames(fdn.BodyNode);
                    }
                    CollectCalleeMutatedNamesWalk(fdn.BodyNode, map);
                    return;
                }
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectCalleeMutatedNamesWalk(c, map);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectCalleeMutatedNamesWalk(cs.Condition, map);
                        CollectCalleeMutatedNamesWalk(cs.Expr, map);
                    }
                    if (ifn.ElseCase.HasValue) CollectCalleeMutatedNamesWalk(ifn.ElseCase.Value.Expr, map);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectCalleeMutatedNamesWalk(wn.ConditionNode, map);
                    CollectCalleeMutatedNamesWalk(wn.BodyNode, map);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectCalleeMutatedNamesWalk(dw.ConditionNode, map);
                    CollectCalleeMutatedNamesWalk(dw.BodyNode, map);
                    return;
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    CollectCalleeMutatedNamesWalk(fn.BodyNode, map);
                    return;
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    CollectCalleeMutatedNamesWalk(fe.BodyNode, map);
                    return;
                }
                case AstNodeType.Try:
                {
                    var tn = (Parser.Nodes.Special.TryNode)node;
                    CollectCalleeMutatedNamesWalk(tn.TryBody, map);
                    if (tn.CatchBody != null) CollectCalleeMutatedNamesWalk(tn.CatchBody, map);
                    if (tn.FinallyBody != null) CollectCalleeMutatedNamesWalk(tn.FinallyBody, map);
                    return;
                }
                case AstNodeType.NamespaceDeclaration:
                {
                    var ns = (Parser.Nodes.Namespaces.NamespaceDeclarationNode)node;
                    CollectCalleeMutatedNamesWalk(ns.Body, map);
                    return;
                }
                default:
                    return;
            }
        }

        // M16: compile a user function body into a RaFunction. Mirrors the
        // CompileScript shape but uses the function's own frame id so slot
        // lowering admits parameters / locals declared inside the function,
        // not the enclosing script. The body is wrapped in a default
        // OP_RET_NULL terminator so functions that fall off the end implicitly
        // return null (matches AST visitor semantics).
        //
        // Failure mode: any IrCompileException raised mid-body propagates to
        // the caller, which lets FunctionValue cache "null" and keep
        // dispatching through the AST fallback. Once IR coverage extends to
        // every supported construct, that fallback path simply never runs.
        public static RaFunction CompileFunction(Parser.Nodes.Functions.FunctionDefinitionNode fnNode)
        {
            return CompileMethodShape(
                name: fnNode.VarNameTok?.Value?.ToString() ?? "<fn>",
                frameId: fnNode.FrameId,
                arity: fnNode.ArgNameToks.Count,
                paramBindings: fnNode.ParamBindings,
                argNameToks: fnNode.ArgNameToks,
                body: fnNode.BodyNode,
                shouldAutoReturn: fnNode.ShouldAutoReturn,
                reserveSelfSlot: fnNode.ReservesSelfSlot);
        }

        // M24: compile a single AstNode as an expression-shape RaFunction.
        // Terminator is `OP_HALT scratch` so the result lands in
        // RuntimeResult.Value (NOT FuncReturnValue). On compile failure the
        // wrapper emits OP_NATIVE_DEFINE so the dispatch loop still produces
        // a Value via the corresponding static Apply helper — no
        // `interpreter.Visit` fallback. Used by IrExpressionEvaluator for
        // sub-expressions invoked from runtime helpers and from visitor
        // static Apply chains.
        public static RaFunction CompileAsExpression(AstNode node, string name)
        {
            var fn = new RaFunction(name);
            fn.FrameId = -1;
            fn.Arity = 0;

            var st = new State();
            st.FrameId = -1;
            st.NumericInitBindings.UnionWith(CollectNumericInitBindingNames(node));
            st.StringInitBindings.UnionWith(CollectStringInitBindingNames(node));
            const byte ScratchSlot = 0;
            byte topSlot = 1;
            byte retSlot = AllocTemp(ref topSlot);

            int savedPc = st.Code.Pc;
            int savedAstRefs = st.AstRefs.Count;
            int savedScopeDepth = st.ScopeDepth;
            bool emitted = false;
            try
            {
                CompileExpression(node, retSlot, st, ref topSlot);
                if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;
                emitted = true;
            }
            catch (IrCompileException)
            {
                // Roll back partial emit and route via OP_NATIVE_DEFINE.
                st.Code.Truncate(savedPc);
                if (st.AstRefs.Count > savedAstRefs)
                    st.AstRefs.RemoveRange(savedAstRefs, st.AstRefs.Count - savedAstRefs);
                st.ScopeDepth = savedScopeDepth;
            }

            if (!emitted)
            {
                if (st.DefineRefs.Count > ushort.MaxValue)
                    throw new IrCompileException("DefineRefs overflow during CompileAsExpression");
                ushort refIdx = (ushort)st.DefineRefs.Count;
                st.DefineRefs.Add(node);
                st.Code.Emit2(Opcode.NativeDefine, retSlot, refIdx);
                if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;
            }

            st.Code.Emit3(Opcode.Halt, retSlot, 0, 0);
            FinalizeFn(fn, st);
            return fn;
        }

        // M24: compile an AstNode as a statement-shape RaFunction. Body
        // dispatch preserves FlowState (FuncReturnValue / Break / Continue
        // propagate through to the caller). Used by visitor.Apply helpers
        // that need to evaluate sub-statements while preserving control-flow
        // signals.
        public static RaFunction CompileAsStatement(AstNode node, string name)
        {
            var fn = new RaFunction(name);
            fn.FrameId = -1;
            fn.Arity = 0;

            var st = new State();
            st.FrameId = -1;
            st.NumericInitBindings.UnionWith(CollectNumericInitBindingNames(node));
            st.StringInitBindings.UnionWith(CollectStringInitBindingNames(node));
            const byte ScratchSlot = 0;

            CompileStatementWithFallback(node, st, ScratchSlot);

            // Trailing terminator: load null + halt (Value=null, no
            // FuncReturnValue set). Explicit `ret X` inside the body emits
            // OP_RET that short-circuits before reaching this terminator.
            st.Code.Emit3(Opcode.LoadNull, ScratchSlot, 0, 0);
            st.Code.Emit3(Opcode.Halt, ScratchSlot, 0, 0);

            FinalizeFn(fn, st);
            return fn;
        }

        // M18: generic IR compile entry shared by FunctionDefinitionNode,
        // StructMethodDefinitionNode, TraitMethodDefinitionNode, and
        // OperatorDefinitionNode. Each caller adapts its own field shape to
        // this signature.
        public static RaFunction CompileMethodShape(
            string name,
            int frameId,
            int arity,
            Pipeline.BindingId[]? paramBindings,
            IReadOnlyList<Lexer.Tokens.Token>? argNameToks,
            AstNode? body,
            bool shouldAutoReturn,
            bool reserveSelfSlot = false)
        {
            var fn = new RaFunction(name);
            fn.FrameId = frameId;
            fn.Arity = arity;
            // Collect every name potentially mutated inside this function
            // body. LICM consults `MutatedNames` to decide whether a
            // `LoadLocalS` whose binding is closure-captured from an
            // outer scope is safe to hoist out of an inner loop. If a
            // parameter name is in the set (rebound via assignment),
            // its LoadLocalS stays in-loop. Parameters that are never
            // reassigned remain hoist-eligible.
            fn.MutatedNames = CollectMutatedNames(body);
            fn.HasImports = AstContainsImport(body);
            fn.CalleeMutatedNames = CollectCalleeMutatedNames(body);
            // Parameters are bound at call-time, never reassigned by
            // the prologue — but a body that DOES reassign them is
            // already captured above. No extra work needed.

            var st = new State();
            st.FrameId = frameId;
            st.NumericInitBindings.UnionWith(CollectNumericInitBindingNames(body));
            st.StringInitBindings.UnionWith(CollectStringInitBindingNames(body));
            const byte ScratchSlot = 0;

            // Pre-register parameter slots so SlotCount accounts for them even
            // when the body never reads a parameter. The dispatch loop relies
            // on f.SlotLocals[paramSlot] being populated by the call-time
            // SymbolTable setup; FunctionValue.PrepareExecutionContextForCall
            // already runs SetLocal for each parameter, so the lazy-fallback
            // path will materialise the slot on first read.
            // PERF (direct-slot method dispatch): reserve frame slot 0 for
            // `self` so SlotCount accounts for it even on zero-arg / zero-local
            // methods (pure getters). The Resolver already reserved offset 0 in
            // the frame; this mirrors it into the IR slot table so the method
            // fast path can bind `self` into VmFrame.SlotLocals[0].
            if (reserveSelfSlot) st.RegisterSlot(0, "self");

            if (paramBindings != null)
            {
                for (int i = 0; i < paramBindings.Length; i++)
                {
                    var pb = paramBindings[i];
                    if (!pb.IsResolved || pb.FrameId != st.FrameId) continue;
                    string? pname = null;
                    if (argNameToks != null && i < argNameToks.Count)
                        pname = argNameToks[i].Value?.ToString();
                    st.RegisterSlot(pb.Offset, pname);
                }

                // PERF (direct-slot arg binding): cache each positional
                // parameter's frame slot. -1 = no stable slot (disqualifies the
                // fast path for the whole call). The call entry uses this to
                // write args straight into VmFrame.SlotLocals.
                var pslots = new int[paramBindings.Length];
                for (int i = 0; i < paramBindings.Length; i++)
                {
                    var pb = paramBindings[i];
                    pslots[i] = (pb.IsResolved && pb.FrameId == st.FrameId) ? pb.Offset : -1;
                }
                fn.ParamSlots = pslots;
            }

            // M17: arrow-form (ShouldAutoReturn) functions must return the
            // body expression's value. Compile body as an expression into a
            // dedicated slot and emit Ret instead of falling off into
            // RetNull. If the body turns out to be a statement the IR can't
            // express as a value (rare — Resolver lets `=>` accept any
            // statement, but the AST visitor's auto-return is only
            // meaningful for value-producing forms), fall back to the
            // block-form path so we don't lose the function semantically.
            if (shouldAutoReturn && body != null)
            {
                int savedPc = st.Code.Pc;
                int savedAstRefs = st.AstRefs.Count;
                int savedScopeDepth = st.ScopeDepth;
                byte retTopSlot = 1;
                byte retSlot = AllocTemp(ref retTopSlot);
                try
                {
                    // M28.3: tail-call detection on arrow-form auto-return.
                    // `fn x => other(x)` rewrites to OP_TAIL_CALL when the
                    // body is a natively-compilable positional FunctionCall.
                    if (body is FunctionCallNode fcArrow
                        && IsCallNativelyCompilable(fcArrow)
                        && fcArrow.ArgNodes.Count <= byte.MaxValue
                        && TryEmitTailCall(fcArrow, st, ref retTopSlot))
                    {
                        if (retTopSlot > st.MaxTempUsed) st.MaxTempUsed = retTopSlot;
                        FinalizeFn(fn, st);
                        return fn;
                    }
                    CompileExpression(body, retSlot, st, ref retTopSlot);
                    if (retTopSlot > st.MaxTempUsed) st.MaxTempUsed = retTopSlot;
                    st.Code.Emit3(Opcode.Ret, retSlot, 0, 0);
                    FinalizeFn(fn, st);
                    return fn;
                }
                catch (IrCompileException)
                {
                    // Roll back any partial emit before falling through to the
                    // block-form path (which uses CompileStatementWithFallback
                    // — strictly more permissive at the cost of losing the
                    // auto-return value).
                    st.Code.Truncate(savedPc);
                    if (st.AstRefs.Count > savedAstRefs)
                        st.AstRefs.RemoveRange(savedAstRefs, st.AstRefs.Count - savedAstRefs);
                    st.ScopeDepth = savedScopeDepth;
                }
            }

            if (body is ScopeNode sc)
            {
                foreach (var stmt in sc.Nodes)
                    CompileStatementWithFallback(stmt, st, ScratchSlot);
            }
            else if (body != null)
            {
                CompileStatementWithFallback(body, st, ScratchSlot);
            }

            // Default trailing terminator: load null into the scratch slot
            // and Halt. This produces RuntimeResult.Success(null) with NO
            // FuncReturnValue set — matching the AST visitor which returns
            // bodyRes.Value (null) without flagging FlowState.Return. A real
            // `ret` opcode would have set FuncReturnValue and short-circuited
            // before reaching this. Distinction matters for constructors:
            // explicit `ret X` from a constructor is an error, but falling
            // off the end is fine; OP_RET_NULL would incorrectly trip the
            // constructor check.
            st.Code.Emit3(Opcode.LoadNull, ScratchSlot, 0, 0);
            st.Code.Emit3(Opcode.Halt, ScratchSlot, 0, 0);

            FinalizeFn(fn, st);
            return fn;
        }

        private static void FinalizeFn(RaFunction fn, State st)
        {
            fn.Code = st.Code.ToArray();
            fn.Consts = st.Consts.ToArray();
            fn.Names = st.Names.ToArray();
            fn.AstRefs = st.AstRefs.ToArray();
            fn.CastRefs = st.CastRefs.ToArray();
            fn.MemberAccessRefs = st.MemberAccessRefs.ToArray();
            fn.MemberAssignRefs = st.MemberAssignRefs.ToArray();
            fn.ListAssignRefs = st.ListAssignRefs.ToArray();
            fn.EnumAccessRefs = st.EnumAccessRefs.ToArray();
            fn.TypeofRefs = st.TypeofRefs.ToArray();
            fn.NameofRefs = st.NameofRefs.ToArray();
            fn.DerefRefs = st.DerefRefs.ToArray();
            fn.SuperRefs = st.SuperRefs.ToArray();
            fn.FuncDefRefs = st.FuncDefRefs.ToArray();
            fn.DefineRefs = st.DefineRefs.ToArray();
            fn.TypeDefs = st.TypeDefs.ToArray();
            fn.EhTable = st.EhTable.ToArray();
            fn.LocalCount = st.MaxTempUsed;
            fn.StrAccCount = st.NextStrAcc;

            if (st.MaxSlot < 0)
            {
                fn.SlotCount = 0;
                fn.SlotNames = System.Array.Empty<string?>();
            }
            else
            {
                int count = st.MaxSlot + 1;
                var arr = new string?[count];
                var nameToSlot = new Dictionary<string, int>(st.SlotNames.Count);
                foreach (var kvp in st.SlotNames)
                {
                    arr[kvp.Key] = kvp.Value;
                    nameToSlot[kvp.Value] = kvp.Key;
                }
                fn.SlotCount = count;
                fn.SlotNames = arr;
                fn.NameToSlot = nameToSlot;
            }
            // M44: pin PC-span arrays onto the finalised function so the
            // VM dispatch loop can resolve real source positions for
            // runtime errors via binary search.
            if (st.PcSpanPcs.Count > 0)
            {
                fn.PcSpansPc = st.PcSpanPcs.ToArray();
                fn.PcSpansSpan = st.PcSpanSpans.ToArray();
            }
            BuildDeclSlotByAstRef(fn);
            // M23.1: allocate per-PC inline cache table sized to code length.
            // Slots stay zero-initialised until the first OP_LOAD_GLOBAL at
            // that PC resolves a SymbolEntry and writes the cache snapshot.
            if (fn.Code.Length > 0)
            {
                fn.LoadGlobalIc = new LoadGlobalIcSlot[fn.Code.Length];
                // M27.3 / M28: per-PC IC backing arrays. Each table is gated
                // on whether the corresponding opcode actually appears in
                // Code. Skips a zero-initialised Code.Length array for
                // arithmetic-only / numeric-only scripts that never emit
                // GetMember, Cast, EnumAccess, or Call. Sized to Code.Length
                // so PC indexing remains bounds-free in the hot path.
                bool needEnumIc = false, needCastIc = false, needMemberIc = false, needCallIc = false;
                for (int ip = 0; ip < fn.Code.Length; ip++)
                {
                    var op = Encoding.DecodeOp(fn.Code[ip]);
                    switch (op)
                    {
                        case Opcode.EnumAccess: needEnumIc = true; break;
                        case Opcode.Cast: needCastIc = true; break;
                        case Opcode.GetMember: needMemberIc = true; break;
                        case Opcode.Call:
                        case Opcode.TailCall: needCallIc = true; break;
                    }
                }
                if (needEnumIc)   fn.EnumAccessIc   = new EnumAccessIcSlot[fn.Code.Length];
                if (needCastIc)   fn.CastIc         = new CastIcSlot[fn.Code.Length];
                if (needMemberIc) fn.MemberAccessIc = new MemberAccessIcSlot[fn.Code.Length];
                if (needCallIc)   fn.CallMethodIc   = new CallMethodIcSlot[fn.Code.Length];
                // M40: infer per-slot type hints via a single forward
                // pass over the opcode stream. Cheap (linear in Code.Length)
                // and the result lives on the RaFunction for the future
                // tier-up compiler.
                InferSlotTypes(fn);
                // M45: rewrite Add/Sub/Mul → AddNN/SubNN/MulNN when both
                // operand slots are statically proven Number by the M40
                // lattice. Runs in-place; preserves all other opcode
                // semantics. Saves 2 type-tag checks + 2 null-checks per
                // arith op on hot loops.
                SpecializeNumericOps(fn);
                // M64: build the CFG / Dominator / SSA / SCCP / GVN /
                // LICM / DCE bundle, then apply `IrRewriter` to rewrite
                // the linear Code[] in place. All rewrites are 1:1
                // opcode substitutions — PC layout, branch offsets,
                // EhTable, PcSpans, and every IC table stay valid.
                // Bundle stays attached to the function for diagnostic
                // dumps (`--dump-cfg`).
                try
                {
                    fn.Analysis = Analysis.IrAnalysisBundle.Build(fn);
                    Analysis.IrRewriter.Apply(fn, fn.Analysis);
                    // M45 specialisation may now apply to ops the
                    // rewriter changed (e.g. an Add folded to LoadConst
                    // doesn't need the AddNN path). Re-run cheaply.
                    SpecializeNumericOps(fn);
                    // Physical LICM hoist: constant loads marked
                    // loop-invariant by `LoopAnalysis` are moved out of
                    // the loop body to the preheader. Rewrites Code[],
                    // EhTable PCs, PcSpansPc, and every per-PC IC table
                    // so PC-relative branches and metadata stay
                    // consistent. Sites where the hoist would overflow
                    // jmp_imm16 (≥ 32K body) silently no-op; the
                    // dispatch loop runs the un-hoisted code in that
                    // case.
                    if (Analysis.LicmHoist.Apply(fn, fn.Analysis) > 0)
                    {
                        // Code[] reshuffled — the cached Analysis
                        // bundle (CFG/SSA/Loops/SCCP/GVN) now references
                        // stale PCs. Re-run InferSlotTypes against the
                        // new layout so SpecializeNumericOps below sees
                        // accurate hints if it picks up further AddNN
                        // promotions.
                        InferSlotTypes(fn);
                        SpecializeNumericOps(fn);
                        // M-tier1 (post-M77): rebuild the bundle and
                        // re-run DCE. Multi-pass LICM may pull an entire
                        // dependence chain (LoadLocalS → Add → Mul) into
                        // the preheader. If the original in-loop writer
                        // for one of those intermediate slots was the
                        // ONLY use of an earlier def, that def is now
                        // dead. The first DCE pass (before LICM)
                        // couldn't see this because the chain was still
                        // in-loop. Rerun DCE on the fresh SSA so any
                        // newly-dead pure ops collapse to Pass.
                        try
                        {
                            var freshBundle = Analysis.IrAnalysisBundle.Build(fn);
                            int erased = 0;
                            foreach (var pc in freshBundle.Opt.DeadDefPcs)
                            {
                                if (pc < 0 || pc >= fn.Code.Length) continue;
                                uint instr = fn.Code[pc];
                                var op = Encoding.DecodeOp(instr);
                                if (op == Opcode.Pass) continue;
                                if (!IsLicmDcePureEraseable(op)) continue;
                                fn.Code[pc] = Encoding.Pack3(Opcode.Pass, 0, 0, 0);
                                erased++;
                            }
                            // Drop the bundle either way — keep nulled
                            // semantics consistent with the
                            // post-rewrite invariant.
                            fn.Analysis = null;
                        }
                        catch
                        {
                            fn.Analysis = null;
                        }
                    }
                }
                catch
                {
                    // Defensive: analysis failure must not break the
                    // compile. Drop the bundle; the function still runs
                    // through the un-rewritten dispatch loop.
                    fn.Analysis = null;
                }

                // M90: fused compare-and-branch — the FINAL code transform.
                // MUST run after LICM (above), which physically reshuffles
                // Code[] and patches only the standard imm16-encoded branches
                // (Jmp/JmpIf/JmpIfNot) — never the fused ops' sbyte-encoded
                // offset. Running it here, with nothing moving code after,
                // keeps the baked offsets valid. Builds its own fresh
                // CFG+SSA bundle on the final layout. Best-effort: a failure
                // leaves the function correct in its unfused form.
                try { Analysis.IrRewriter.FuseCompareBranches(fn); }
                catch { }

                // M91: Pass compaction — the absolute FINAL code transform.
                // Physically removes every Pass (SCCP / DCE / branch-fold
                // leftovers plus the JmpIfNot→Pass that M90 fusion creates)
                // and repatches all relative jumps, EH ranges, per-PC IC
                // arrays, and the PcSpans source map against the shorter
                // stream. MUST be last — nothing may move code after it.
                // Best-effort: bails internally (no-op) on any opcode whose
                // PC encoding it can't safely remap, leaving the function
                // correct in its un-compacted form.
                try { Analysis.PassCompactor.Compact(fn); }
                catch { }
            }
        }

        // M40: single-pass type-hint inference. Walk the opcode stream
        // forwards; on each write to a local slot, record the inferred
        // RuntimeValueType. Subsequent writes to the same slot collapse
        // the hint to RuntimeValueType.Null (top of the lattice) when
        // the new type disagrees. The result is a coarse "this slot
        // holds T at every reachable PC" predicate the JIT can rely on
        // for type-specialised codegen.
        //
        // Conservative: any slot we don't recognise is left at
        // RuntimeValueType.Null. This is correct (the JIT will fall
        // back to the boxed dispatch path) at the cost of missed
        // specialisation opportunities. Refinement to SSA-based
        // per-PC inference is a separate milestone.
        // Mirrors IrRewriter.IsErasableForDce + adds the typed-II family,
        // type bridges (UnboxI/BoxI/UnboxF/BoxF), and FF/BB ops. Used by
        // the post-LICM DCE re-run to nuke any pure opcode whose result
        // becomes unused after the multi-pass hoist (e.g. an
        // intermediate Add that fed a Mul that got hoisted, leaving the
        // pre-hoist Add as a dead def).
        //
        // LoadLocalS is INTENTIONALLY excluded — its side effect is the
        // borrow / move check raised when the SymbolEntry is in an
        // illegal state. Erasing a LoadLocalS would silence
        // "use-after-move" diagnostics.
        private static bool IsLicmDcePureEraseable(Opcode op)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.LoadIntS64:
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                case Opcode.Neg: case Opcode.Not: case Opcode.BNot:
                case Opcode.NegI: case Opcode.NegF:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                case Opcode.LtII: case Opcode.LeII: case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                case Opcode.LtFF: case Opcode.LeFF: case Opcode.GtFF: case Opcode.GeFF:
                case Opcode.UnboxI: case Opcode.BoxI:
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.Move:
                case Opcode.Alias:
                    return true;
                default:
                    return false;
            }
        }

        private static void InferSlotTypes(RaFunction fn)
        {
            int total = fn.LocalCount;
            if (total <= 0) return;
            var hints = new Values.RuntimeValueType[total];
            // Initialise to "unknown" / top. RuntimeValueType.Null is
            // overloaded as the sentinel "no inferred type" since it
            // already encodes the null-valued case and gets overwritten
            // by the first concrete observation.
            for (int i = 0; i < hints.Length; i++) hints[i] = Values.RuntimeValueType.Null;
            bool[] seen = new bool[total];

            for (int pc = 0; pc < fn.Code.Length; pc++)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                byte a = Encoding.A(instr);
                ushort imm = Encoding.Imm16(instr);
                Values.RuntimeValueType inferred = Values.RuntimeValueType.Null;
                bool writes = false;
                switch (op)
                {
                    case Opcode.LoadConst:
                        if (imm < fn.Consts.Length && fn.Consts[imm] != null)
                            inferred = fn.Consts[imm]!.Type;
                        writes = true;
                        break;
                    case Opcode.LoadNull: inferred = Values.RuntimeValueType.Null; writes = true; break;
                    case Opcode.LoadTrue:
                    case Opcode.LoadFalse: inferred = Values.RuntimeValueType.Boolean; writes = true; break;
                    case Opcode.LoadIntS: inferred = Values.RuntimeValueType.Number; writes = true; break;
                    // Arithmetic results stay in the Number lattice when
                    // both inputs are numeric. Without per-PC SSA we
                    // conservatively assume Number for the unboxed
                    // int64 fast path; the slow path may yield a
                    // different concrete type but the JIT's guarded
                    // specialisation can branch on it.
                    case Opcode.Add:
                    case Opcode.Sub:
                    case Opcode.Mul:
                    case Opcode.Div:
                    case Opcode.Mod:
                    case Opcode.Pow:
                    case Opcode.Shl:
                    case Opcode.Shr:
                    case Opcode.Ushr:
                    case Opcode.Rol:
                    case Opcode.Ror:
                    case Opcode.BAnd:
                    case Opcode.BOr:
                    case Opcode.BXor:
                    case Opcode.Neg:
                    case Opcode.BNot:
                        inferred = Values.RuntimeValueType.Number; writes = true; break;
                    case Opcode.Not:
                    case Opcode.Eq:
                    case Opcode.Ne:
                    case Opcode.SEq:
                    case Opcode.SNe:
                    case Opcode.Lt:
                    case Opcode.Le:
                    case Opcode.Gt:
                    case Opcode.Ge:
                        inferred = Values.RuntimeValueType.Boolean; writes = true; break;
                    case Opcode.NewList:
                        inferred = Values.RuntimeValueType.List; writes = true; break;
                    case Opcode.NewMap:
                        inferred = Values.RuntimeValueType.Map; writes = true; break;
                    case Opcode.NewSet:
                        inferred = Values.RuntimeValueType.Set; writes = true; break;
                    case Opcode.NewTuple:
                        inferred = Values.RuntimeValueType.Tuple; writes = true; break;
                    case Opcode.StrConcat:
                    case Opcode.Interp:
                    case Opcode.Fmt:
                        inferred = Values.RuntimeValueType.String; writes = true; break;
                    // Writers we cannot statically type. Mark as a write so
                    // any pre-existing hint on this slot is killed (joined
                    // to top = Null), preventing M45's
                    // SpecializeNumericOps from picking a stale hint for a
                    // slot now holding a non-Number value (e.g. an IntegerValue
                    // returned via LoadLocalS from a typed `var a: int`
                    // binding that the constructor coerced post-LoadConst).
                    case Opcode.LoadLocalS:
                    case Opcode.LoadGlobal:
                    case Opcode.LoadBuiltin:
                    case Opcode.LoadUpval:
                    case Opcode.Move:
                    case Opcode.Alias:
                    case Opcode.MoveLet:
                    case Opcode.Borrow:
                    case Opcode.Deref:
                    case Opcode.Range:
                    case Opcode.ListGet:
                    case Opcode.MapGet:
                    case Opcode.GetMember:
                    case Opcode.EnumAccess:
                    case Opcode.ForEachIterable:
                    case Opcode.ListLen:
                    case Opcode.Cast:
                    case Opcode.Closure:
                    case Opcode.Call:
                    case Opcode.CallKw:
                    case Opcode.CallMethod:
                    case Opcode.NewInstance:
                    case Opcode.With:
                    case Opcode.GetSelf:
                    case Opcode.GetSuper:
                    case Opcode.Typeof:
                    case Opcode.Nameof:
                    case Opcode.DefineFunction:
                    case Opcode.NativeDefine:
                    case Opcode.Await:
                    case Opcode.Spawn:
                    case Opcode.NullCoal:
                        inferred = Values.RuntimeValueType.Null; writes = true; break;
                }
                if (writes && a < total)
                {
                    if (!seen[a])
                    {
                        hints[a] = inferred;
                        seen[a] = true;
                    }
                    else if (hints[a] != inferred)
                    {
                        // Conflicting writes → top of lattice.
                        hints[a] = Values.RuntimeValueType.Null;
                    }
                }
            }
            fn.SlotTypeHints = hints;
        }

        // M45: rewrite Add/Sub/Mul into their type-specialised variants
        // (AddNN/SubNN/MulNN) when the static SlotTypeHints lattice proves
        // both source operands hold RuntimeValueType.Number at every
        // reachable PC. Preserves the 3-byte ABC encoding — only the
        // opcode byte changes.
        private static void SpecializeNumericOps(RaFunction fn)
        {
            var hints = fn.SlotTypeHints;
            if (hints == null) return;
            for (int pc = 0; pc < fn.Code.Length; pc++)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                Opcode? spec = op switch
                {
                    Opcode.Add => Opcode.AddNN,
                    Opcode.Sub => Opcode.SubNN,
                    Opcode.Mul => Opcode.MulNN,
                    _ => null,
                };
                if (spec == null) continue;
                byte b = Encoding.B(instr);
                byte c = Encoding.C(instr);
                if (b >= hints.Length || c >= hints.Length) continue;
                if (hints[b] != Values.RuntimeValueType.Number) continue;
                if (hints[c] != Values.RuntimeValueType.Number) continue;
                // Swap only the opcode byte. ABC operands unchanged.
                fn.Code[pc] = (instr & 0xFFFFFF00u) | (uint)spec.Value;
            }
        }

        private static void BuildDeclSlotByAstRef(RaFunction fn)
        {
            var arr = new int[fn.AstRefs.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = -1;
                if (fn.AstRefs[i] is VariableDeclarationNode vd
                    && vd.Bindings != null
                    && vd.Bindings.Length > 0
                    && vd.Bindings[0].IsResolved
                    && vd.Bindings[0].FrameId == fn.FrameId)
                {
                    arr[i] = vd.Bindings[0].Offset;
                }
            }
            fn.DeclSlotByAstRef = arr;
        }

        // Slot eligibility predicate for hot read/write lowering. Kept tight:
        // only frame-0 (script-frame) bindings of kinds that name a real
        // slot at runtime. Captured / Builtin / Unresolved fall back to
        // OP_LOAD_GLOBAL by-name.
        private static bool IsSlotEligible(BindingId b, BindingKind k, State st)
        {
            if (!b.IsResolved) return false;
            if (b.FrameId != st.FrameId) return false;
            return k == BindingKind.Local
                || k == BindingKind.Global
                || k == BindingKind.Parameter
                || k == BindingKind.SelfRef;
        }

        // M27.2 — Peephole: collapse `slot = slot + <safe-rhs>` (and `-`) into
        // a single AddIntoSlot/SubIntoSlot. The fused opcode reads the slot's
        // current value at execution time, so RHS sub-trees that could mutate
        // the same slot or otherwise observe ordering (function calls, nested
        // assignments) are forbidden. Caller has already verified the target
        // binding is slot-eligible.
        private static bool TryEmitSelfAdditiveSlot(VariableAssignmentNode va, State st, ref byte topSlot)
        {
            if (va.ValueNode is not BinaryOperationNode bo) return false;
            var opType = bo.OpTok.Type;
            if (opType != TokenType.PLUS && opType != TokenType.MINUS) return false;

            // String accumulator append: `s = s + p1 + … + pn` (single or
            // chained, leftmost leaf is the PROMOTED accumulator). Each part is
            // appended to the per-frame StringBuilder (amortised O(1)) instead
            // of the boxed O(n) reallocating concat; `s`'s SymbolEntry is
            // refreshed once at loop exit. Runs FIRST — before the bare
            // `bo.LeftNode is VariableAccessNode` requirement (a chain's
            // top-level LHS is a nested BinaryOp) and before IsSafeRhsForSelfFuse
            // (the append evaluates the part's VALUE; unlike the numeric fused
            // slot-read it tolerates function-call / side-effecting parts, which
            // the gate already permits — so this is also where `s = s + f()`
            // gets its append, previously dropped).
            if (opType == TokenType.PLUS
                && st.StringAccumulators.TryGetValue(va.Name, out int strAccIdx))
            {
                var parts = GetStringAppendChainParts(va, va.Binding);
                if (parts != null)
                {
                    foreach (var part in parts)
                        EmitStringAccumulatorPart(part, strAccIdx, st, ref topSlot);
                    return true;
                }
            }

            if (bo.LeftNode is not VariableAccessNode lvn) return false;
            // Self-additive: LHS of the binary op must reference the same
            // resolved binding as the assignment target. BindingId comparison
            // is sufficient — frame_id+offset uniquely identifies a slot.
            if (!lvn.Binding.IsResolved) return false;
            if (lvn.Binding != va.Binding) return false;
            if (lvn.BindingKind != va.BindingKind) return false;
            if (!IsSlotEligible(lvn.Binding, lvn.BindingKind, st)) return false;
            if (!IsSafeRhsForSelfFuse(bo.RightNode)) return false;

            // --- Typed-accumulator paths run FIRST so they win over the
            // boxed AddIntoSlotImm / AddIntoSlot variants. The accumulator's
            // typed Int64 slot was set up by `CompileForLazyLong`'s pre-
            // loop UnboxI and boxed back via BoxI on loop exit. Body
            // operations on it incur ZERO per-iter alloc.

            // Path 1: acc + typed iter. Pure AddII typed-typed-typed.
            if (bo.RightNode is VariableAccessNode rvn0
                && !string.IsNullOrEmpty(rvn0.Name)
                && st.TypedAccumulators.TryGetValue(va.Name, out var accTyped)
                && st.ActiveTypedIters.TryGetValue(rvn0.Name, out byte typedIterSlot0))
            {
                var opAA = opType == TokenType.PLUS ? Opcode.AddII : Opcode.SubII;
                st.Code.Emit3(opAA, accTyped.LongSlot, accTyped.LongSlot, typedIterSlot0);
                st.RedirectedTypedIterAccessCount++;
                st.DirtyTypedAccs.Add(va.Name);
                return true;
            }

            // Path 1b (M87): acc + another typed accumulator. Pattern
            // `b = b + a` where both `a` and `b` are typed accumulators
            // (e.g. running sums that feed each other across iterations).
            // Reads the RHS acc's typed long slot directly — no boxed
            // mirror touch, no per-iter alloc. The compile-time dirty
            // flag for the RHS acc is irrelevant here: typed II reads
            // always observe the current typed-slot value.
            if (bo.RightNode is VariableAccessNode rvn1b
                && !string.IsNullOrEmpty(rvn1b.Name)
                && st.TypedAccumulators.TryGetValue(va.Name, out var accTyped1b)
                && rvn1b.Name != va.Name
                && st.TypedAccumulators.TryGetValue(rvn1b.Name, out var rhsAccTyped))
            {
                var opAA1b = opType == TokenType.PLUS ? Opcode.AddII : Opcode.SubII;
                st.Code.Emit3(opAA1b, accTyped1b.LongSlot, accTyped1b.LongSlot, rhsAccTyped.LongSlot);
                st.DirtyTypedAccs.Add(va.Name);
                return true;
            }

            // Path 1c (M87): acc + typed-long-binding. Pattern
            // `acc = acc + cap` where `cap` is a never-mutated local
            // pre-loaded into a typed Int64 slot via the typed-long
            // binding pre-load. Avoids the boxed read of `cap` each iter.
            if (bo.RightNode is VariableAccessNode rvn1c
                && !string.IsNullOrEmpty(rvn1c.Name)
                && st.TypedAccumulators.TryGetValue(va.Name, out var accTyped1c)
                && st.TypedLongBindings.TryGetValue(rvn1c.Name, out var rhsTypedBnd))
            {
                var opAA1c = opType == TokenType.PLUS ? Opcode.AddII : Opcode.SubII;
                st.Code.Emit3(opAA1c, accTyped1c.LongSlot, accTyped1c.LongSlot, rhsTypedBnd.LongSlot);
                st.DirtyTypedAccs.Add(va.Name);
                return true;
            }

            // Path 2: acc + literal int64. RHS literal was pre-loaded into
            // `TypedAccumulatorLiterals[literalValue]` by `CompileForLazy-
            // Long`. Emit a pure `AddII / SubII`. This is the `counter =
            // counter + 1` shape that previously paid `AddIntoSlotImm`
            // per iter (NumberValue alloc post-8192).
            if (st.TypedAccumulators.TryGetValue(va.Name, out var accTypedLit)
                && TryGetLiteralLongFromConstExpr(bo.RightNode, out long litValue)
                && st.TypedAccumulatorLiterals.TryGetValue(litValue, out byte litRhsSlot))
            {
                var opAA2 = opType == TokenType.PLUS ? Opcode.AddII : Opcode.SubII;
                st.Code.Emit3(opAA2, accTypedLit.LongSlot, accTypedLit.LongSlot, litRhsSlot);
                st.DirtyTypedAccs.Add(va.Name);
                return true;
            }

            // Path 2b: acc + loop-invariant pure expression. The RHS
            // AstNode was pre-compiled into a typed Int64 slot by
            // `CompileForLazyLong` and registered in
            // `TypedAccumulatorExprs`. Emit a pure `AddII / SubII`
            // reading the cached typed slot — zero per-iter alloc, no
            // const-pool dispatch, no boxed mirror touch.
            if (st.TypedAccumulators.TryGetValue(va.Name, out var accTypedExpr)
                && st.TypedAccumulatorExprs.TryGetValue(bo.RightNode, out byte exprRhsSlot))
            {
                var opAA3 = opType == TokenType.PLUS ? Opcode.AddII : Opcode.SubII;
                st.Code.Emit3(opAA3, accTypedExpr.LongSlot, accTypedExpr.LongSlot, exprRhsSlot);
                st.DirtyTypedAccs.Add(va.Name);
                return true;
            }

            // M27.5: when the slot fits in a u8 AND the RHS is a constant
            // expression that folds to an int16-range integer, emit the
            // immediate variant. Skips both the LoadConst dispatch and the
            // temp-slot consumption for the RHS.
            if (va.Binding.Offset <= byte.MaxValue
                && TryConstEvalNumber(bo.RightNode, out var rhsConst)
                && TryGetInt16FromNumberValue(rhsConst, out short rhsImm))
            {
                st.RegisterSlot(va.Binding.Offset, va.Name);
                var immOp = opType == TokenType.PLUS ? Opcode.AddIntoSlotImm : Opcode.SubIntoSlotImm;
                st.Code.Emit2(immOp, (byte)va.Binding.Offset, unchecked((ushort)rhsImm));
                return true;
            }

            if (bo.RightNode is VariableAccessNode rvn
                && !string.IsNullOrEmpty(rvn.Name)
                && st.ActiveTypedIters.TryGetValue(rvn.Name, out byte typedIterSlot)
                && va.Binding.Offset <= ushort.MaxValue)
            {
                st.RegisterSlot(va.Binding.Offset, va.Name);
                var opI = opType == TokenType.PLUS ? Opcode.AddIntoSlotI : Opcode.SubIntoSlotI;
                st.Code.Emit2(opI, typedIterSlot, (ushort)va.Binding.Offset);
                st.RedirectedTypedIterAccessCount++;
                return true;
            }

            byte rhsSlot = AllocTemp(ref topSlot);
            CompileExpression(bo.RightNode, rhsSlot, st, ref topSlot);
            st.RegisterSlot(va.Binding.Offset, va.Name);
            var op = opType == TokenType.PLUS ? Opcode.AddIntoSlot : Opcode.SubIntoSlot;
            st.Code.Emit2(op, rhsSlot, (ushort)va.Binding.Offset);
            return true;
        }

        // Narrow a foldable constant down to an int16 immediate, if it sits in
        // [-32768..32767] and has scale 0. Used by M27.5 to gate emission of
        // the inline-immediate AddIntoSlotImm/SubIntoSlotImm variants.
        private static bool TryGetInt16FromNumberValue(RuntimeValue value, out short result)
        {
            result = 0;
            if (value is not NumberValue nv) return false;
            var bn = nv.Value;
            if (!bn.Scale.IsZero) return false;
            var u = bn.Unscaled;
            if (u < short.MinValue || u > short.MaxValue) return false;
            result = (short)(int)u;
            return true;
        }

        // Returns true iff compiling `node` cannot mutate the surrounding
        // frame / produce a side effect observable by the fused opcode. The
        // whitelist matches the dominant `+= literal` / `+= other_local`
        // patterns at loop-counter sites without dragging in nested writes,
        // function-call dispatches, member assignments, etc.
        private static bool IsSafeRhsForSelfFuse(AstNode node)
        {
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                case AstNodeType.String:
                case AstNodeType.Null:
                case AstNodeType.VariableAccess:
                    return true;
                case AstNodeType.UnaryOperation:
                {
                    var un = (UnaryOperationNode)node;
                    return IsSafeRhsForSelfFuse(un.Node);
                }
                case AstNodeType.BinaryOperation:
                {
                    var inner = (BinaryOperationNode)node;
                    // Allow constant arithmetic sub-trees so `i = i + (1 + 2)`
                    // still folds; constant folding earlier may already have
                    // collapsed it, but be defensive.
                    return IsSafeRhsForSelfFuse(inner.LeftNode)
                        && IsSafeRhsForSelfFuse(inner.RightNode);
                }
                default:
                    return false;
            }
        }

        private static void CompileStatementWithFallback(AstNode stmt, State st, byte scratchSlot)
        {
            int savedPc = st.Code.Pc;
            int savedAstRefs = st.AstRefs.Count;
            int savedScopeDepth = st.ScopeDepth;
            byte topSlot = 1;

            try
            {
                // L0 parity oracle: when this kind is force-flagged, skip the
                // native path so the statement rolls back to OP_NATIVE_DEFINE.
                // Nothing was emitted yet, so the rollback below is a no-op.
                if (!IsForcedFallback(stmt.NodeType)
                    && TryCompileStatement(stmt, st, ref topSlot, scratchSlot, strict: false))
                {
                    if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;
                    return;
                }
            }
            catch (IrCompileException)
            {
                // intentional: fall through and fallback
            }

            // rollback tentative state
            st.Code.Truncate(savedPc);
            if (st.AstRefs.Count > savedAstRefs)
                st.AstRefs.RemoveRange(savedAstRefs, st.AstRefs.Count - savedAstRefs);
            st.ScopeDepth = savedScopeDepth;
            EmitFallback(stmt, st, scratchSlot);
        }

        // Rollback emits OP_NATIVE_DEFINE for every node kind that has a
        // registered Apply dispatch in VmExecutor. Anything else is a hard
        // failure: there is no AST-walker fallback any more.
        private static void EmitFallback(AstNode node, State st, byte scratchSlot)
        {
            if (!HasNativeDefineRoute(node.NodeType))
                throw new IrCompileException(
                    $"no NATIVE_DEFINE route for node {node.NodeType}; wire VmExecutor.OP_NATIVE_DEFINE first");
            if (st.DefineRefs.Count > ushort.MaxValue)
                throw new IrCompileException("DefineRefs overflow (>65535)");
            ushort refIdx = (ushort)st.DefineRefs.Count;
            st.DefineRefs.Add(node);
            st.Code.Emit2(Opcode.NativeDefine, scratchSlot, refIdx);
        }

        // Mirrors VmExecutor's OP_NATIVE_DEFINE switch. Keep in sync when
        // adding a new dispatch case there.
        private static bool HasNativeDefineRoute(AstNodeType t)
        {
            switch (t)
            {
                case AstNodeType.ExtensionDefinition:
                case AstNodeType.TraitDefinition:
                case AstNodeType.StructDefinition:
                case AstNodeType.RecordDefinition:
                case AstNodeType.InterfaceDefinition:
                case AstNodeType.EnumDefinition:
                case AstNodeType.UsingNamespace:
                case AstNodeType.ClassDefinition:
                case AstNodeType.AnnotationDefinition:
                case AstNodeType.DelegateDefinition:
                case AstNodeType.NamespaceDeclaration:
                case AstNodeType.ImportAll:
                case AstNodeType.ImportSelective:
                case AstNodeType.ImportAlias:
                case AstNodeType.Match:
                case AstNodeType.DestructuringDeclaration:
                case AstNodeType.TryUnwrap:
                case AstNodeType.Await:
                case AstNodeType.Spawn:
                case AstNodeType.Emit:
                case AstNodeType.ForAwait:
                case AstNodeType.Pipeline:
                case AstNodeType.Borrow:
                case AstNodeType.DereferenceAssignment:
                case AstNodeType.Goto:
                case AstNodeType.Label:
                case AstNodeType.SuperFor:
                case AstNodeType.AsmBlock:
                case AstNodeType.RegexLiteral:
                case AstNodeType.FormattedInterpolation:
                case AstNodeType.Yield:
                case AstNodeType.AnnotationApplication:
                case AstNodeType.Switch:
                case AstNodeType.Try:
                case AstNodeType.Scope:
                case AstNodeType.If:
                case AstNodeType.VariableDeclaration:
                case AstNodeType.VariableAssignment:
                case AstNodeType.Break:
                case AstNodeType.Continue:
                case AstNodeType.Pass:
                case AstNodeType.Return:
                case AstNodeType.Throw:
                case AstNodeType.Retry:
                case AstNodeType.BinaryOperation:
                case AstNodeType.UnaryOperation:
                case AstNodeType.List:
                case AstNodeType.Set:
                case AstNodeType.Tuple:
                case AstNodeType.Map:
                case AstNodeType.FunctionCall:
                case AstNodeType.VariableDelete:
                case AstNodeType.MemberAssignment:
                case AstNodeType.ListAssignment:
                case AstNodeType.WithExpression:
                    return true;
                default:
                    return false;
            }
        }

        // L5: build a FLAT EnumDef from an enum declaration, or return false to
        // fall back to the visitor. Lowerable iff: no annotations, no where-
        // constraints, every variant name is non-empty + unique, and every
        // explicit value is a plain integer literal (matched + folded the same
        // way the visitor evaluates it). Auto-increment mirrors the visitor's
        // `lastValue + 1`. Payload tuple types + generic param names are already
        // flat. Anything else → false (the visitor handles value side effects,
        // exotic numeric types, expression values, etc., with full diagnostics).
        private static bool TryBuildEnumDef(Parser.Nodes.Enums.EnumDefinitionNode node, out Defs.EnumDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;

            string enumName = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(enumName)) return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var variants = new Defs.EnumVariantDef[node.Variants.Count];
            System.Int128 lastValue = -1;

            for (int i = 0; i < node.Variants.Count; i++)
            {
                var spec = node.Variants[i];
                string memberName = spec.Name;
                if (string.IsNullOrWhiteSpace(memberName)) return false;
                if (!seen.Add(memberName)) return false;

                System.Int128 value;
                if (spec.ValueNode == null)
                {
                    value = lastValue + 1;
                }
                else if (spec.ValueNode is NumberNode nn)
                {
                    RuntimeValue parsed;
                    try { parsed = Visitors.Primitives.NumberNodeVisitor.ParseLiteral(nn); }
                    catch { return false; }
                    if (!Runtime.EnumDefOps.TryExtractInt128(parsed, out value)) return false;
                }
                else
                {
                    return false; // non-constant value expression → fallback
                }
                lastValue = value;

                var payloads = (spec.PayloadTypes == null || spec.PayloadTypes.Count == 0)
                    ? System.Array.Empty<Types.TypeDescriptor>()
                    : spec.PayloadTypes.ToArray();
                variants[i] = new Defs.EnumVariantDef(memberName, i, value, payloads);
            }

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.EnumDef(enumName, generics, variants);
            return true;
        }

        // L5: build a FLAT DelegateDef, or return false to fall back. Lowerable
        // iff the name is non-empty and there are no where-constraints (those
        // carry AST). The structural signature + generic param names + the
        // public flag are already flat.
        private static bool TryBuildDelegateDef(Parser.Nodes.Functions.DelegateDefinitionNode node, out Defs.DelegateDef def)
        {
            def = null!;
            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.SignatureType == null) return false;

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.DelegateDef(name, node.SignatureType, generics, node.IsPublic);
            return true;
        }

        // L5: build a FLAT UsingDef, or return false to fall back. Lowerable iff
        // every path segment is a non-empty identifier (matches the visitor's
        // per-segment validation; an empty segment defers so the visitor reports
        // it at the precise position).
        private static bool TryBuildUsingDef(Parser.Nodes.Namespaces.UsingNamespaceNode node, out Defs.UsingDef def)
        {
            def = null!;
            var segments = new string[node.Segments.Count];
            for (int i = 0; i < node.Segments.Count; i++)
            {
                segments[i] = node.Segments[i].Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(segments[i])) return false;
            }
            if (segments.Length == 0) return false;
            def = new Defs.UsingDef(segments, node.HasAlias ? node.Alias : null);
            return true;
        }

        // L5e: build a FLAT StructDef (fields + precompiled method bodies), or
        // return false to fall back to the visitor. Lowerable subset: no
        // annotations / where-constraints / operators / properties / events,
        // no field default values, no method param defaults. Method bodies are
        // compiled via the SAME runtime helper the visitor uses lazily
        // (`GetOrCompileStructMethod`) → byte-identical bytecode. The handler
        // reconstructs the StructDefinitionNode and runs the same visitor Apply,
        // so registration / validation / dispatch match exactly.
        private static bool TryBuildStructDef(Parser.Nodes.Structs.StructDefinitionNode node, out Defs.StructDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.Operators != null && node.Operators.Count > 0) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;

            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            var fields = new Defs.StructFieldDef[node.Fields.Count];
            for (int i = 0; i < node.Fields.Count; i++)
            {
                var fld = node.Fields[i];
                if (fld.HasAnnotations) return false;
                RuntimeValue? defConst = null;
                if (fld.DefaultValueNode != null && !TryFoldFieldDefaultConst(fld.DefaultValueNode, out defConst))
                    return false; // non-constant field default → fallback to the visitor
                fields[i] = new Defs.StructFieldDef(
                    fld.NameTok.Value?.ToString() ?? "", fld.FieldType,
                    fld.IsPublic, fld.IsStatic, fld.IsAbstract, fld.IsOverride,
                    (int)fld.DeclarationType, defConst);
            }

            var methods = new Defs.StructMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
                if (!TryBuildStructMethodDef(node.Methods[i], out methods[i])) return false;

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.StructDef(name, node.IsPublic, generics, fields, methods);
            return true;
        }

        // Shared method-descriptor build for struct + record (both use
        // StructMethodDefinitionNode). Compiles the body via the SAME runtime
        // path the visitor uses lazily → identical bytecode. Methods with
        // annotations or param-defaults (AST) → false (the type falls back).
        private static bool TryBuildStructMethodDef(Parser.Nodes.Structs.StructMethodDefinitionNode m, out Defs.StructMethodDef md)
        {
            md = null!;
            if (m.HasAnnotations) return false;
            if (m.ParamDefaults != null)
                foreach (var pd in m.ParamDefaults)
                    if (pd != null) return false;

            var body = Runtime.FunctionDefinitionHelper.GetOrCompileStructMethod(m);
            if (body == null) return false;

            var argNames = new string[m.ArgNameToks.Count];
            for (int a = 0; a < m.ArgNameToks.Count; a++)
                argNames[a] = m.ArgNameToks[a].Value?.ToString() ?? "";

            md = new Defs.StructMethodDef(
                m.NameTok.Value?.ToString() ?? "", m.IsPublic, m.IsConstructor, m.IsAsync, m.IsAsyncStream,
                argNames, m.ArgTypes.ToArray(), m.IsRefParams.ToArray(), m.HasVarArgs,
                m.VarArgNameTok?.Value?.ToString(), m.VarArgType, m.ReturnType, m.ShouldAutoReturn,
                m.FrameId, body);
            return true;
        }

        // L5e: build a FLAT RecordDef, or return false to fall back. First
        // sub-stage: value records (no `record class` inheritance), no abstract /
        // operators / properties / events / annotations / where-constraints /
        // param-defaults / non-const field-defaults. Methods reuse the shared
        // struct-method build; primary-field defaults fold like struct fields.
        private static bool TryBuildRecordDef(Parser.Nodes.Records.RecordDefinitionNode node, out Defs.RecordDef def)
        {
            def = null!;
            if (node.BaseType != null) return false;       // inheritance → fallback
            if (node.IsAbstract) return false;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.Operators != null && node.Operators.Count > 0) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;

            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            var pfields = new Defs.RecordPrimaryFieldDef[node.PrimaryFields.Count];
            for (int i = 0; i < node.PrimaryFields.Count; i++)
            {
                var pf = node.PrimaryFields[i];
                RuntimeValue? defConst = null;
                if (pf.DefaultValueNode != null && !TryFoldFieldDefaultConst(pf.DefaultValueNode, out defConst))
                    return false;
                pfields[i] = new Defs.RecordPrimaryFieldDef(
                    pf.NameTok.Value?.ToString() ?? "", pf.FieldType, pf.IsPublic, pf.IsMutable, defConst);
            }

            var methods = new Defs.StructMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
                if (!TryBuildStructMethodDef(node.Methods[i], out methods[i])) return false;

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.RecordDef(name, node.IsPublic, node.IsRefRecord,
                node.AutoEquals, node.AutoToString, generics, pfields, methods);
            return true;
        }

        // L5e: build a FLAT ClassDef, or return false to fall back. First
        // sub-stage: plain classes — no inheritance / interfaces / traits /
        // properties / events / operators / static / abstract / annotations /
        // where-constraints. Fields reuse the struct field machinery; class
        // methods are FunctionDefinitionNodes (via TryBuildClassMethodDef).
        private static bool TryBuildClassDef(Parser.Nodes.Classes.ClassDefinitionNode node, out Defs.ClassDef def)
        {
            def = null!;
            if (node.BaseType != null) return false;
            if (node.ImplementedInterfaces != null && node.ImplementedInterfaces.Count > 0) return false;
            if (node.WithTraits != null && node.WithTraits.Count > 0) return false;
            if (node.IsAbstract || node.IsStatic) return false;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.Operators != null && node.Operators.Count > 0) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;

            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            var fields = new Defs.StructFieldDef[node.Fields.Count];
            for (int i = 0; i < node.Fields.Count; i++)
            {
                var fld = node.Fields[i];
                if (fld.HasAnnotations) return false;
                RuntimeValue? defConst = null;
                if (fld.DefaultValueNode != null && !TryFoldFieldDefaultConst(fld.DefaultValueNode, out defConst))
                    return false;
                fields[i] = new Defs.StructFieldDef(
                    fld.NameTok.Value?.ToString() ?? "", fld.FieldType,
                    fld.IsPublic, fld.IsStatic, fld.IsAbstract, fld.IsOverride,
                    (int)fld.DeclarationType, defConst);
            }

            var methods = new Defs.ClassMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
                if (!TryBuildClassMethodDef(node.Methods[i], out methods[i])) return false;

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.ClassDef(name, node.IsPublic, generics, fields, methods);
            return true;
        }

        // Class method = FunctionDefinitionNode. Compiles via GetOrCompileBody
        // (same path the visitor uses lazily). Factory ctors / abstract /
        // generic / param-default / param-annotated / captured / where-
        // constrained / annotated methods → false (the class falls back).
        private static bool TryBuildClassMethodDef(Parser.Nodes.Functions.FunctionDefinitionNode m, out Defs.ClassMethodDef md)
        {
            md = null!;
            if (m.IsAbstract || m.IsFactory) return false;
            if (m.ConstructorName != null) return false; // named ctor (Name.ctor) — name not captured yet
            if (m.HasAnnotations) return false;
            if (m.CaptureList != null && m.CaptureList.Count > 0) return false;
            if (m.GenericTypeParams != null && m.GenericTypeParams.Count > 0) return false;
            if (m.WhereConstraints != null && m.WhereConstraints.Count > 0) return false;
            if (m.ParamDefaults != null)
                foreach (var pd in m.ParamDefaults) if (pd != null) return false;
            if (m.ParamAnnotations != null)
                foreach (var pa in m.ParamAnnotations) if (pa != null && pa.Count > 0) return false;
            if (m.VarArgAnnotations != null && m.VarArgAnnotations.Count > 0) return false;

            var body = Runtime.FunctionDefinitionHelper.GetOrCompileBody(m);
            if (body == null) return false;

            string mname = m.VarNameTok?.Value?.ToString() ?? "";
            var argNames = new string[m.ArgNameToks.Count];
            for (int a = 0; a < m.ArgNameToks.Count; a++)
                argNames[a] = m.ArgNameToks[a].Value?.ToString() ?? "";

            md = new Defs.ClassMethodDef(
                mname, m.IsPublic, m.IsConstructor, m.IsOverride, m.IsStatic, m.IsAsync, m.IsAsyncStream,
                argNames, m.ArgTypes.ToArray(), m.IsRefParams.ToArray(), m.HasVarArgs,
                m.VarArgNameTok?.Value?.ToString(), m.VarArgType, m.ReturnType, m.ShouldAutoReturn,
                m.FrameId, body);
            return true;
        }

        // L5e: build a FLAT TraitDef, or false to fall back. First sub-stage:
        // methods (provided + abstract/required) + fields; fallback on
        // properties / events / where-constraints / annotations.
        private static bool TryBuildTraitDef(Parser.Nodes.Traits.TraitDefinitionNode node, out Defs.TraitDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;

            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            var fields = new Defs.StructFieldDef[node.Fields.Count];
            for (int i = 0; i < node.Fields.Count; i++)
            {
                var fld = node.Fields[i];
                if (fld.HasAnnotations) return false;
                RuntimeValue? defConst = null;
                if (fld.DefaultValueNode != null && !TryFoldFieldDefaultConst(fld.DefaultValueNode, out defConst))
                    return false;
                fields[i] = new Defs.StructFieldDef(
                    fld.NameTok.Value?.ToString() ?? "", fld.FieldType,
                    fld.IsPublic, fld.IsStatic, fld.IsAbstract, fld.IsOverride,
                    (int)fld.DeclarationType, defConst);
            }

            var methods = new Defs.TraitMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
                if (!TryBuildTraitMethodDef(node.Methods[i], out methods[i])) return false;

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.TraitDef(name, node.IsPublic, generics, fields, methods);
            return true;
        }

        private static bool TryBuildTraitMethodDef(Parser.Nodes.Traits.TraitMethodDefinitionNode m, out Defs.TraitMethodDef md)
        {
            md = null!;
            if (m.HasAnnotations) return false;
            if (m.ParamDefaults != null)
                foreach (var pd in m.ParamDefaults) if (pd != null) return false;

            // Abstract/required methods carry no body; provided methods compile
            // via the SAME runtime path the visitor uses lazily.
            RaFunction? body = null;
            if (!m.IsAbstract && m.BodyNode != null)
            {
                body = Runtime.FunctionDefinitionHelper.GetOrCompileTraitMethod(m);
                if (body == null) return false;
            }

            var argNames = new string[m.ArgNameToks.Count];
            for (int a = 0; a < m.ArgNameToks.Count; a++)
                argNames[a] = m.ArgNameToks[a].Value?.ToString() ?? "";

            md = new Defs.TraitMethodDef(
                m.NameTok?.Value?.ToString() ?? "", m.IsAbstract, m.IsAsync, m.IsAsyncStream,
                argNames, m.ArgTypes.ToArray(), m.IsRefParams.ToArray(), m.HasVarArgs,
                m.VarArgNameTok?.Value?.ToString(), m.VarArgType, m.ReturnType, m.ShouldAutoReturn,
                m.FrameId, body);
            return true;
        }

        // L5e: build a FLAT InterfaceDef, or false to fall back. Interface methods
        // are pure SIGNATURES (no bodies → no precompiled RaFunction); fields
        // reuse the struct field descriptor (interface fields can't carry defaults
        // → DefaultConst stays null). Fallback on annotations (on the interface, a
        // method, or a field) / properties / events / where-constraints / a field
        // that declares a default value (the visitor rejects those — fall back so
        // it surfaces the identical error directly). Invalid-but-default-free
        // fields (final/let/no-type) still lower: the reconstructed node re-runs
        // the SAME visitor validation → byte-identical error.
        private static bool TryBuildInterfaceDef(Parser.Nodes.Interfaces.InterfaceDefinitionNode node, out Defs.InterfaceDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false;
            if (node.WhereConstraints != null && node.WhereConstraints.Count > 0) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;

            string name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            var fields = new Defs.StructFieldDef[node.Fields.Count];
            for (int i = 0; i < node.Fields.Count; i++)
            {
                var fld = node.Fields[i];
                if (fld.HasAnnotations) return false;
                if (fld.DefaultValueNode != null) return false; // interface fields can't have defaults
                fields[i] = new Defs.StructFieldDef(
                    fld.NameTok.Value?.ToString() ?? "", fld.FieldType,
                    fld.IsPublic, fld.IsStatic, fld.IsAbstract, fld.IsOverride,
                    (int)fld.DeclarationType, null);
            }

            var methods = new Defs.InterfaceMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
            {
                var m = node.Methods[i];
                if (m.HasAnnotations) return false;
                var argNames = new string[m.ArgNameToks.Count];
                for (int a = 0; a < m.ArgNameToks.Count; a++)
                    argNames[a] = m.ArgNameToks[a].Value?.ToString() ?? "";
                methods[i] = new Defs.InterfaceMethodDef(
                    m.NameTok.Value?.ToString() ?? "", argNames, m.ArgTypes.ToArray(), m.ReturnType);
            }

            var generics = (node.GenericTypeParams == null || node.GenericTypeParams.Count == 0)
                ? System.Array.Empty<string>()
                : node.GenericTypeParams.ToArray();
            def = new Defs.InterfaceDef(name, node.IsPublic, generics, fields, methods);
            return true;
        }

        // L5e: build a FLAT AnnotationDef, or false to fall back. First sub-stage:
        // annotations with NO meta-annotations (any `@meta annotation Foo` carries
        // argument expressions + registers metadata via AnnotationProcessor →
        // fall back) and only const-foldable / absent parameter defaults.
        private static bool TryBuildAnnotationDef(Parser.Nodes.Annotations.AnnotationDefinitionNode node, out Defs.AnnotationDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false; // meta-annotations → fallback
            string name = node.Name;
            if (string.IsNullOrWhiteSpace(name)) return false;

            var ps = new Defs.AnnotationParamDef[node.Parameters.Count];
            for (int i = 0; i < node.Parameters.Count; i++)
            {
                var p = node.Parameters[i];
                RuntimeValue? defConst = null;
                if (p.DefaultValueNode != null && !TryFoldFieldDefaultConst(p.DefaultValueNode, out defConst))
                    return false;
                ps[i] = new Defs.AnnotationParamDef(p.Name, p.DeclaredType, defConst, p.IsVarArgs);
            }

            def = new Defs.AnnotationDef(name, node.IsPublic, ps);
            return true;
        }

        // L6: build a FLAT ImportDef from an ImportNode. The ModuleSpecifier is
        // already flat (string-literal raw path OR dotted segments + wildcard);
        // the form-specific extra is the selected names (Selective) or alias
        // (Alias). No fallback — every import shape lowers.
        private static Defs.ImportDef BuildImportDef(Parser.Nodes.Imports.ImportNode node)
        {
            var spec = node.Specifier;
            bool isDotted = spec.Kind == Modules.ModuleSpecifierKind.Dotted;
            string[] segments;
            if (spec.Segments != null)
            {
                segments = new string[spec.Segments.Count];
                for (int i = 0; i < spec.Segments.Count; i++) segments[i] = spec.Segments[i];
            }
            else segments = System.Array.Empty<string>();

            switch (node)
            {
                case Parser.Nodes.Imports.ImportSelectiveNode sel:
                {
                    var names = new string[sel.SymbolNames.Count];
                    for (int i = 0; i < sel.SymbolNames.Count; i++)
                        names[i] = sel.SymbolNames[i].Value?.ToString() ?? "";
                    return new Defs.ImportDef(Defs.ImportDefKind.Selective, isDotted, spec.RawPath, segments,
                        spec.IsWildcard, names, null);
                }
                case Parser.Nodes.Imports.ImportAliasNode al:
                    return new Defs.ImportDef(Defs.ImportDefKind.Alias, isDotted, spec.RawPath, segments,
                        spec.IsWildcard, System.Array.Empty<string>(), al.Alias);
                default: // ImportAllNode
                    return new Defs.ImportDef(Defs.ImportDefKind.All, isDotted, spec.RawPath, segments,
                        spec.IsWildcard, System.Array.Empty<string>(), null);
            }
        }

        // L6: build a FLAT NamespaceDef, or false to fall back. The body
        // statements are precompiled to RaFunctions the SAME way the visitor's
        // on-demand IrExpressionEvaluator path compiles them, so the lowered
        // path is bytecode-identical. A nested compile that throws (can't lower)
        // → fall back to the visitor.
        private static bool TryBuildNamespaceDef(Parser.Nodes.Namespaces.NamespaceDeclarationNode node, out Defs.NamespaceDef def)
        {
            def = null!;
            if (s_namespaceLoweringOff) return false; // gate (flipped on once handler+serializer validated)

            var stmts = ExtractNamespaceStatements(node.Body);
            var bodies = new RaFunction[stmts.Count];
            try
            {
                for (int i = 0; i < stmts.Count; i++)
                    bodies[i] = Runtime.IrExpressionEvaluator.CompileBodyStatement(stmts[i]);
            }
            catch (IrCompileException)
            {
                return false;
            }

            var segs = new string[node.Segments.Count];
            for (int i = 0; i < node.Segments.Count; i++)
                segs[i] = node.Segments[i].Value?.ToString() ?? "";

            def = new Defs.NamespaceDef(segs, node.IsFileScoped, bodies);
            return true;
        }

        // Mirrors NamespaceDeclarationNodeVisitor.ExtractStatements: a brace body
        // is a ScopeNode whose Nodes run at namespace scope (NOT in a pushed
        // child scope); a single-statement body is wrapped.
        private static System.Collections.Generic.IReadOnlyList<AstNode> ExtractNamespaceStatements(AstNode body)
        {
            if (body is Parser.Nodes.Special.ScopeNode scope) return scope.Nodes;
            return new[] { body };
        }

        // Gate for the NamespaceDeclaration lowering (L6). Validated OFF first
        // (visitor refactor + IrExpressionEvaluator additions proven behavior-
        // preserving via the still-active OP_NATIVE_DEFINE path), now flipped ON
        // (false) — body statements precompiled into a NamespaceDef.
        private static readonly bool s_namespaceLoweringOff = false;

        // L5e: build a FLAT ExtensionDef, or false to fall back. First sub-stage:
        // methods only (extension methods are FunctionDefinitionNodes → reuse
        // TryBuildClassMethodDef); fallback on properties/operators/events/
        // fields/indexers.
        private static bool TryBuildExtensionDef(Parser.Nodes.Classes.ExtensionDefinitionNode node, out Defs.ExtensionDef def)
        {
            def = null!;
            if (node.HasAnnotations) return false;
            if (node.Properties != null && node.Properties.Count > 0) return false;
            if (node.Operators != null && node.Operators.Count > 0) return false;
            if (node.Events != null && node.Events.Count > 0) return false;
            if (node.Fields != null && node.Fields.Count > 0) return false;
            if (node.Indexers != null && node.Indexers.Count > 0) return false;
            if (node.TargetType == null) return false;

            var methods = new Defs.ClassMethodDef[node.Methods.Count];
            for (int i = 0; i < node.Methods.Count; i++)
                if (!TryBuildClassMethodDef(node.Methods[i], out methods[i])) return false;

            def = new Defs.ExtensionDef(node.TargetType, node.IsPublic, node.IsSealed, methods);
            return true;
        }

        // L5e: fold a struct field's default-value expression to a constant
        // RuntimeValue at compile time, or return false (→ the struct falls back
        // to the visitor). Covers plain literals (number / bool / null / non-
        // interpolated string) — the overwhelming majority of `var x = <lit>`
        // field defaults. The folded const is produced by the SAME path the
        // visitor would (NumberNodeVisitor.ParseLiteral / the literal value), so
        // construction is byte-identical; the handler rebuilds a NumberNode whose
        // `CachedValue` is this const (NumberNodeVisitor returns CachedValue
        // verbatim when set, so it round-trips any value type).
        private static bool TryFoldFieldDefaultConst(AstNode node, out RuntimeValue val)
        {
            val = null!;
            switch (node)
            {
                case NumberNode nn:
                    try { val = Visitors.Primitives.NumberNodeVisitor.ParseLiteral(nn); return true; }
                    catch { return false; }
                case Parser.Nodes.Primitives.BooleanNode bn:
                    if (bn.Token.Value is Lexer.Tokens.Keyword kw)
                    { val = Values.Primitives.BooleanValue.Of(kw == Lexer.Tokens.Keyword.True); return true; }
                    return false;
                case Parser.Nodes.Primitives.NullNode:
                    val = Values.Primitives.NullValue.Null; return true;
                case StringNode sn:
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < sn.Parts.Count; i++)
                    {
                        if (sn.Parts[i] is Parser.Nodes.Primitives.StringTextNode st) sb.Append(st.Text);
                        else return false; // interpolated → not a compile-time constant
                    }
                    val = new Values.Primitives.StringValue(sb.ToString()); return true;
                }
                default:
                    return false;
            }
        }

        private static bool TryCompileStatement(
            AstNode stmt, State st, ref byte topSlot, byte scratchSlot, bool strict)
        {
            // M44: anchor a (PC → source span) entry at the start of every
            // top-level statement. Coarse but enough for the VM to surface
            // a real source position when an opcode raises a runtime
            // error mid-statement.
            st.RecordPcSpan(stmt);
            switch (stmt.NodeType)
            {
                // ---- pure expression statements (M2) ----
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                case AstNodeType.Null:
                case AstNodeType.String:
                case AstNodeType.VariableAccess:
                case AstNodeType.BinaryOperation:
                case AstNodeType.UnaryOperation:
                case AstNodeType.FunctionCall:
                case AstNodeType.FunctionDefinition:
                case AstNodeType.MemberAccess:
                case AstNodeType.ListAccess:
                case AstNodeType.EnumAccess:
                case AstNodeType.Self:
                case AstNodeType.Super:
                case AstNodeType.Typeof:
                case AstNodeType.Nameof:
                case AstNodeType.Dereference:
                case AstNodeType.Cast:
                case AstNodeType.IsType:
                case AstNodeType.Ternary:
                case AstNodeType.NullCoalescing:
                case AstNodeType.Range:
                case AstNodeType.List:
                case AstNodeType.Set:
                case AstNodeType.Map:
                case AstNodeType.Tuple:
                // L3: borrow / deref-store as a bare statement lower through the
                // same expression path (CompileExpression has dedicated cases);
                // the produced value lands in scratch and is discarded.
                case AstNodeType.Borrow:
                case AstNodeType.DereferenceAssignment:
                // L4: a bare `x |> f()` statement routes through the same
                // expression path (CompileExpression desugars it to OP_CALL);
                // the call result lands in scratch and is discarded.
                case AstNodeType.Pipeline:
                // L4: a bare `recv with { ... }` statement likewise lowers via
                // the expression path (CompileExpression emits OP_WITH); the
                // clone lands in scratch and is discarded.
                case AstNodeType.WithExpression:
                {
                    byte topAtEntry = topSlot;
                    CompileExpression(stmt, scratchSlot, st, ref topSlot);
                    if (topSlot < topAtEntry) topSlot = topAtEntry;
                    return true;
                }

                // ---- control flow (M3) ----
                case AstNodeType.Pass:
                    // `pass` is a pure no-op. Skip the emit entirely so the
                    // dispatch loop doesn't pay a decode + switch for a NOP
                    // body inside a hot loop (`for i = 0 to N { pass; }`).
                    return true;

                case AstNodeType.Scope:
                {
                    // Top-level `{ ... }` bare blocks need their own scope so
                    // local declarations die at block end. We now have scope
                    // opcodes so we can compile them natively.
                    var sc = (ScopeNode)stmt;
                    if (!strict)
                    {
                        EmitPushScope(st);
                        foreach (var child in sc.Nodes)
                            CompileStatementWithFallback(child, st, scratchSlot);
                        EmitPopScope(st);
                        return true;
                    }
                    return CompileScopeStrict(sc, st, ref topSlot, scratchSlot);
                }

                case AstNodeType.If:
                    CompileIf((IfNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.While:
                    CompileWhile((WhileNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.DoWhile:
                    CompileDoWhile((DoWhileNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.For:
                    CompileFor((ForNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.ForEach:
                    CompileForEach((ForEachNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                // L1: C-style `for (init; cond; step) { body }`.
                case AstNodeType.SuperFor:
                    CompileSuperFor((Parser.Nodes.Statements.SuperForNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.Try:
                    CompileTry((Parser.Nodes.Special.TryNode)stmt, st, ref topSlot, scratchSlot);
                    return true;

                case AstNodeType.Break:
                {
                    if (st.Loops.Count == 0)
                        throw new IrCompileException("`break` outside loop");
                    var loop = st.Loops.Peek();
                    EmitPopsDownTo(st, loop.BaselineScopeDepth);
                    int pc = st.Code.EmitForwardJump(Opcode.Jmp);
                    loop.BreakFixups.Add(pc);
                    return true;
                }

                case AstNodeType.Continue:
                {
                    // L7: continue targets the nearest enclosing real LOOP,
                    // passing through any switch break-barrier contexts.
                    var loop = NearestRealLoop(st);
                    if (loop == null)
                        throw new IrCompileException("`continue` outside loop");
                    EmitPopsDownTo(st, loop.BaselineScopeDepth);
                    int pc = st.Code.EmitForwardJump(Opcode.Jmp);
                    loop.ContinueFixups.Add(pc);
                    return true;
                }

                case AstNodeType.Retry:
                {
                    var loop = NearestRealLoop(st);
                    if (loop == null)
                        throw new IrCompileException("`retry` outside loop");
                    EmitPopsDownTo(st, loop.BaselineScopeDepth);
                    st.Code.EmitBackwardJump(Opcode.Jmp, 0, loop.RetryTargetPc);
                    return true;
                }

                case AstNodeType.Return:
                {
                    // OP_RET exits the dispatch loop immediately. Any open
                    // scopes go out of C# scope along with the VmFrame, so
                    // no PopScopes need to be emitted before the RET.
                    var rn = (ReturnNode)stmt;
                    if (rn.NodeToReturn == null)
                    {
                        st.Code.Emit3(Opcode.RetNull, 0, 0, 0);
                    }
                    else
                    {
                        // M28.3: tail-call detection. When the returned
                        // expression is a positional-only FunctionCall the IR
                        // can natively compile, emit OP_TAIL_CALL instead of
                        // the OP_CALL + OP_RET pair — saves the OP_RET
                        // dispatch and prepares the ground for true
                        // stack-trampolined TCO if/when the dispatch loop is
                        // refactored to a thunk-return discipline.
                        if (rn.NodeToReturn is FunctionCallNode fcRet
                            && IsCallNativelyCompilable(fcRet)
                            && fcRet.ArgNodes.Count <= byte.MaxValue
                            && TryEmitTailCall(fcRet, st, ref topSlot))
                        {
                            return true;
                        }
                        byte slot = scratchSlot;
                        CompileExpression(rn.NodeToReturn, slot, st, ref topSlot);
                        st.Code.Emit3(Opcode.Ret, slot, 0, 0);
                    }
                    return true;
                }

                case AstNodeType.Throw:
                {
                    var thr = (ThrowNode)stmt;
                    byte slot = AllocTemp(ref topSlot);
                    CompileExpression(thr.Expression, slot, st, ref topSlot);
                    st.Code.Emit3(Opcode.Throw, slot, 0, 0);
                    return true;
                }

                // L5: enum definitions whose variant values are all auto or
                // constant-integer literals (and with no annotations / where-
                // constraints) lower to a FLAT EnumDef descriptor + OP_DEFINE_TYPE
                // — no AST in the `.rac`. Anything outside that flat subset
                // throws → CompileStatementWithFallback emits OP_NATIVE_DEFINE so
                // the visitor handles it (value side effects, exotic types,
                // annotations, generics-with-constraints, …).
                case AstNodeType.EnumDefinition:
                {
                    var en = (Parser.Nodes.Enums.EnumDefinitionNode)stmt;
                    if (!TryBuildEnumDef(en, out var edef))
                        throw new IrCompileException("enum not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(edef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5: `delegate Name = fn(...) -> R` — pure flat metadata (the
                // structural signature is a TypeDescriptor). Lowers to a
                // DelegateDef + OP_DEFINE_TYPE. Where-constraints carry AST →
                // fall back to the visitor.
                case AstNodeType.DelegateDefinition:
                {
                    var dn = (Parser.Nodes.Functions.DelegateDefinitionNode)stmt;
                    if (!TryBuildDelegateDef(dn, out var ddef))
                        throw new IrCompileException("delegate not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(ddef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5: `using a.b.c [as alias]` — a flat one-shot directive (the
                // dotted path + optional alias). Lowers to a UsingDef +
                // OP_DEFINE_TYPE; the handler runs the same namespace resolve +
                // member injection. An empty path segment → fall back to the
                // visitor (which carries a per-segment position).
                case AstNodeType.UsingNamespace:
                {
                    var un = (Parser.Nodes.Namespaces.UsingNamespaceNode)stmt;
                    if (!TryBuildUsingDef(un, out var udef))
                        throw new IrCompileException("using not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(udef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `struct Name { ... }` — flat-lower the common subset
                // (fields + methods, no operators/annotations/where-constraints/
                // param-defaults/non-const field-defaults) to a StructDef +
                // OP_DEFINE_TYPE; the handler reconstructs the runtime
                // StructTypeValue from the flat data + precompiled method bodies.
                // Everything outside the subset throws → the visitor handles it.
                case AstNodeType.StructDefinition:
                {
                    var sn = (Parser.Nodes.Structs.StructDefinitionNode)stmt;
                    if (!TryBuildStructDef(sn, out var sdef))
                        throw new IrCompileException("struct not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(sdef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `record Name(fields) { methods }` — value records in the
                // flat-lowerable subset (no inheritance/operators/etc.) lower to
                // a RecordDef + OP_DEFINE_TYPE; the handler reconstructs + runs
                // the same visitor Apply. Reuses the struct method machinery.
                case AstNodeType.RecordDefinition:
                {
                    var rn = (Parser.Nodes.Records.RecordDefinitionNode)stmt;
                    if (!TryBuildRecordDef(rn, out var rdef))
                        throw new IrCompileException("record not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(rdef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `class Name { ... }` — plain classes (no inheritance/
                // interfaces/traits/properties/events/operators/static/abstract)
                // lower to a ClassDef + OP_DEFINE_TYPE; the handler reconstructs
                // + runs the same (async, sync-completing) visitor Apply.
                case AstNodeType.ClassDefinition:
                {
                    var cn = (Parser.Nodes.Classes.ClassDefinitionNode)stmt;
                    if (!TryBuildClassDef(cn, out var cdef))
                        throw new IrCompileException("class not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(cdef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `trait Name { ... }` — methods (provided + abstract) +
                // fields lower to a TraitDef + OP_DEFINE_TYPE.
                case AstNodeType.TraitDefinition:
                {
                    var tn = (Parser.Nodes.Traits.TraitDefinitionNode)stmt;
                    if (!TryBuildTraitDef(tn, out var tdef))
                        throw new IrCompileException("trait not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(tdef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `extend T { ... }` — methods lower to an ExtensionDef +
                // OP_DEFINE_TYPE.
                case AstNodeType.ExtensionDefinition:
                {
                    var en = (Parser.Nodes.Classes.ExtensionDefinitionNode)stmt;
                    if (!TryBuildExtensionDef(en, out var edef))
                        throw new IrCompileException("extension not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(edef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `interface Name { fn sig(); var f: T }` — method
                // signatures (no bodies) + fields lower to an InterfaceDef +
                // OP_DEFINE_TYPE; the handler reconstructs + runs the same
                // visitor Apply. Pure flat metadata (no precompiled bodies).
                case AstNodeType.InterfaceDefinition:
                {
                    var ifn = (Parser.Nodes.Interfaces.InterfaceDefinitionNode)stmt;
                    if (!TryBuildInterfaceDef(ifn, out var idef))
                        throw new IrCompileException("interface not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(idef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L5e: `annotation Name(params)` — annotations with NO meta-
                // annotations + const/absent parameter defaults lower to an
                // AnnotationDef + OP_DEFINE_TYPE; the handler reconstructs + runs
                // the same visitor Apply. Meta-annotated annotations (carrying arg
                // expressions + metadata registration) → fallback.
                case AstNodeType.AnnotationDefinition:
                {
                    var an = (Parser.Nodes.Annotations.AnnotationDefinitionNode)stmt;
                    if (!TryBuildAnnotationDef(an, out var adef))
                        throw new IrCompileException("annotation not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(adef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L6: `import …` (all three forms) — lowers to an ImportDef +
                // OP_DEFINE_TYPE; the handler rebuilds the ImportNode + runs the
                // same ImportNodeVisitor.Apply (ModuleManager.Load resolution).
                // The ModuleSpecifier is already flat data → no fallback.
                case AstNodeType.ImportAll:
                case AstNodeType.ImportSelective:
                case AstNodeType.ImportAlias:
                {
                    var idef = BuildImportDef((Parser.Nodes.Imports.ImportNode)stmt);
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(idef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // L6: `namespace A.B { … }` — body statements precompiled into a
                // NamespaceDef + OP_DEFINE_TYPE; the handler reconstructs the
                // node + runs the same (async, sync-completing) visitor Apply
                // with the precompiled bodies. Gated until the handler +
                // serializer are validated.
                case AstNodeType.NamespaceDeclaration:
                {
                    var nsn = (Parser.Nodes.Namespaces.NamespaceDeclarationNode)stmt;
                    if (!TryBuildNamespaceDef(nsn, out var nsdef))
                        throw new IrCompileException("namespace not flat-lowerable -> fallback");
                    if (st.TypeDefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeDefs overflow (>65535)");
                    ushort tdIdx = (ushort)st.TypeDefs.Count;
                    st.TypeDefs.Add(nsdef);
                    st.Code.Emit2(Opcode.DefineType, scratchSlot, tdIdx);
                    return true;
                }

                // Native registrations + long-tail expressions / statements.
                // The VM dispatches to the visitor's static Apply method
                // directly, bypassing interpreter._visitors[].
                case AstNodeType.DestructuringDeclaration:
                case AstNodeType.TryUnwrap:
                case AstNodeType.Await:
                case AstNodeType.Spawn:
                case AstNodeType.Emit:
                case AstNodeType.ForAwait:
                case AstNodeType.Goto:
                case AstNodeType.Label:
                case AstNodeType.AsmBlock:
                case AstNodeType.RegexLiteral:
                case AstNodeType.FormattedInterpolation:
                case AstNodeType.Yield:
                case AstNodeType.AnnotationApplication:
                {
                    if (st.DefineRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DefineRefs overflow (>65535)");
                    ushort refIdx = (ushort)st.DefineRefs.Count;
                    st.DefineRefs.Add(stmt);
                    st.Code.Emit2(Opcode.NativeDefine, scratchSlot, refIdx);
                    return true;
                }

                // L7: statement-position switch — lower into the scratch slot
                // (value discarded). On a non-lowerable switch, fall back to
                // OP_NATIVE_DEFINE HERE (not by throwing): in a strict body
                // context (while/for body via CompileBodyStrictInline) a throw
                // would propagate and sink the whole enclosing statement.
                case AstNodeType.Switch:
                {
                    int savedPc = st.Code.Pc;
                    byte savedTop = topSlot;
                    int savedRefs = st.DefineRefs.Count;
                    try
                    {
                        CompileSwitchExpr((SwitchNode)stmt, scratchSlot, st, ref topSlot);
                        return true;
                    }
                    catch (IrCompileException)
                    {
                        st.Code.Truncate(savedPc);
                        topSlot = savedTop;
                        if (st.DefineRefs.Count > savedRefs)
                            st.DefineRefs.RemoveRange(savedRefs, st.DefineRefs.Count - savedRefs);
                    }
                    if (st.DefineRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DefineRefs overflow (>65535)");
                    ushort swStmtRefIdx = (ushort)st.DefineRefs.Count;
                    st.DefineRefs.Add(stmt);
                    st.Code.Emit2(Opcode.NativeDefine, scratchSlot, swStmtRefIdx);
                    return true;
                }

                // L7: statement-position match — lower the literal/wildcard
                // subset into scratch (value discarded). On a non-lowerable
                // match, fall back to OP_NATIVE_DEFINE HERE (not by throwing):
                // in a strict body context (while/for body via
                // CompileBodyStrictInline) a throw would propagate and sink the
                // whole enclosing statement (e.g. a while-let desugars to a
                // While wrapping a list-pattern match). Mirrors the old native
                // group that carried Match.
                case AstNodeType.Match:
                {
                    int savedPc = st.Code.Pc;
                    byte savedTop = topSlot;
                    int savedRefs = st.DefineRefs.Count;
                    try
                    {
                        CompileMatchExpr((Parser.Nodes.Patterns.MatchNode)stmt, scratchSlot, st, ref topSlot);
                        return true;
                    }
                    catch (IrCompileException)
                    {
                        st.Code.Truncate(savedPc);
                        topSlot = savedTop;
                        if (st.DefineRefs.Count > savedRefs)
                            st.DefineRefs.RemoveRange(savedRefs, st.DefineRefs.Count - savedRefs);
                    }
                    if (st.DefineRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DefineRefs overflow (>65535)");
                    ushort mStmtRefIdx = (ushort)st.DefineRefs.Count;
                    st.DefineRefs.Add(stmt);
                    st.Code.Emit2(Opcode.NativeDefine, scratchSlot, mStmtRefIdx);
                    return true;
                }

                case AstNodeType.VariableAssignment:
                {
                    // M6: handle compound assignments (+=, -=, *=, /=, %=, **=,
                    // &=, |=, <<=, >>=, and=, or=, ??=). AssignmentHelper.ApplyPrechecked
                    // encodes the operator selection via node.AssignmentToken.Type,
                    // so OP_STORE_GLOBAL covers every form.
                    // M14: plain `=` to a slot-eligible binding bypasses
                    // AssignmentHelper and routes through OP_STORE_LOCAL_S.
                    var va = (VariableAssignmentNode)stmt;
                    // M27.2: try the LoadLocalS+Add+StoreLocalS fused
                    // superinstruction first (`i = i + 1` ⇒ AddIntoSlot). Must
                    // run before we compile the RHS expression because the
                    // fused opcode reads the slot directly — emitting the
                    // unfused LoadLocalS would waste a temp slot.
                    if (va.AssignmentToken.Type == TokenType.EQ
                        && IsSlotEligible(va.Binding, va.BindingKind, st)
                        && TryEmitSelfAdditiveSlot(va, st, ref topSlot))
                    {
                        return true;
                    }
                    // PERF: a plain const-int64 write to a promoted typed
                    // accumulator (e.g. the `a = 0` reset inside
                    // `if c < 0 { a = 0; b = 1 }`). Write the typed Int64 slot
                    // directly so the accumulator keeps its unboxed promotion
                    // across the non-additive write, instead of being
                    // disqualified and boxed for the whole loop. Mark dirty so a
                    // later boxed read republishes. Kept in lock-step with the
                    // matching relaxation in HasNonRedirectableAccumulatorWrite:
                    // both gate on the SAME const-int64 test, so the typed slot
                    // is always the source of truth for this binding.
                    if (va.AssignmentToken.Type == TokenType.EQ
                        && st.TypedAccumulators.TryGetValue(va.Name, out var accConstW)
                        && TryGetLiteralLongFromConstExpr(va.ValueNode, out long accConstVal))
                    {
                        EmitLiteralLongLoad(accConstVal, accConstW.LongSlot, st, ref topSlot);
                        st.DirtyTypedAccs.Add(va.Name);
                        return true;
                    }
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(va.ValueNode, src, st, ref topSlot);
                    if (va.AssignmentToken.Type == TokenType.EQ
                        && IsSlotEligible(va.Binding, va.BindingKind, st))
                    {
                        st.RegisterSlot(va.Binding.Offset, va.Name);
                        st.Code.Emit2(Opcode.StoreLocalS, src, (ushort)va.Binding.Offset);
                        return true;
                    }
                    if (st.AstRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("AstRefs overflow");
                    ushort refIdx = (ushort)st.AstRefs.Count;
                    st.AstRefs.Add(va);
                    st.Code.Emit2(Opcode.StoreGlobal, src, refIdx);
                    return true;
                }

                case AstNodeType.MemberAssignment:
                {
                    // `obj.member = value`. Use OP_SET_MEMBER.
                    var ma = (Parser.Nodes.Structs.MemberAssignmentNode)stmt;
                    if (st.MemberAssignRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("MemberAssignRefs overflow (>65535)");
                    byte ownerSlot = AllocTemp(ref topSlot);
                    CompileExpression(ma.TargetNode.TargetNode, ownerSlot, st, ref topSlot);
                    byte valSlot = AllocTemp(ref topSlot);
                    CompileExpression(ma.ValueNode, valSlot, st, ref topSlot);
                    int refIdx = st.MemberAssignRefs.Count;
                    st.MemberAssignRefs.Add(ma);
                    // M82 — Wide prefix when refIdx > 255.
                    st.Code.Emit3WideC(Opcode.SetMember, ownerSlot, valSlot, refIdx);
                    return true;
                }

                case AstNodeType.ListAssignment:
                {
                    // `arr[i] = v` (or compound). Use OP_SET_INDEX with the
                    // contract that idxSlot is followed by valSlot.
                    var la = (Parser.Nodes.Variables.ListAssignmentNode)stmt;
                    if (la.Target.NodeType != AstNodeType.ListAccess)
                    {
                        if (strict) throw new IrCompileException("list-assignment target not ListAccess");
                        return false;
                    }
                    if (st.ListAssignRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("ListAssignRefs overflow (>65535)");
                    var lan = (Parser.Nodes.Variables.ListAccessNode)la.Target;
                    byte tgtSlot = AllocTemp(ref topSlot);
                    CompileExpression(lan.Target, tgtSlot, st, ref topSlot);
                    // M24: reserve idxSlot + valSlot as a consecutive pair
                    // BEFORE compiling the index expression. The encoded
                    // OP_SET_INDEX expects valSlot == idxSlot + 1; if we
                    // alloc them after the index compile, an expression
                    // like `m[(i as string)] = i` whose index sub-tree
                    // bumps topSlot internally would push valSlot to
                    // idxSlot + 2 and fail the contract. Pre-reserving the
                    // pair keeps internal index temps allocating after the
                    // value slot.
                    byte idxSlot = AllocTemp(ref topSlot);
                    byte valSlot = AllocTemp(ref topSlot); // = idxSlot + 1 (guaranteed)
                    if (valSlot != idxSlot + 1)
                        throw new IrCompileException("VM SetIndex layout requires idxSlot+1 = valSlot");
                    CompileExpression(lan.Index, idxSlot, st, ref topSlot);
                    CompileExpression(la.Value, valSlot, st, ref topSlot);
                    int refIdx = st.ListAssignRefs.Count;
                    st.ListAssignRefs.Add(la);
                    // M82 — Wide prefix when refIdx > 255.
                    st.Code.Emit3WideC(Opcode.SetIndex, tgtSlot, idxSlot, refIdx);
                    return true;
                }

                case AstNodeType.VariableDeclaration:
                {
                    var vd = (VariableDeclarationNode)stmt;
                    if (!Runtime.DeclarationHelper.IsNativelyCompilable(vd))
                    {
                        if (strict)
                            throw new IrCompileException("declaration not natively compilable in strict body");
                        return false;
                    }
                    var initExpr = vd.Declarations[0].Item2!;
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(initExpr, src, st, ref topSlot);
                    if (st.AstRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("AstRefs overflow");
                    ushort refIdx = (ushort)st.AstRefs.Count;
                    st.AstRefs.Add(vd);
                    if (vd.Bindings != null && vd.Bindings.Length > 0
                        && vd.Bindings[0].IsResolved && vd.Bindings[0].FrameId == st.FrameId)
                    {
                        var declName = vd.Declarations[0].Item1.Value?.ToString();
                        st.RegisterSlot(vd.Bindings[0].Offset, declName);
                    }
                    st.Code.Emit2(Opcode.DeclareLocal, src, refIdx);
                    return true;
                }

                default:
                    if (strict)
                        throw new IrCompileException($"unsupported statement in compiled body: {stmt.NodeType}");
                    return false;
            }
        }

        private static bool CompileScopeStrict(
            ScopeNode scope, State st, ref byte topSlot, byte scratchSlot)
        {
            EmitPushScope(st);
            foreach (var child in scope.Nodes)
            {
                if (!TryCompileStatement(child, st, ref topSlot, scratchSlot, strict: true))
                    throw new IrCompileException($"strict scope child not compilable: {child.NodeType}");
            }
            EmitPopScope(st);
            return true;
        }

        // Compile `if cond { body } elif … else …`. Each branch body runs
        // inside a fresh scope so nested `var x = ...` declarations die at
        // branch exit.
        //
        // M21.1: dropped the BodyContainsUnsupported pre-checks. Strict-mode
        // TryCompileStatement already emits OP_NATIVE_DEFINE for the long
        // tail (Match, Switch, Try, Await, AnnotationApplication, etc.), so
        // pre-rejecting them throws away native lowering of the if scaffold
        // for no gain. Anything strict-mode genuinely can't express still
        // throws IrCompileException, which the outer
        // CompileStatementWithFallback catches and routes the whole If
        // through OP_NATIVE_DEFINE → IfNodeVisitor.Apply.
        private static void CompileIf(IfNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            var endJumps = new List<int>();

            for (int i = 0; i < node.Cases.Count; i++)
            {
                var (cond, body, _shouldReturnNull) = node.Cases[i];

                // M22.1: constant-fold a literal True / False / Null
                // condition. `if true { X } else { Y }` → emit X only.
                // `if false { X } else { Y }` → skip X. Eliminates the
                // condition slot allocation + JmpIfNot + extra jump.
                bool? folded = TryFoldCondition(cond);
                if (folded == true)
                {
                    CompileBodyScoped(body, st, ref topSlot, scratchSlot);
                    // remaining elif / else branches statically dead — drop.
                    foreach (var j in endJumps) st.Code.PatchJumpToHere(j);
                    return;
                }
                if (folded == false)
                {
                    // skip this branch entirely; continue to next elif/else.
                    continue;
                }

                byte condSlot = AllocTemp(ref topSlot);
                CompileExpression(cond, condSlot, st, ref topSlot);

                int skipJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot);

                CompileBodyScoped(body, st, ref topSlot, scratchSlot);

                int endJmp = st.Code.EmitForwardJump(Opcode.Jmp);
                endJumps.Add(endJmp);

                st.Code.PatchJumpToHere(skipJmp);
            }

            if (node.ElseCase.HasValue)
            {
                var (elseBody, _shouldReturnNull) = node.ElseCase.Value;
                CompileBodyScoped(elseBody, st, ref topSlot, scratchSlot);
            }

            foreach (var j in endJumps) st.Code.PatchJumpToHere(j);

            // Conservative post-branch merge: each `if` arm may have
            // taken a different path through dirty tracking. The
            // compile-time set tracks ONE linear path; at runtime any
            // arm may run. Re-add every typed accumulator to the dirty
            // set so the next boxed read in this scope re-publishes.
            MarkAllTypedAccsDirty(st);
        }

        private static void MarkAllTypedAccsDirty(State st)
        {
            foreach (var name in st.TypedAccumulators.Keys)
                st.DirtyTypedAccs.Add(name);
        }

        // L7 (first cut): expression-switch lowering. A switch where EVERY case
        // is arrow (`=>`) with a pure-value expression body reduces to an
        // if-else-chain producing a value — built from EXISTING opcodes
        // (Eq/JmpIf/Jmp/LoadNull, all already modeled in every analysis pass, so
        // NO new opcode surface). The scrutinee is evaluated ONCE into a
        // persistent slot; each case compares it against its labels with
        // Opcode.Eq (runtime → GetComparisonEq, identical to the visitor's
        // switchVal.GetComparisonEq(labelVal)), JmpIf-to-body on the first
        // matching label (so later labels of that case AND all later cases are
        // skipped — matching the visitor's lazy, first-match-wins evaluation).
        // The matched arm's expression lands in destSlot; a default arm is
        // unconditional-when-reached (later cases dead); no match + no default →
        // null. Falls back (IrCompileException → OP_NATIVE_DEFINE) on
        // colon-fallthrough, block/List bodies, or a body that is itself a
        // break/continue/yield/return (the visitor special-cases those).
        private static void CompileSwitchExpr(SwitchNode node, byte destSlot, State st, ref byte topSlot)
        {
            // Route by separator style. All-colon → C-style fallthrough (handles
            // `break`); all-arrow → the pure-value expression-arm path below.
            // A switch mixing `=>` and `:` cases has tangled fallthrough
            // semantics → fall back to the visitor.
            bool anyColon = false, anyArrow = false;
            for (int i = 0; i < node.Cases.Count; i++)
            {
                if (node.Cases[i].Separator == SwitchCaseSeparator.Colon) anyColon = true;
                else anyArrow = true;
            }
            if (anyColon && anyArrow)
                throw new IrCompileException("switch: mixed arrow/colon cases -> fallback");
            if (anyColon)
            {
                CompileSwitchColon(node, destSlot, st, ref topSlot);
                return;
            }

            // Guard the all-arrow lowerable subset (before emitting anything).
            for (int i = 0; i < node.Cases.Count; i++)
            {
                var c = node.Cases[i];
                if (c.Body == null)
                    throw new IrCompileException("switch: null arm body -> fallback");
                bool isBlock = c.Body.NodeType == AstNodeType.List || c.Body.NodeType == AstNodeType.Scope;
                if (isBlock)
                {
                    // Block arm → run stmts, value is null. A control escape
                    // (yield / break / continue / return) inside the block has
                    // inconsistent visitor semantics we don't replicate — fall
                    // back. Only pure side-effect blocks lower.
                    if (ArrowBlockHasControlEscape(c.Body))
                        throw new IrCompileException("switch: arrow-block control escape -> fallback");
                }
                else switch (c.Body.NodeType)
                {
                    case AstNodeType.Return:
                    case AstNodeType.Yield:
                    case AstNodeType.Break:
                    case AstNodeType.Continue:
                        throw new IrCompileException("switch: control-escape arm body -> fallback");
                }
                if (!c.IsDefault && (c.Labels == null || c.Labels.Count == 0))
                    throw new IrCompileException("switch: non-default case with no labels -> fallback");
            }

            // Evaluate the scrutinee exactly once into a persistent slot.
            // NOTE: topSlot grows monotonically here (no reset-down between
            // arms) — like CompileIf. The per-statement MaxTempUsed high-water
            // captures topSlot's PEAK, so resetting it down would undersize the
            // frame (→ IndexOutOfRange at runtime). Extra temps for a wide switch
            // are bounded by the 255 limit (overflow → IrCompileException →
            // fallback).
            byte scrutSlot = AllocTemp(ref topSlot);
            CompileExpression(node.Expression, scrutSlot, st, ref topSlot);
            // Discard slot for block arms' expression-statements (NOT destSlot).
            byte bodyScratch = AllocTemp(ref topSlot);

            var endJumps = new List<int>();
            bool sawDefault = false;

            // Break-barrier: `break` inside a block arm exits the switch (→ end);
            // `continue` / `retry` pass through to the enclosing real loop.
            // Harmless for pure expression arms (no break inside → empty fixups).
            var swCtx = new LoopContext(st.Code.Pc, st.ScopeDepth) { BreakBarrierOnly = true };
            st.Loops.Push(swCtx);

            for (int i = 0; i < node.Cases.Count; i++)
            {
                var c = node.Cases[i];

                if (c.IsDefault)
                {
                    // Reached without a prior match → always taken; later cases dead.
                    EmitArrowBody(c.Body!, destSlot, bodyScratch, st, ref topSlot);
                    endJumps.Add(st.Code.EmitForwardJump(Opcode.Jmp));
                    sawDefault = true;
                    break;
                }

                // Compare the scrutinee against each label; jump to the body on
                // the first match (lazy — later labels skipped at runtime).
                var bodyJumps = new List<int>();
                foreach (var labelExpr in c.Labels)
                {
                    byte labelSlot = AllocTemp(ref topSlot);
                    CompileExpression(labelExpr, labelSlot, st, ref topSlot);
                    byte condSlot = AllocTemp(ref topSlot);
                    st.Code.Emit3(Opcode.Eq, condSlot, scrutSlot, labelSlot);
                    bodyJumps.Add(st.Code.EmitForwardJump(Opcode.JmpIf, condSlot));
                }
                // No label matched → fall through to the next case.
                int skipBody = st.Code.EmitForwardJump(Opcode.Jmp);
                // Body entry — patch every matching-label jump to here.
                foreach (var bj in bodyJumps) st.Code.PatchJumpToHere(bj);
                EmitArrowBody(c.Body!, destSlot, bodyScratch, st, ref topSlot);
                endJumps.Add(st.Code.EmitForwardJump(Opcode.Jmp));
                st.Code.PatchJumpToHere(skipBody);
            }

            st.Loops.Pop();

            // No case matched and no default → null switch value (matches the
            // visitor's trailing `res.Success(NullValue.Null)`).
            if (!sawDefault)
                st.Code.Emit3(Opcode.LoadNull, destSlot, 0, 0);

            foreach (var j in endJumps) st.Code.PatchJumpToHere(j);
            // `break` inside a block arm lands at the switch end too.
            foreach (var bf in swCtx.BreakFixups) st.Code.PatchJumpToHere(bf);

            // Capture the temp high-water (in case no later statement-boundary
            // update does) so the frame is sized for our slots.
            if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;

            // Each arm may take a different path through the linear dirty-set
            // tracking — re-publish every typed accumulator afterwards.
            MarkAllTypedAccsDirty(st);
        }

        // L7: C-style colon switch (every case `case X:` / `default:`).
        // Statement-style — the switch VALUE is null. The scrutinee is evaluated
        // ONCE; a dispatch chain compares it against each case's labels (Opcode.Eq
        // → GetComparisonEq) and jumps to that case's body. Bodies are laid in
        // SOURCE ORDER and FALL THROUGH into each other (C semantics) until a
        // `break` (→ switch end) or the end of the switch. A break-barrier
        // LoopContext catches `break` while letting `continue`/`retry` pass
        // through to the nearest enclosing real loop. Bodies compile via
        // CompileBodyStrictInline (NO per-case scope push → fallthrough shares the
        // switch scope, matching the visitor which runs colon stmts at `context`).
        // Falls back (IrCompileException → OP_NATIVE_DEFINE) when a body statement
        // cannot lower (including `yield`, which the visitor special-cases).
        private static void CompileSwitchColon(SwitchNode node, byte destSlot, State st, ref byte topSlot)
        {
            // A colon switch yields null.
            st.Code.Emit3(Opcode.LoadNull, destSlot, 0, 0);

            // Evaluate the scrutinee exactly once into a persistent slot.
            byte scrutSlot = AllocTemp(ref topSlot);
            CompileExpression(node.Expression, scrutSlot, st, ref topSlot);
            // Dedicated discard slot for the bodies' expression-statements (must
            // NOT be destSlot, which holds the null switch value).
            byte bodyScratch = AllocTemp(ref topSlot);

            int n = node.Cases.Count;
            var caseDispatchJumps = new List<int>[n];
            int defaultIdx = -1;

            // Dispatch: per non-default case, compare vs each label → JmpIf body.
            for (int i = 0; i < n; i++)
            {
                var c = node.Cases[i];
                if (c.IsDefault) { defaultIdx = i; continue; }
                var jumps = new List<int>();
                if (c.Labels != null)
                {
                    foreach (var labelExpr in c.Labels)
                    {
                        byte labelSlot = AllocTemp(ref topSlot);
                        CompileExpression(labelExpr, labelSlot, st, ref topSlot);
                        byte condSlot = AllocTemp(ref topSlot);
                        st.Code.Emit3(Opcode.Eq, condSlot, scrutSlot, labelSlot);
                        jumps.Add(st.Code.EmitForwardJump(Opcode.JmpIf, condSlot));
                    }
                }
                caseDispatchJumps[i] = jumps;
            }
            // No case matched → jump to the default body (if any) or the end.
            int noMatchJump = st.Code.EmitForwardJump(Opcode.Jmp);

            // Break-barrier: `break` inside a body → switch end; `continue` /
            // `retry` walk past to the enclosing real loop.
            var swCtx = new LoopContext(st.Code.Pc, st.ScopeDepth) { BreakBarrierOnly = true };
            st.Loops.Push(swCtx);

            // Bodies in source order, FALLING THROUGH (no jump between them).
            for (int i = 0; i < n; i++)
            {
                var c = node.Cases[i];
                if (caseDispatchJumps[i] != null)
                    foreach (var j in caseDispatchJumps[i]) st.Code.PatchJumpToHere(j);
                if (i == defaultIdx)
                    st.Code.PatchJumpToHere(noMatchJump); // no-match falls into the default body
                if (c.Body != null)
                    CompileBodyStrictInline(c.Body, st, ref topSlot, bodyScratch);
            }

            st.Loops.Pop();

            // Switch end: every `break` jump lands here; if there is no default,
            // the no-match jump lands here too.
            foreach (var bf in swCtx.BreakFixups) st.Code.PatchJumpToHere(bf);
            if (defaultIdx < 0) st.Code.PatchJumpToHere(noMatchJump);

            if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;
            MarkAllTypedAccsDirty(st);
        }

        // L7 (Match, first cut): a `match` whose every arm is a WILDCARD (`_`)
        // or LITERAL (number / string / bool) pattern — no binding, no
        // destructuring — reduces to an if-else chain producing a value, built
        // from existing Eq/JmpIf/Jmp opcodes (NO new opcode surface). Literal
        // arms compare the once-evaluated scrutinee against the literal with
        // Opcode.Eq (= GetComparisonEq, the same the visitor's TryMatchLiteral
        // uses for non-null operands); a wildcard arm matches unconditionally.
        // Guards (`case P if g`) are evaluated AFTER the pattern test and skip to
        // the next arm when false. Requires a wildcard-with-no-guard CATCH-ALL
        // (so the match is exhaustive and no runtime no-match error path is
        // needed — arms after it are dead). Falls back (IrCompileException →
        // OP_NATIVE_DEFINE) on: variable / variant / tuple / list / struct / type
        // / or-/and-/range/… patterns, `null` or non-trivial literal patterns,
        // block or control-escape arm bodies, or a match with no catch-all.
        // NOTE: a literal arm uses plain Eq, which (unlike the visitor) does not
        // pre-check a null scrutinee — sound for the non-null scrutinees in the
        // corpus; null-scrutinee literal matches are caught by the parity oracle.
        private static void CompileMatchExpr(Parser.Nodes.Patterns.MatchNode node, byte destSlot, State st, ref byte topSlot)
        {
            int catchAllIdx = -1;
            int maxBindingSlot = -1; // highest pattern-binding local slot used
            for (int i = 0; i < node.Arms.Count; i++)
            {
                var arm = node.Arms[i];
                switch (arm.Pattern)
                {
                    case Parser.Nodes.Patterns.WildcardPatternNode _:
                        if (arm.Guard == null && catchAllIdx < 0) catchAllIdx = i;
                        break;
                    case Parser.Nodes.Patterns.LiteralPatternNode lp:
                        if (!IsLowerableMatchLiteral(lp.Expression))
                            throw new IrCompileException("match: non-trivial literal pattern -> fallback");
                        break;
                    case Parser.Nodes.Patterns.VariablePatternNode vp:
                    {
                        // Lower a variable pattern ONLY when it is a confirmed
                        // BINDING: the guard/body must reference the name through a
                        // slot-eligible LOCAL access. The Resolver resolves a real
                        // binding ref to a Local slot; a zero-arity variant ref
                        // (`case None`) resolves to Global/enum — so the variant
                        // disambiguation falls out for free (no Local ref → fall
                        // back). An unused binding also can't be confirmed → fall
                        // back. The matched slot is where the arm body reads it.
                        int bslot = FindMatchBindingSlot(vp.Name, arm.Guard, arm.Body, st);
                        if (bslot < 0)
                            throw new IrCompileException("match: unconfirmable variable binding -> fallback");
                        if (bslot > maxBindingSlot) maxBindingSlot = bslot;
                        // A variable arm with no guard always matches → catch-all.
                        if (arm.Guard == null && catchAllIdx < 0) catchAllIdx = i;
                        break;
                    }
                    default:
                        throw new IrCompileException("match: non-literal/wildcard pattern -> fallback");
                }
                // Body must be a pure-value expression (like switch arrow-expr).
                var b = arm.Body;
                switch (b.NodeType)
                {
                    case AstNodeType.List:
                    case AstNodeType.Scope:
                    case AstNodeType.Return:
                    case AstNodeType.Yield:
                    case AstNodeType.Break:
                    case AstNodeType.Continue:
                        throw new IrCompileException("match: block/control-escape arm body -> fallback");
                }
                if (catchAllIdx >= 0) break; // arms after the catch-all are dead
            }
            if (catchAllIdx < 0)
                throw new IrCompileException("match: no wildcard catch-all (exhaustiveness) -> fallback");

            // Reserve the pattern-binding local slots: the Resolver allocates them
            // (e.g. `x` -> slot 1) but the IR's temp allocator (topSlot) only
            // accounts for params/known locals, so temps would otherwise COLLIDE
            // with a binding slot (clobbering the bound value). Bump topSlot above
            // the highest binding slot so every AllocTemp lands clear of them.
            if (maxBindingSlot >= 0 && topSlot <= maxBindingSlot)
            {
                if (maxBindingSlot >= byte.MaxValue)
                    throw new IrCompileException("match: binding slot out of temp range -> fallback");
                topSlot = (byte)(maxBindingSlot + 1);
            }

            // Evaluate the scrutinee exactly once into a persistent slot.
            byte scrutSlot = AllocTemp(ref topSlot);
            CompileExpression(node.Scrutinee, scrutSlot, st, ref topSlot);

            var endJumps = new List<int>();
            for (int i = 0; i <= catchAllIdx; i++)
            {
                var arm = node.Arms[i];
                bool isBinding = arm.Pattern is Parser.Nodes.Patterns.VariablePatternNode;

                // A binding arm runs in a FRESH scope so the declared name is
                // isolated to this arm (mirrors the visitor's per-arm scope): a
                // later arm may bind the SAME name (re-declaration would error),
                // and a failed-guard arm must not leak the binding. PushScope
                // here; PopScope on BOTH the match exit and the no-match skip.
                if (isBinding)
                {
                    EmitPushScope(st);
                    var vp = (Parser.Nodes.Patterns.VariablePatternNode)arm.Pattern;
                    // DECLARE the binding bound to the scrutinee. (StoreLocalS
                    // can't — it assigns to an EXISTING SymbolEntry; the pattern
                    // var is new.) A synthesized `var <name>` decl whose
                    // Bindings[0] is the slot the body/guard resolved the name to
                    // → DeclareLocal: DeclarationHelper.ApplySingle creates the
                    // SymbolEntry from the scrutinee value + caches it into that
                    // slot, so the body's LoadLocalS(slot) reads it.
                    int slot = FindMatchBindingSlot(vp.Name, arm.Guard, arm.Body, st);
                    var nameTok = new Lexer.Tokens.Token(
                        Lexer.Tokens.TokenType.IDENTIFIER, vp.Name, node.PositionStart, node.PositionEnd);
                    var declNode = new Parser.Nodes.Variables.VariableDeclarationNode(
                        Parser.Nodes.Variables.VariableDeclarationType.VARIABLE,
                        new List<(Lexer.Tokens.Token, AstNode?, Types.TypeDescriptor?)> { (nameTok, null, null) });
                    declNode.Bindings = new[] { new RaLanguage.Interpreter.Pipeline.BindingId(st.FrameId, slot) };
                    if (st.AstRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("AstRefs overflow");
                    ushort declRefIdx = (ushort)st.AstRefs.Count;
                    st.AstRefs.Add(declNode);
                    st.RegisterSlot(slot, vp.Name);
                    st.Code.Emit2(Opcode.DeclareLocal, scrutSlot, declRefIdx);
                }

                var skips = new List<int>(); // jumps to this arm's no-match cleanup
                if (arm.Pattern is Parser.Nodes.Patterns.LiteralPatternNode lp)
                {
                    byte litSlot = AllocTemp(ref topSlot);
                    CompileExpression(lp.Expression, litSlot, st, ref topSlot);
                    byte condSlot = AllocTemp(ref topSlot);
                    st.Code.Emit3(Opcode.Eq, condSlot, scrutSlot, litSlot);
                    skips.Add(st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot));
                }
                // Wildcard / variable arms have no pattern test. Guard runs after
                // the pattern test + binding (it can see a variable-pattern bind).
                if (arm.Guard != null)
                {
                    byte gSlot = AllocTemp(ref topSlot);
                    CompileExpression(arm.Guard, gSlot, st, ref topSlot);
                    skips.Add(st.Code.EmitForwardJump(Opcode.JmpIfNot, gSlot));
                }

                // Match: body -> dest, close the arm scope, jump to the end.
                CompileExpression(arm.Body, destSlot, st, ref topSlot);
                if (isBinding) st.Code.Emit3(Opcode.PopScope, 0, 0, 0);
                endJumps.Add(st.Code.EmitForwardJump(Opcode.Jmp));

                // No-match: close the arm scope (if the skip is reachable) and
                // fall through to the next arm.
                foreach (var s in skips) st.Code.PatchJumpToHere(s);
                if (isBinding)
                {
                    if (skips.Count > 0) st.Code.Emit3(Opcode.PopScope, 0, 0, 0);
                    st.ScopeDepth--; // balance the EmitPushScope (no EmitPopScope used)
                }
            }
            // The catch-all guarantees a match — no no-match error path needed.

            foreach (var j in endJumps) st.Code.PatchJumpToHere(j);
            if (topSlot > st.MaxTempUsed) st.MaxTempUsed = topSlot;
            MarkAllTypedAccsDirty(st);
        }

        // A match literal pattern this first cut can lower: a plain number /
        // bool / single-text string. `null` is excluded (the visitor matches it
        // by identity, not Eq); unary-minus / interpolation / others fall back.
        private static bool IsLowerableMatchLiteral(AstNode expr)
        {
            switch (expr.NodeType)
            {
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                    return true;
                case AstNodeType.String:
                {
                    var sn = (Parser.Nodes.Primitives.StringNode)expr;
                    return sn.Parts.Count == 1
                        && sn.Parts[0] is Parser.Nodes.Primitives.StringTextNode;
                }
                default:
                    return false;
            }
        }

        // L7: the local SLOT a match variable-pattern binding occupies, or -1 if
        // it can't be confirmed as a slot-eligible Local binding. The guard/body
        // is searched for a VariableAccess to `name`; if that access resolves to a
        // slot-eligible Local (the Resolver's pattern-binding slot), its offset is
        // returned — this is the slot the body reads, so storing the scrutinee
        // there makes the binding visible. A variant ref (`case None`) resolves to
        // Global/enum (not slot-eligible Local) → -1 → fallback; an unused binding
        // has no access → -1 → fallback.
        private static int FindMatchBindingSlot(string name, AstNode? guard, AstNode? body, State st)
        {
            var acc = FindVarAccessByName(guard, name) ?? FindVarAccessByName(body, name);
            if (acc == null) return -1;
            if (!IsSlotEligible(acc.Binding, acc.BindingKind, st)) return -1;
            return acc.Binding.Offset;
        }

        // Find a VariableAccess to `name` in an expression subtree (common pure-
        // value node kinds; does not descend nested fn/lambda bodies).
        private static Parser.Nodes.Variables.VariableAccessNode? FindVarAccessByName(AstNode? node, string name)
        {
            switch (node)
            {
                case null: return null;
                case Parser.Nodes.Variables.VariableAccessNode va:
                    return va.Name == name ? va : null;
                case Parser.Nodes.Operations.BinaryOperationNode bo:
                    return FindVarAccessByName(bo.LeftNode, name) ?? FindVarAccessByName(bo.RightNode, name);
                case Parser.Nodes.Operations.UnaryOperationNode uo:
                    return FindVarAccessByName(uo.Node, name);
                case CastNode cn:
                    return FindVarAccessByName(cn.Expression, name);
                case Parser.Nodes.Operations.TernaryNode tn:
                    return FindVarAccessByName(tn.Condition, name)
                        ?? FindVarAccessByName(tn.TrueExpression, name)
                        ?? FindVarAccessByName(tn.FalseExpression, name);
                case Parser.Nodes.Functions.FunctionCallNode fc:
                {
                    var r = FindVarAccessByName(fc.NodeToCall, name);
                    if (r != null) return r;
                    foreach (var a in fc.ArgNodes) { r = FindVarAccessByName(a.Expr, name); if (r != null) return r; }
                    return null;
                }
                default: return null;
            }
        }

        // L7: emit one arrow (`=>`) switch arm body. A block body (`{ … }`) runs
        // its statements and the arm value is null (LoadNull dest); an expression
        // body evaluates straight into dest. The caller emits the trailing Jmp to
        // the switch end (arrow arms never fall through).
        private static void EmitArrowBody(AstNode body, byte destSlot, byte bodyScratch, State st, ref byte topSlot)
        {
            // An arrow block is parsed as a ListNode whose ElementNodes are run
            // as STATEMENTS (the visitor's `(ListNode)c.Body` path); the arm
            // value is null. A Scope is handled the same way (defensive).
            if (body.NodeType == AstNodeType.List)
            {
                st.Code.Emit3(Opcode.LoadNull, destSlot, 0, 0);
                var lst = (Parser.Nodes.Primitives.ListNode)body;
                foreach (var stmt in lst.ElementNodes)
                    if (!TryCompileStatement(stmt, st, ref topSlot, bodyScratch, strict: true))
                        throw new IrCompileException($"switch arrow-block stmt not compilable: {stmt.NodeType}");
            }
            else if (body.NodeType == AstNodeType.Scope)
            {
                st.Code.Emit3(Opcode.LoadNull, destSlot, 0, 0);
                CompileBodyStrictInline(body, st, ref topSlot, bodyScratch);
            }
            else
            {
                CompileExpression(body, destSlot, st, ref topSlot);
            }
        }

        // L7: conservative — does this arrow-block arm body contain a control
        // ESCAPE (`yield` / `break` / `continue` / `return`) reachable in THIS
        // arm (not inside a nested function / generator / loop)? The visitor
        // special-cases each (yield sets the switch value; break exits with null;
        // continue/return propagate) with inconsistent semantics we don't
        // replicate — so any escape → fall back to the visitor. Returns true for
        // an escape or any node type we can't prove escape-free; only PURE
        // side-effect blocks (calls / assignments / decls) lower. Nested
        // fn/type/loop bodies own their own break/continue/yield scope and are
        // NOT descended.
        private static bool ArrowBlockHasControlEscape(AstNode? node)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.Yield:
                case AstNodeType.Break:
                case AstNodeType.Continue:
                case AstNodeType.Return:
                    return true;
                // Own break/continue/yield scope — escapes inside are not this arm's.
                case AstNodeType.FunctionDefinition:
                case AstNodeType.ClassDefinition:
                case AstNodeType.StructDefinition:
                case AstNodeType.RecordDefinition:
                case AstNodeType.EnumDefinition:
                case AstNodeType.For:
                case AstNodeType.ForEach:
                case AstNodeType.While:
                case AstNodeType.DoWhile:
                case AstNodeType.SuperFor:
                    return false;
                // Definitely escape-free leaves / expression statements.
                case AstNodeType.Number:
                case AstNodeType.String:
                case AstNodeType.Boolean:
                case AstNodeType.Null:
                case AstNodeType.VariableAccess:
                case AstNodeType.Pass:
                case AstNodeType.Throw:
                case AstNodeType.FunctionCall:
                case AstNodeType.VariableAssignment:
                case AstNodeType.MemberAssignment:
                case AstNodeType.VariableDeclaration:
                    return false;
                case AstNodeType.Scope:
                {
                    var sn = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var n in sn.Nodes)
                        if (ArrowBlockHasControlEscape(n)) return true;
                    return false;
                }
                case AstNodeType.List:
                {
                    // The arrow-block container itself (its elements are stmts).
                    var ln = (Parser.Nodes.Primitives.ListNode)node;
                    foreach (var e in ln.ElementNodes)
                        if (ArrowBlockHasControlEscape(e)) return true;
                    return false;
                }
                // Conservative: an unknown structure (if / try / nested switch /
                // …) might hide an escape → fall back.
                default:
                    return true;
            }
        }

        // Returns true / false when the node is a compile-time constant
        // condition that evaluates trivially. Returns null otherwise (caller
        // must emit runtime evaluation).
        //   - `true` / `false` literal → boolean truthiness
        //   - `null` literal → falsy
        //   - bare integer literal → truthy if non-zero, falsy if zero
        // Everything else (variable reads, function calls, complex
        // expressions) returns null because side effects + dynamic typing
        // forbid the elision.
        private static bool? TryFoldCondition(AstNode node)
        {
            switch (node.NodeType)
            {
                case AstNodeType.Boolean:
                {
                    var bn = (Parser.Nodes.Primitives.BooleanNode)node;
                    if (bn.Token.Value is Keyword k)
                    {
                        if (k == Keyword.True) return true;
                        if (k == Keyword.False) return false;
                    }
                    return null;
                }
                case AstNodeType.Null:
                    return false;
                case AstNodeType.Number:
                {
                    var nn = (NumberNode)node;
                    var raw = nn.Tok.Value?.ToString() ?? "";
                    if (raw.Length == 0) return null;
                    // Reject suffixed / base-prefixed literals; only plain
                    // decimal int / float is safe to fold here.
                    if (raw[0] == '0' && raw.Length >= 2
                        && (raw[1] == 'x' || raw[1] == 'X' || raw[1] == 'b' || raw[1] == 'B'
                            || raw[1] == 'o' || raw[1] == 'O'))
                        return null;
                    char last = raw[raw.Length - 1];
                    if ((last >= 'a' && last <= 'z') || (last >= 'A' && last <= 'Z')) return null;
                    foreach (char c in raw)
                        if (c != '0' && c != '.' && c != '-' && c != '+') return true;
                    return false;
                }
                default:
                    return null;
            }
        }

        // While: PushScope ; loop_top: ClearScope ; cond ; JmpIfNot exit ;
        // body strict ; Jmp loop_top ; exit: PopScope.
        // Continue → jump to loop_top (after the body's pops). Break → jump
        // to exit (loop's pop runs naturally).
        private static void CompileWhile(WhileNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            // M21.1: pre-check dropped — strict-mode CompileBodyStrictInline
            // raises IrCompileException directly when a body child genuinely
            // cannot lower, and the outer CompileStatementWithFallback rolls
            // back and routes the whole While through OP_NATIVE_DEFINE.
            // M22.1: constant-fold a literal condition. `while false {}`
            // emits nothing; `while true {}` drops the cond + exit jump.
            bool? whileFolded = TryFoldCondition(node.ConditionNode);
            if (whileFolded == false) return;

            // M87: lazy-long while counter. Detect the C-style counter
            // pattern `var i = 0; while i ⋈ N { ... i = i ± step; }` and
            // lower it through the same typed-Int64 machinery as
            // `CompileForLazyLong`. Catches `bench_while.ra` /
            // `bench_branchy.ra` whose previous boxed dispatch paid
            // O(1M) NumberValue allocations per run.
            if (whileFolded != true
                && TryCompileWhileLazyLongCounter(node, st, ref topSlot, scratchSlot))
                return;

            // Skip scope plumbing entirely when the body introduces no
            // bindings. Saves PushScope + per-iter ClearScope + PopScope.
            bool whileBodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (whileBodyNeedsScope)
                EmitPushScope(st);
            int baselineDepth = st.ScopeDepth;
            int loopTopPc = st.Code.Pc;

            // ClearScope at the top of each iter mirrors bodySymbols.Clear()
            // in WhileNodeVisitor — drops any locals declared in the
            // previous iteration before re-evaluating the condition.
            if (whileBodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            int exitJmp = -1;
            if (whileFolded != true)
            {
                byte condSlot = AllocTemp(ref topSlot);
                CompileExpression(node.ConditionNode, condSlot, st, ref topSlot);
                exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot);
            }

            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
                st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);
            }
            finally
            {
                st.Loops.Pop();
            }

            if (exitJmp >= 0) st.Code.PatchJumpToHere(exitJmp);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, loopTopPc);

            if (whileBodyNeedsScope)
                EmitPopScope(st);
            // Body may have mutated typed accumulators through paths
            // we don't statically model (variable iteration count, break/
            // continue). Conservative re-dirty.
            MarkAllTypedAccsDirty(st);
        }

        // M87: C-style counter detection. Returns true when `node` matches
        //   var i = expr_init;     // declared in outer scope
        //   while i ⋈ end { body; ... i = i ± step_lit; }
        //
        // and `i`, `end` (if a binding), `step_lit` all satisfy:
        //   * `i` is a slot-eligible local with a resolved BindingId.
        //   * `i` is NOT re-declared inside `body` (no shadowing).
        //   * Every write to `i` in `body` is a self-additive
        //     `i = i ± literal_long` (multiple writes across `if` branches
        //     are OK as long as each is the redirectable shape).
        //   * `cmpOp` is one of `<`, `<=`, `>`, `>=`, `==`, `!=`.
        //   * `end` is either a constant-foldable int64 literal OR a
        //     never-mutated slot-eligible local binding.
        //
        // When the pattern matches, the heavy lifting is delegated to
        // `CompileWhileLazyLongCounter` which mirrors `CompileForLazyLong`'s
        // typed-promotion plumbing (`TypedAccumulators`,
        // `TypedAccumulatorLiterals`, `TypedAccumulatorExprs`,
        // `TypedLongBindings`) but adapted to the while form where the
        // iter binding lives in the outer scope and the body owns the
        // advance.
        private static bool TryCompileWhileLazyLongCounter(
            WhileNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            if (node.ConditionNode is not Parser.Nodes.Operations.BinaryOperationNode cond)
                return false;
            if (!IsTypedComparableOp(cond.OpTok.Type)) return false;

            Parser.Nodes.Variables.VariableAccessNode? iterAccess;
            AstNode endNode;
            bool iterOnLeft;
            if (cond.LeftNode is Parser.Nodes.Variables.VariableAccessNode lva
                && !string.IsNullOrEmpty(lva.Name)
                && lva.Binding.IsResolved)
            {
                iterAccess = lva;
                endNode = cond.RightNode;
                iterOnLeft = true;
            }
            else if (cond.RightNode is Parser.Nodes.Variables.VariableAccessNode rva
                && !string.IsNullOrEmpty(rva.Name)
                && rva.Binding.IsResolved)
            {
                iterAccess = rva;
                endNode = cond.LeftNode;
                iterOnLeft = false;
            }
            else
            {
                return false;
            }

            string iterName = iterAccess.Name;
            var iterBinding = iterAccess.Binding;
            var iterKind = iterAccess.BindingKind;
            // Soundness gate: the counter must be provably numeric to be
            // UnboxI-promoted into a typed Int64 slot. A non-numeric (e.g.
            // string) `i` would unbox to 0 and corrupt the loop.
            if (!st.NumericInitBindings.Contains(iterName)) return false;
            if (!IsSlotEligible(iterBinding, iterKind, st)) return false;
            if (iterBinding.Offset > ushort.MaxValue) return false;
            if (BodyDeclaresName(node.BodyNode, iterName)) return false;

            // Body must contain at least one redirectable iter advance,
            // and every assignment to iter must be redirectable.
            if (!HasAnyAssignmentTo(node.BodyNode, iterName)) return false;
            if (HasNonRedirectableIterAdvance(node.BodyNode, iterName)) return false;
            // Iter must not be reassigned via the condition expression.
            if (HasAnyAssignmentTo(node.ConditionNode, iterName)) return false;

            bool endIsLiteral = TryGetLiteralLongFromConstExpr(endNode, out long endLit);
            Parser.Nodes.Variables.VariableAccessNode? endBindingNode = null;
            if (!endIsLiteral)
            {
                if (endNode is not Parser.Nodes.Variables.VariableAccessNode evn) return false;
                if (string.IsNullOrEmpty(evn.Name)) return false;
                if (evn.Name == iterName) return false;
                if (!evn.Binding.IsResolved) return false;
                if (evn.Binding.Offset > ushort.MaxValue) return false;
                if (!IsSlotEligible(evn.Binding, BindingKind.Local, st)
                    && !IsSlotEligible(evn.Binding, BindingKind.Global, st)
                    && !IsSlotEligible(evn.Binding, BindingKind.Parameter, st))
                    return false;
                if (HasAnyAssignmentTo(node.BodyNode, evn.Name)) return false;
                if (HasAnyAssignmentTo(node.ConditionNode, evn.Name)) return false;
                endBindingNode = evn;
            }

            CompileWhileLazyLongCounter(
                node, iterName, iterBinding, cond, iterOnLeft,
                endIsLiteral, endLit, endBindingNode,
                st, ref topSlot, scratchSlot);
            return true;
        }

        // Walks `node` and returns true when there's any write to `iterName`
        // that is NOT a typed-redirectable self-additive `iter = iter ± lit`.
        // Used by `TryCompileWhileLazyLongCounter` to gate the typed
        // promotion — anything more complex than the canonical counter
        // shape falls back to the boxed while compile.
        private static bool HasNonRedirectableIterAdvance(AstNode? node, string iterName)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.Name == iterName)
                    {
                        if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return true;
                        if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return true;
                        if (bo.OpTok.Type != Lexer.Tokens.TokenType.PLUS
                            && bo.OpTok.Type != Lexer.Tokens.TokenType.MINUS) return true;
                        if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return true;
                        if (lvn.Name != iterName) return true;
                        if (!TryGetLiteralLongFromConstExpr(bo.RightNode, out _)) return true;
                        // Defensively descend into RHS in case it contains
                        // a side-effecting iter write (extremely unlikely
                        // given the literal predicate, but cheap).
                        return HasNonRedirectableIterAdvance(bo.RightNode, iterName);
                    }
                    return HasNonRedirectableIterAdvance(va.ValueNode, iterName);
                }
                case AstNodeType.VariableDeclaration:
                {
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    foreach (var d in vd.Declarations)
                    {
                        if (d.Item1.Value?.ToString() == iterName) return true;
                        if (d.Item2 != null && HasNonRedirectableIterAdvance(d.Item2, iterName)) return true;
                    }
                    return false;
                }
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        if (HasNonRedirectableIterAdvance(c, iterName)) return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (HasNonRedirectableIterAdvance(cs.Condition, iterName)) return true;
                        if (HasNonRedirectableIterAdvance(cs.Expr, iterName)) return true;
                    }
                    if (ifn.ElseCase.HasValue
                        && HasNonRedirectableIterAdvance(ifn.ElseCase.Value.Expr, iterName)) return true;
                    return false;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    return HasNonRedirectableIterAdvance(bo.LeftNode, iterName)
                        || HasNonRedirectableIterAdvance(bo.RightNode, iterName);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    // `i++` / `i--` are non-redirectable (would need a
                    // separate compile path; the typed-acc machinery only
                    // covers `i = i ± lit`).
                    if ((uo.OpTok.Type == Lexer.Tokens.TokenType.DOUBLE_PLUS
                         || uo.OpTok.Type == Lexer.Tokens.TokenType.DOUBLE_MINUS)
                        && uo.Node is Parser.Nodes.Variables.VariableAccessNode vau
                        && vau.Name == iterName)
                        return true;
                    return HasNonRedirectableIterAdvance(uo.Node, iterName);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    if (HasNonRedirectableIterAdvance(fc.NodeToCall, iterName)) return true;
                    foreach (var arg in fc.ArgNodes)
                        if (HasNonRedirectableIterAdvance(arg.Expr, iterName)) return true;
                    return false;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    return HasNonRedirectableIterAdvance(tn.Condition, iterName)
                        || HasNonRedirectableIterAdvance(tn.TrueExpression, iterName)
                        || HasNonRedirectableIterAdvance(tn.FalseExpression, iterName);
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    return rn.NodeToReturn != null
                        && HasNonRedirectableIterAdvance(rn.NodeToReturn, iterName);
                }
                // Nested loops / function defs / try-catch can write to
                // iter via paths the simple shape analysis above can't
                // verify. Fall back conservatively.
                case AstNodeType.While:
                case AstNodeType.DoWhile:
                case AstNodeType.For:
                case AstNodeType.ForEach:
                case AstNodeType.FunctionDefinition:
                case AstNodeType.Try:
                    return HasAnyAssignmentTo(node, iterName);
                default:
                    return false;
            }
        }

        // Lazy-long while counter. Mirrors `CompileForLazyLong`'s
        // typed-promotion shape, with two key differences:
        //   1. The iter binding lives in the OUTER scope (declared by a
        //      preceding `var i = …;`), so the pre-loop reads its
        //      current boxed value into a typed slot and the post-loop
        //      writes it back via `BoxI + StoreLocalS`.
        //   2. The body owns the advance — `i = i ± lit` is part of the
        //      user code, so the iter is registered in BOTH
        //      `ActiveTypedIters` (for comparison redirects) AND
        //      `TypedAccumulators` (for self-additive redirects via
        //      `TryEmitSelfAdditiveSlot`).
        //
        // Layout:
        //   <LoadLocalS iter_boxed_tmp, iter_binding; UnboxI iter_long, iter_boxed_tmp>
        //   <pre-load end (literal or never-mutated binding)>
        //   <pre-load typed accumulator candidates (sum, count, …)>
        //   <pre-load TypedAccumulatorLiterals (every distinct int64 literal
        //    appearing as accumulator-RHS or comparison-operand)>
        //   <pre-load TypedAccumulatorExprs / TypedLongBindings as for-loop does>
        //   PushScope (body) if needed
        //   loop_top:
        //     ClearScope (if body needs scope)
        //     cond  → typed II compare (LtII / GtII / EqII / …)
        //     JmpIfNot exit
        //     <body — every self-additive is now AddII / SubII;
        //      every typed-iter comparison is *II>
        //     Jmp loop_top
        //   exit:
        //     <BoxI + StoreLocalS for every TypedAccumulator including iter>
        //   PopScope (body) if needed
        private static void CompileWhileLazyLongCounter(
            WhileNode node, string iterName, BindingId iterBinding,
            Parser.Nodes.Operations.BinaryOperationNode condNode, bool iterOnLeft,
            bool endIsLiteral, long endLit,
            Parser.Nodes.Variables.VariableAccessNode? endBindingNode,
            State st, ref byte topSlot, byte scratchSlot)
        {
            // M87: snapshot for nested-loop containment. The outer
            // loop's typed-promotion dictionaries survive this compile
            // unchanged.
            var snapshot = new TypedPromotionSnapshot(st);

            // M88 (#29): conditional preheader. Evaluate the loop
            // condition ONCE up front (via the existing boxed dispatch
            // — typed slots aren't set up yet) and jump past the
            // entire typed-promotion scaffold + body when the body
            // wouldn't run. With the body guaranteed to run on the
            // in-loop side, `IsLoopInvariantPureNumericExpr` can admit
            // Div / Mod / Pow RHS shapes safely — any error their
            // pre-load evaluation raises is one the original boxed
            // dispatch would also raise on iteration 0 (same source
            // PC, same diagnostic). The post-loop box-back is INSIDE
            // the guarded region so an empty loop leaves the iter /
            // accumulator SymbolEntry values intact.
            byte preCondSlot = AllocTemp(ref topSlot);
            CompileExpression(node.ConditionNode, preCondSlot, st, ref topSlot);
            int skipAllJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, preCondSlot);
            bool prevWcGuaranteed = st.LoopGuaranteedToEnter;
            st.LoopGuaranteedToEnter = true;

            // Pre-loop: unbox iter into typed slot. Reading the boxed
            // mirror BEFORE registering iter in any typed dict — otherwise
            // a recursive typed redirect would try to use the slot before
            // it's been initialised.
            byte iterLong = AllocTemp(ref topSlot);
            byte iterBoxedTmp = AllocTemp(ref topSlot);
            st.Code.Emit2(Opcode.LoadLocalS, iterBoxedTmp, (ushort)iterBinding.Offset);
            st.Code.Emit3(Opcode.UnboxI, iterLong, iterBoxedTmp, 0);

            // Register iter in both maps so:
            //   * `TryEmitSelfAdditiveSlot` redirects `i = i + lit` to
            //     AddII against `iterLong` (typed-acc Path 2).
            //   * Typed comparison redirects pick up `iter ⋈ X` shapes.
            //   * Boxed reads of iter (e.g. `print(i)`) publish through
            //     the `DirtyTypedAccs` machinery in `VariableAccess`.
            st.ActiveTypedIters[iterName] = iterLong;
            st.TypedAccumulators[iterName] = (iterLong, iterBinding);

            // Collect non-iter typed accumulators (sum, count, etc.) the
            // body would benefit from promoting. `CollectTypedAccumulatorCandidates`
            // applies its own redirectable-write gate.
            var typedAccCandidates = CollectTypedAccumulatorCandidates(node.BodyNode, iterName, st);
            // Strip iter (we've already registered it manually so its
            // SymbolEntry → typed slot read happens up here, not via the
            // candidate-loop's pre-load below).
            typedAccCandidates.RemoveAll(c => c.Name == iterName);

            foreach (var acc in typedAccCandidates)
            {
                if (acc.Binding.Offset > ushort.MaxValue) continue;
                byte accLong = AllocTemp(ref topSlot);
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, accBoxedTmp, (ushort)acc.Binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, accLong, accBoxedTmp, 0);
                st.TypedAccumulators[acc.Name] = (accLong, acc.Binding);
            }

            // Literal pre-load: union of literals used by typed acc
            // self-additives (including iter's own advance) + all typed
            // iter comparison sites in BOTH the condition AND the body.
            var accNamesAll = new HashSet<string>();
            accNamesAll.Add(iterName);
            foreach (var acc in typedAccCandidates) accNamesAll.Add(acc.Name);

            var litValuesAll = new HashSet<long>();
            CollectAccumulatorLiteralRhsValues(node.BodyNode, accNamesAll, litValuesAll);
            CollectIterComparisonLiterals(node.BodyNode, iterName, litValuesAll);
            CollectIterComparisonLiterals(node.ConditionNode, iterName, litValuesAll);
            // M87: capture every constant-int int64 literal anywhere in
            // body / condition so the generalised typed-Int64 redirect
            // (`(i & 1) == 0`, `(i + 5) % N`, …) can pull operands
            // straight out of pre-loaded typed slots.
            CollectAllConstIntLiterals(node.BodyNode, litValuesAll);
            CollectAllConstIntLiterals(node.ConditionNode, litValuesAll);
            if (endIsLiteral) litValuesAll.Add(endLit);

            foreach (var lit in litValuesAll)
            {
                byte litSlot = AllocTemp(ref topSlot);
                EmitLiteralLongLoad(lit, litSlot, st, ref topSlot);
                st.TypedAccumulatorLiterals[lit] = litSlot;
            }

            // Loop-invariant pure-expression RHS pre-load — same path as
            // `CompileForLazyLong`. Only non-iter accumulators are
            // candidates here; iter's RHS is always a literal so it's
            // already covered by the literal pre-load above.
            if (typedAccCandidates.Count > 0)
            {
                var accNamesNonIter = new HashSet<string>();
                foreach (var acc in typedAccCandidates) accNamesNonIter.Add(acc.Name);
                CollectAccumulatorLoopInvariantExprs(
                    node.BodyNode, node.BodyNode, iterName, accNamesNonIter, st, ref topSlot);
            }

            // Typed-long bindings: comparison-binding-names in body +
            // condition + the end binding (when end is not a literal).
            var bindingNamesAll = new HashSet<string>();
            CollectIterComparisonBindingNames(node.BodyNode, iterName, bindingNamesAll);
            CollectIterComparisonBindingNames(node.ConditionNode, iterName, bindingNamesAll);
            if (endBindingNode != null) bindingNamesAll.Add(endBindingNode.Name);
            foreach (var nm in bindingNamesAll)
            {
                if (st.ActiveTypedIters.ContainsKey(nm)) continue;
                if (st.TypedAccumulators.ContainsKey(nm)) continue;
                if (st.TypedLongBindings.ContainsKey(nm)) continue;
                if (HasAnyAssignmentTo(node.BodyNode, nm)) continue;
                if (HasAnyAssignmentTo(node.ConditionNode, nm)) continue;
                BindingId binding = FindFirstBindingOfName(node.BodyNode, nm);
                if (!binding.IsResolved && endBindingNode != null && endBindingNode.Name == nm)
                    binding = endBindingNode.Binding;
                if (!binding.IsResolved && condNode != null)
                    binding = FindFirstBindingOfName(condNode, nm);
                if (!binding.IsResolved) continue;
                if (binding.Offset > ushort.MaxValue) continue;
                if (!IsSlotEligible(binding, BindingKind.Local, st)
                    && !IsSlotEligible(binding, BindingKind.Global, st)
                    && !IsSlotEligible(binding, BindingKind.Parameter, st))
                    continue;
                byte bndLong = AllocTemp(ref topSlot);
                byte bndBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, bndBoxedTmp, (ushort)binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, bndLong, bndBoxedTmp, 0);
                st.TypedLongBindings[nm] = (bndLong, binding);
            }

            // Loop scaffold.
            bool bodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (bodyNeedsScope)
                EmitPushScope(st);
            int baselineDepth = st.ScopeDepth;
            int loopTopPc = st.Code.Pc;
            if (bodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            // Back-edge correctness: the body is compiled ONCE but runs many
            // times. A typed iter / accumulator advanced near the body's END
            // is already dirty (its boxed mirror stale) on RE-ENTRY to the
            // body top from iteration 2 onward. The lazy publish-on-read
            // (CompileExpression VariableAccess) only emits a BoxI+StoreLocalS
            // when the name is in DirtyTypedAccs at compile time — so a
            // non-redirectable read positioned BEFORE the advance (e.g.
            // `while i<n { print(i); i=i+1 }`) would never get a publish and
            // would read the stale pre-loop boxed value every iteration.
            // Seed the dirty set at body-top so the first such read publishes
            // the current typed value; that publish instruction then executes
            // every iteration. Loops with no non-redirectable reads emit no
            // extra publishes — zero cost on the hot numeric paths.
            foreach (var __k in new List<string>(st.TypedAccumulators.Keys))
                st.DirtyTypedAccs.Add(__k);

            // Compile the condition normally. The typed-Int64 / typed-iter
            // redirects in `CompileExpression` will lower it to a single
            // *II compare reading from the iter typed slot + pre-loaded
            // literal / typed binding slot — zero per-iter alloc.
            byte condSlot = AllocTemp(ref topSlot);
            CompileExpression(node.ConditionNode, condSlot, st, ref topSlot);
            int exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot);

            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
                st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);
            }
            finally
            {
                st.Loops.Pop();
            }

            st.Code.PatchJumpToHere(exitJmp);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, loopTopPc);

            // Post-loop: box every typed accumulator (iter included) back
            // to its SymbolEntry slot so downstream `print(sum)` /
            // `print(i)` reads see the freshly-computed values. ONLY
            // emit the box-back for entries we ourselves added (i.e.
            // entries not present in the entry snapshot).
            var accumulatorsToBox = new List<KeyValuePair<string, (byte LongSlot, BindingId Binding)>>(
                st.TypedAccumulators);
            foreach (var kvp in accumulatorsToBox)
            {
                if (snapshot.TypedAccumulators.ContainsKey(kvp.Key)) continue;
                if (kvp.Value.Binding.Offset > ushort.MaxValue) continue;
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.BoxI, accBoxedTmp, kvp.Value.LongSlot, 0);
                st.Code.Emit2(Opcode.StoreLocalS, accBoxedTmp, (ushort)kvp.Value.Binding.Offset);
            }
            // Restore the entry-snapshot of every typed-promotion dict so
            // the OUTER lazy-counter compile keeps the state it had at
            // entry.
            snapshot.RestoreInto(st);
            // M88: restore the guaranteed-enter flag.
            st.LoopGuaranteedToEnter = prevWcGuaranteed;

            if (bodyNeedsScope)
                EmitPopScope(st);

            // M88: patch the preheader skip target. Falls through here
            // when the original (pre-evaluated) condition was already
            // false, leaving every SymbolEntry untouched.
            st.Code.PatchJumpToHere(skipAllJmp);
        }

        // Walks `node` collecting every constant-foldable int64 literal
        // value into `outValues`. Used by the M87 typed-Int64 redirect
        // pre-load so even literals that appear OUTSIDE accumulator
        // self-additives / iter comparisons (e.g. inside `(i + 5) % N`
        // sub-trees) end up in `TypedAccumulatorLiterals` and can be
        // picked up by `EmitTypedInt64Operand`'s `Number` case without
        // re-emitting a `LoadIntS64` per iteration.
        private static void CollectAllConstIntLiterals(AstNode? node, HashSet<long> outValues)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                    if (TryGetLiteralLongFromConstExpr(node, out long v)) outValues.Add(v);
                    return;
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectAllConstIntLiterals(c, outValues);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectAllConstIntLiterals(cs.Condition, outValues);
                        CollectAllConstIntLiterals(cs.Expr, outValues);
                    }
                    if (ifn.ElseCase.HasValue)
                        CollectAllConstIntLiterals(ifn.ElseCase.Value.Expr, outValues);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    CollectAllConstIntLiterals(va.ValueNode, outValues);
                    return;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    CollectAllConstIntLiterals(bo.LeftNode, outValues);
                    CollectAllConstIntLiterals(bo.RightNode, outValues);
                    return;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    CollectAllConstIntLiterals(uo.Node, outValues);
                    return;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    CollectAllConstIntLiterals(tn.Condition, outValues);
                    CollectAllConstIntLiterals(tn.TrueExpression, outValues);
                    CollectAllConstIntLiterals(tn.FalseExpression, outValues);
                    return;
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    CollectAllConstIntLiterals(fc.NodeToCall, outValues);
                    foreach (var arg in fc.ArgNodes)
                        CollectAllConstIntLiterals(arg.Expr, outValues);
                    return;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    if (rn.NodeToReturn != null)
                        CollectAllConstIntLiterals(rn.NodeToReturn, outValues);
                    return;
                }
                default:
                    return;
            }
        }

        // DoWhile: PushScope ; loop_top: ClearScope ; body ; continue_target:
        // cond ; JmpIf loop_top ; exit: PopScope.
        // Break → exit. Continue → continue_target.
        private static void CompileDoWhile(DoWhileNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            // M21.1: pre-check dropped — see CompileWhile rationale.
            bool dwBodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (dwBodyNeedsScope)
                EmitPushScope(st);
            int baselineDepth = st.ScopeDepth;
            int loopTopPc = st.Code.Pc;
            if (dwBodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            int continueTargetPc = st.Code.Pc;

            byte condSlot = AllocTemp(ref topSlot);
            CompileExpression(node.ConditionNode, condSlot, st, ref topSlot);
            st.Code.EmitBackwardJump(Opcode.JmpIf, condSlot, loopTopPc);

            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, continueTargetPc);

            if (dwBodyNeedsScope)
                EmitPopScope(st);
            MarkAllTypedAccsDirty(st);
        }

        // For loop: iter scope (holds iter var) + body scope (cleared each
        // iter). Mirrors ForNodeVisitor.cs lines 13-78.
        //
        //   <compute start, end, step into temp slots>
        //   PushScope (iter scope)
        //   SetLocalDirect iterName, iterSlot    # initialize iter var = start
        //   <copy start into iterValSlot>        # VM-side counter (avoids body mutating iter sequence)
        //   PushScope (body scope)
        //   loop_top:
        //     ClearScope                          # drop body locals
        //     <test step >= 0 ? iter < end : iter > end>
        //     JmpIfNot exit_outer
        //     AssignBinding iterName, iterValSlot # publish current iter value
        //     <body strict>
        //     iterValSlot = iterValSlot + step
        //     Jmp loop_top
        //   exit_outer:
        //   PopScope (body)
        //   PopScope (iter)
        // L1: C-style `for (init…; cond…; step…) { body }` (SuperForNode).
        // Lowered to the same Jmp / compare / scope primitives the `to`-form
        // For and While already use — zero new opcodes. Mirrors
        // SuperForNodeVisitor exactly:
        //
        //   loopContext = ctx.Copy()           → PushScope (iter scope)
        //   for each init: eval in loopContext
        //   bodyContext = loopContext.Copy()   → PushScope (body scope, if any)
        //   while (every condition is true):   → cond_i; JmpIfNot exit (AND)
        //     <body>                           → CompileBodyStrictInline
        //     <steps>                          → eval in loopContext
        //
        // Layout:
        //   PushScope                ; iter scope (init vars persist)
        //   <inits>
        //   [PushScope]              ; body scope (only when body declares)
        // loopTop:
        //   <cond_i; JmpIfNot exit>* ; empty list ⇒ infinite loop
        //   <body>
        // continueTarget:           ; `continue` lands here (runs the steps)
        //   [ClearScope]             ; drop body locals BEFORE the steps, so a
        //                            ; body shadow of an iter var cannot capture
        //                            ; the step's write — matching the visitor
        //                            ; running steps in loopContext (NOT
        //                            ; bodyContext). Without this, a `var i` in
        //                            ; the body would make `i = i + 1` mutate
        //                            ; the shadow and loop forever.
        //   <steps>
        //   Jmp loopTop
        // exit:
        //   [PopScope]               ; body scope
        //   PopScope                 ; iter scope
        private static void CompileSuperFor(Parser.Nodes.Statements.SuperForNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            st.RecordPcSpan(node);

            EmitPushScope(st); // iter scope (loopContext) — holds the init vars

            foreach (var initNode in node.InitializationNodes)
            {
                st.RecordPcSpan(initNode);
                if (!TryCompileStatement(initNode, st, ref topSlot, scratchSlot, strict: true))
                    throw new IrCompileException($"super-for init not compilable: {initNode.NodeType}");
            }

            bool bodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (bodyNeedsScope)
                EmitPushScope(st); // body scope (bodyContext)
            int baselineDepth = st.ScopeDepth;

            int loopTopPc = st.Code.Pc;

            // Conditions: logical AND — every one must hold to enter the body.
            // An empty condition list leaves no exit test ⇒ `for(;;)` is an
            // infinite loop (matches the visitor's `canContinue` default).
            var condExitFixups = new List<int>();
            foreach (var condNode in node.ConditionNodes)
            {
                st.RecordPcSpan(condNode);
                byte condSlot = AllocTemp(ref topSlot);
                CompileExpression(condNode, condSlot, st, ref topSlot);
                condExitFixups.Add(st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot));
            }

            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);

                // continue target = clear-body-locals + steps. Patch the body's
                // forward `continue` jumps to HERE (Code.Pc == this point).
                foreach (var p in loop.ContinueFixups) st.Code.PatchJumpToHere(p);
                if (bodyNeedsScope)
                    st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);
                foreach (var stepNode in node.StepNodes)
                {
                    st.RecordPcSpan(stepNode);
                    if (!TryCompileStatement(stepNode, st, ref topSlot, scratchSlot, strict: true))
                        throw new IrCompileException($"super-for step not compilable: {stepNode.NodeType}");
                }
                st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);
            }
            finally
            {
                st.Loops.Pop();
            }

            foreach (var p in condExitFixups) st.Code.PatchJumpToHere(p);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);

            if (bodyNeedsScope)
                EmitPopScope(st); // body scope
            EmitPopScope(st); // iter scope
            MarkAllTypedAccsDirty(st);
        }

        private static void CompileFor(ForNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            // M21.1: pre-checks dropped — see CompileWhile rationale.
            string iterName = node.VarNameTok.Value!.ToString()!;
            ushort iterNameIdx = st.Names.Add(iterName);

            // Fast path: literal int bounds with default (or literal) step.
            // Mirrors `CompileForEachLazyIntRange` — the iter counter stays
            // in a typed Int64 slot for the whole loop, so the dispatch
            // hot path becomes LtII + JmpIfNot + AddII + Jmp (zero boxed
            // allocations per iter). Boxing happens only when the body
            // actually reads `i` (BoxI + AssignBinding before each
            // iteration). Catches the overwhelmingly dominant numeric-loop
            // shape `for i = 0 to N` / `for i = lo to hi step k`.
            if (TryGetLiteralLong(node.StartValueNode, out long startLit)
                && TryGetLiteralLong(node.EndValueNode, out long endLit)
                && (node.StepValueNode == null
                    || TryGetLiteralLong(node.StepValueNode, out _)))
            {
                long stepLit = 1;
                if (node.StepValueNode != null) TryGetLiteralLong(node.StepValueNode, out stepLit);
                if (stepLit != 0) // step==0 falls through to the dynamic boxed path so the original error surfaces
                {
                    CompileForLazyLong(node, iterName, iterNameIdx,
                        startLit, endLit, stepLit, st, ref topSlot, scratchSlot);
                    return;
                }
            }

            // Compute bounds into temp slots BEFORE pushing scopes (so the
            // expressions execute in the outer scope, matching the AST
            // visitor where bounds are evaluated in loopContext after Copy
            // but BEFORE the body context is allocated).
            //
            // Allocate temps in a way that survives the body's own temp
            // bumping — bump topSlot now and pin these.
            byte startSlot = AllocTemp(ref topSlot);
            CompileExpression(node.StartValueNode, startSlot, st, ref topSlot);

            byte endSlot = AllocTemp(ref topSlot);
            CompileExpression(node.EndValueNode, endSlot, st, ref topSlot);

            byte stepSlot = AllocTemp(ref topSlot);
            // Statically classify the step's sign. When the step is null
            // (default 1) or a literal constant, the asc/desc branch is
            // dead code — emit a single compare. Saves ~5 opcodes per
            // iter (LoadConst zero, Ge step≥0, JmpIfNot, dead Gt, trailing
            // Jmp) in the common `for i = 0 to N` shape, which is the
            // overwhelmingly dominant numeric-loop form.
            //   stepStatic:  1 → ascending, -1 → descending, 0 → unknown.
            int stepStatic = 0;
            if (node.StepValueNode == null)
            {
                // Step defaults to NumberValue.One — always ascending.
                ushort oneIdx = st.Consts.Add(NumberValue.One);
                st.Code.Emit2(Opcode.LoadConst, stepSlot, oneIdx);
                stepStatic = 1;
            }
            else
            {
                CompileExpression(node.StepValueNode, stepSlot, st, ref topSlot);
                if (TryConstEvalNumber(node.StepValueNode, out var stepNV))
                {
                    var u = stepNV.Value.Unscaled;
                    if (stepNV.Value.Scale.IsZero && u.Sign > 0) stepStatic = 1;
                    else if (stepNV.Value.Scale.IsZero && u.Sign < 0) stepStatic = -1;
                    // Sign==0 (literal step=0) keeps stepStatic=0 → falls
                    // through to dynamic dispatch so the runtime can
                    // surface the divergence the AST visitor would.
                }
            }

            // VM-side iterator counter — body mutations to the iter binding
            // do not affect this slot.
            byte iterValSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.Move, iterValSlot, startSlot, 0);

            EmitPushScope(st); // iter scope
            st.Code.Emit2(Opcode.SetLocalDirect, startSlot, iterNameIdx);

            // Body scope + per-iter ClearScope only when the body introduces
            // its own bindings (var/let/const/final, nested fn/class/struct/
            // enum/trait/interface/extension/annotation, import, namespace,
            // using). Side-effect-only bodies (`pass`, `print(...)`, plain
            // arithmetic reads) pay nothing — two PushScope opcodes, one
            // PopScope pair, and a per-iter ClearScope dictionary walk all
            // vanish. Common-case loop overhead drops to LtII + JmpIfNot +
            // Add + Jmp.
            bool bodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (bodyNeedsScope)
                EmitPushScope(st); // body scope
            int baselineDepth = st.ScopeDepth;

            // Hoist loop-invariant compute outside the loop body. The
            // dynamic-step path needs a `step >= 0` flag computed once
            // before loop_top.
            byte stepNonNegOuter = 0;
            if (stepStatic == 0)
            {
                ushort zeroIdxOuter = st.Consts.Add(NumberValue.Zero);
                byte zeroSlotOuter = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadConst, zeroSlotOuter, zeroIdxOuter);
                stepNonNegOuter = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.Ge, stepNonNegOuter, stepSlot, zeroSlotOuter);
            }

            int loopTopPc = st.Code.Pc;
            if (bodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            byte cmpAsc = AllocTemp(ref topSlot);
            int exitJmp;
            if (stepStatic > 0)
            {
                // Ascending only: iter < end.
                st.Code.Emit3(Opcode.Lt, cmpAsc, iterValSlot, endSlot);
                exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpAsc);
            }
            else if (stepStatic < 0)
            {
                // Descending only: iter > end.
                st.Code.Emit3(Opcode.Gt, cmpAsc, iterValSlot, endSlot);
                exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpAsc);
            }
            else
            {
                // Dynamic: if stepNonNegOuter (ascending), iter < end; else iter > end.
                int jmpToDescTest = st.Code.EmitForwardJump(Opcode.JmpIfNot, stepNonNegOuter);
                st.Code.Emit3(Opcode.Lt, cmpAsc, iterValSlot, endSlot);
                int jmpAfterAsc = st.Code.EmitForwardJump(Opcode.Jmp);
                st.Code.PatchJumpToHere(jmpToDescTest);
                st.Code.Emit3(Opcode.Gt, cmpAsc, iterValSlot, endSlot);
                st.Code.PatchJumpToHere(jmpAfterAsc);
                exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpAsc);
            }

            // Publish iter value into the iter scope binding (so body sees
            // the current i), then advance iterValSlot for the next test.
            // The AST visitor does:
            //     iterEntry.Value = NumberValue.OfBigNumber(i);
            //     i += step;
            //     <body>
            // Doing the increment BEFORE the body is essential because
            // `continue` jumps back to loop_top; if we incremented after
            // body, continue would skip the increment and the loop would
            // never make progress.
            //
            // Skip the publish entirely when the body never reads the iter
            // name and does not introduce a nested function/class/closure
            // that could capture it. The dispatch loop pays a NumberValue
            // allocation per iter for the boxed publish (auto-boxing from
            // the typed slot via `LocalsView.ToRuntimeValue`) — eliding it
            // when nobody sees the binding is a pure win. Loops with `pass`
            // bodies, side-effect-only bodies (`print("x")`), or external
            // counters all qualify. The advance via `Add` still runs so the
            // counter slot makes progress.
            bool iterPublished = BodyReadsBinding(node.BodyNode, iterName);
            if (iterPublished)
                st.Code.Emit2(Opcode.AssignBinding, iterValSlot, iterNameIdx);
            st.Code.Emit3(Opcode.Add, iterValSlot, iterValSlot, stepSlot);

            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);

            st.Code.PatchJumpToHere(exitJmp);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, loopTopPc);

            if (bodyNeedsScope)
                EmitPopScope(st); // body
            EmitPopScope(st); // iter
            MarkAllTypedAccsDirty(st);
        }

        // Lazy lowering of `for i = lit to lit [step lit] { body }`. The
        // iterator counter lives in a typed Int64 slot for the whole loop;
        // per iter, only LtII / JmpIfNot / AddII / Jmp run on the hot path
        // (plus a body-scope ClearScope when the body introduces bindings,
        // and a BoxI+AssignBinding pair when the body reads `i`). No
        // NumberValue allocations in the iter-advance path.
        //
        // Sign of `step` decides ascending vs descending compare statically.
        // step==0 must have been rejected by the caller (would diverge —
        // the boxed path surfaces the original error).
        private static void CompileForLazyLong(
            ForNode node, string iterName, ushort iterNameIdx,
            long startLit, long endLit, long stepLit,
            State st, ref byte topSlot, byte scratchSlot)
        {
            // M87: snapshot for nested-loop containment.
            var forSnapshot = new TypedPromotionSnapshot(st);

            // M88: statically classify whether the loop body runs at
            // least once. When yes, loop-invariant pure-expression
            // RHS pre-loads admit Div / Mod / Pow without violating
            // the original error-PC contract.
            bool prevGuaranteed = st.LoopGuaranteedToEnter;
            bool willEnter = (stepLit > 0 && startLit < endLit)
                || (stepLit < 0 && startLit > endLit);
            st.LoopGuaranteedToEnter = willEnter;

            // Long-typed slots for iter / end / step.
            byte iterLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(startLit, iterLongSlot, st, ref topSlot);
            byte endLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(endLit, endLongSlot, st, ref topSlot);
            byte stepLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(stepLit, stepLongSlot, st, ref topSlot);

            EmitPushScope(st); // iter scope

            // Placeholder binding so the body's AssignBinding survives
            // ClearScope at every iteration top. Matches CompileForEachLazyIntRange.
            byte nullSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.LoadNull, nullSlot, 0, 0);
            st.Code.Emit2(Opcode.SetLocalDirect, nullSlot, iterNameIdx);

            bool bodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (bodyNeedsScope)
                EmitPushScope(st); // body scope
            int baselineDepth = st.ScopeDepth;

            // Decide upfront whether the boxed iter mirror is needed.
            //
            // Redirectable iter accesses (skip publish):
            //   1. Self-additive RHS: `acc = acc ± iter` — handled by
            //      `TryEmitSelfAdditiveSlot` via AddIntoSlotI or pure
            //      AddII (typed accumulator).
            //   2. Comparison vs literal: `iter ⋈ lit` / `lit ⋈ iter`
            //      where ⋈ ∈ {==, !=, <, <=, >, >=}. Lowered to a
            //      typed `EqII / NeII / LtII / LeII / GtII / GeII`
            //      reading the iter long slot directly.
            //
            // Any other access requires the per-iter `BoxI +
            // AssignBinding` publish so `LoadLocalS iter` sees a fresh
            // boxed mirror in the symbol entry.
            PopulatePromotableStrAccNames(node.BodyNode, st);
            int totalIterAccess = CountVariableAccess(node.BodyNode, iterName);
            int redirectableIterAccess = CountRedirectableIterAccess(node.BodyNode, iterName, st);
            int typedComparableIterAccess = CountTypedIterComparisonAccess(node.BodyNode, iterName);
            int totalRedirectable = redirectableIterAccess + typedComparableIterAccess;
            bool iterPublished = totalIterAccess > 0
                && (totalIterAccess < 0 || totalRedirectable < totalIterAccess);
            // -1 from CountVariableAccess means "unknown node, conservative" —
            // in that case publish to be safe.
            if (totalIterAccess < 0) iterPublished = BodyReadsBinding(node.BodyNode, iterName);

            // Expose the typed iter slot to `TryEmitSelfAdditiveSlot` so
            // `sum = sum + iterName` redirects to `AddIntoSlotI` (or to
            // the pure `AddII` typed-accumulator path below).
            bool addedTyped = false;
            if (iterLongSlot <= byte.MaxValue && !st.ActiveTypedIters.ContainsKey(iterName))
            {
                st.ActiveTypedIters[iterName] = iterLongSlot;
                addedTyped = true;
            }

            // Typed-accumulator promotions emitted ONCE before loop_top.
            // For each accumulator (e.g. `sum`), pre-loop reads its
            // SymbolEntry value and unboxes into a typed Int64 slot. The
            // body's self-additive becomes pure `AddII / SubII`. After
            // the loop, we box the typed slot back to the SymbolEntry.
            var typedAccs = CollectTypedAccumulatorCandidates(node.BodyNode, iterName, st);
            foreach (var acc in typedAccs)
            {
                if (acc.Binding.Offset > ushort.MaxValue) continue;
                byte accLong = AllocTemp(ref topSlot);
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, accBoxedTmp, (ushort)acc.Binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, accLong, accBoxedTmp, 0);
                st.TypedAccumulators[acc.Name] = (accLong, acc.Binding);
            }

            // PERF (O(n) string building): promote loop string accumulators to a
            // per-frame StringBuilder. Pre-loop seeds it from `s`'s current
            // value; the body's `s = s + x` self-appends; loop exit materialises
            // back into `s`. `forStrAccs` records only the accumulators THIS
            // loop registers, so a nested loop reusing the same name (already
            // promoted by an outer loop) appends into the outer builder and is
            // materialised exactly once, by the outer loop.
            var forStrAccs = new List<(string Name, Pipeline.BindingId Binding, int AccIdx)>();
            foreach (var sa in CollectStringAccumulatorCandidates(node.BodyNode, st))
            {
                if (sa.Binding.Offset > ushort.MaxValue) continue;
                if (st.StringAccumulators.ContainsKey(sa.Name)) continue;
                int accIdx = st.NextStrAcc++;
                byte sBoxed = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, sBoxed, (ushort)sa.Binding.Offset);
                st.Code.Emit2(Opcode.StrAccBegin, sBoxed, (ushort)accIdx);
                st.StringAccumulators[sa.Name] = accIdx;
                forStrAccs.Add((sa.Name, sa.Binding, accIdx));
            }

            // Pre-load every distinct int64 literal value used either as
            // RHS of a typed-accumulator self-additive, OR as the literal
            // operand of a typed-iter comparison (`iter ⋈ lit` patterns
            // — see `CompileExpression`'s BinaryOperation case for the
            // typed II emission). Each unique value gets a single typed
            // slot pre-loaded before loop_top.
            var litValuesAll = new HashSet<long>();
            if (typedAccs.Count > 0)
            {
                var accNames = new HashSet<string>();
                foreach (var acc in typedAccs) accNames.Add(acc.Name);
                CollectAccumulatorLiteralRhsValues(node.BodyNode, accNames, litValuesAll);
            }
            CollectIterComparisonLiterals(node.BodyNode, iterName, litValuesAll);
            foreach (var lit in litValuesAll)
            {
                byte litSlot = AllocTemp(ref topSlot);
                EmitLiteralLongLoad(lit, litSlot, st, ref topSlot);
                st.TypedAccumulatorLiterals[lit] = litSlot;
            }

            // Loop-invariant pure-expression RHS pre-load. For each
            // typed accumulator candidate whose RHS is a pure tree of
            // outer-scope-only never-mutated bindings (admitted by
            // `IsLoopInvariantPureNumericExpr`), compile the RHS ONCE
            // before loop_top into a boxed slot, then UnboxI into a
            // typed Int64 slot. The body's self-additive then emits a
            // pure `AddII / SubII` reading the typed slot directly —
            // no per-iter NumberValue allocation. UnboxI deopts to a
            // Ref tag if the RHS doesn't fit Int64; subsequent
            // `AddII` still reads correctly via `TryReadAsLong`'s
            // boxed fallback (slower but correct).
            if (typedAccs.Count > 0)
            {
                var accNameSet = new HashSet<string>();
                foreach (var acc in typedAccs) accNameSet.Add(acc.Name);
                CollectAccumulatorLoopInvariantExprs(
                    node.BodyNode, node.BodyNode, iterName, accNameSet, st, ref topSlot);
            }

            // Typed-long-binding promotion. Pattern: `iter ⋈ var_name`
            // where var_name is a slot-eligible local that is NEVER
            // mutated in the body. Pre-loop reads the binding's boxed
            // value once and unboxes into a typed Int64 slot. The
            // comparison redirect below emits a pure typed II compare
            // reading both operands as longs — no boxed mirror, no
            // iter publish required for this access.
            var bindingNames = new HashSet<string>();
            CollectIterComparisonBindingNames(node.BodyNode, iterName, bindingNames);
            foreach (var nm in bindingNames)
            {
                if (st.ActiveTypedIters.ContainsKey(nm)) continue;
                if (st.TypedAccumulators.ContainsKey(nm)) continue;
                if (st.TypedLongBindings.ContainsKey(nm)) continue;
                if (HasAnyAssignmentTo(node.BodyNode, nm)) continue;
                var binding = FindFirstBindingOfName(node.BodyNode, nm);
                if (!binding.IsResolved) continue;
                if (binding.Offset > ushort.MaxValue) continue;
                if (!IsSlotEligible(binding, BindingKind.Local, st)
                    && !IsSlotEligible(binding, BindingKind.Global, st)
                    && !IsSlotEligible(binding, BindingKind.Parameter, st))
                    continue;
                byte bndLong = AllocTemp(ref topSlot);
                byte bndBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, bndBoxedTmp, (ushort)binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, bndLong, bndBoxedTmp, 0);
                st.TypedLongBindings[nm] = (bndLong, binding);
            }

            int loopTopPc = st.Code.Pc;
            if (bodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            byte cmpSlot = AllocTemp(ref topSlot);
            Opcode testOp = stepLit > 0 ? Opcode.LtII : Opcode.GtII;
            st.Code.Emit3(testOp, cmpSlot, iterLongSlot, endLongSlot);
            int exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpSlot);

            if (iterPublished)
            {
                byte iterBoxSlot = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.BoxI, iterBoxSlot, iterLongSlot, 0);
                st.Code.Emit2(Opcode.AssignBinding, iterBoxSlot, iterNameIdx);
            }

            // Body runs BEFORE advance so both the boxed publish above and
            // the typed `AddIntoSlotI` reads inside the body observe the
            // SAME iteration value (pre-advance). `continue` jumps to
            // `continueTargetPc` (the advance) so the loop still makes
            // progress through any early exit. Matches `for i = 0 to N`
            // semantics: body sees i = 0, 1, …, N-1.
            //
            // Initialise dirty-tracking: at body entry the typed slot
            // matches the SymbolEntry (pre-loop UnboxI synced them).
            // ACROSS iterations, the previous iter may have left the
            // SymbolEntry stale though — conservatively mark every
            // typed acc dirty at body start so the first boxed read in
            // an iter always publishes.
            MarkAllTypedAccsDirty(st);
            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            int continueTargetPc = st.Code.Pc;
            st.Code.Emit3(Opcode.AddII, iterLongSlot, iterLongSlot, stepLongSlot);
            st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);
            st.Code.PatchJumpToHere(exitJmp);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, continueTargetPc);

            // Box accumulators back to their SymbolEntry after every loop
            // exit (test failure + break both land here). Subsequent code
            // observing `acc` sees the freshly-computed value. Only
            // emit the box-back for entries WE added (not present in
            // entry snapshot) — outer loops may still own typed slots
            // whose box-back is their responsibility.
            foreach (var acc in typedAccs)
            {
                if (forSnapshot.TypedAccumulators.ContainsKey(acc.Name)) continue;
                if (acc.Binding.Offset > ushort.MaxValue) continue;
                if (!st.TypedAccumulators.ContainsKey(acc.Name)) continue;
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.BoxI, accBoxedTmp, st.TypedAccumulators[acc.Name].LongSlot, 0);
                st.Code.Emit2(Opcode.StoreLocalS, accBoxedTmp, (ushort)acc.Binding.Offset);
            }
            // O(n) string building: materialise each string accumulator THIS
            // loop owns back into its boxed `s` SymbolEntry, then retire it from
            // the active set so post-loop code reads the finished string.
            foreach (var sa in forStrAccs)
            {
                byte matTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.StrAccMaterialize, matTmp, (ushort)sa.AccIdx);
                st.Code.Emit2(Opcode.StoreLocalS, matTmp, (ushort)sa.Binding.Offset);
                st.StringAccumulators.Remove(sa.Name);
            }
            // M87: restore the entry-snapshot rather than blow away the
            // outer loop's typed dicts. Preserves correctness of nested
            // loop compilation.
            forSnapshot.RestoreInto(st);
            // M88: restore the guaranteed-enter flag too.
            st.LoopGuaranteedToEnter = prevGuaranteed;

            if (bodyNeedsScope)
                EmitPopScope(st); // body
            EmitPopScope(st); // iter
        }

        // ForEach: canonicalises the iteration source to a ListValue via
        // OP_FOREACH_ITERABLE, then index-iterates it. Two scopes (iter +
        // body) mirror ForEachNodeVisitor's loopContext + bodyContext.
        private static void CompileForEach(ForEachNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            // M21.1: pre-checks dropped — see CompileWhile rationale.
            string iterName = node.VarNameToken.Value!.ToString()!;
            ushort iterNameIdx = st.Names.Add(iterName);

            // M66.6: lazy-Range fast path. `for v in lit..lit` (or `..=lit`)
            // without an explicit step iterates as a long counter in
            // `LongLocals` so the loop never materialises a million-element
            // `ListValue`. The boxed Range opcode is bypassed entirely.
            //
            // Restricted to compile-time literal int bounds so the
            // semantic equivalence (including the "start > end →
            // RuntimeError" throw) is decided statically: when the
            // literals satisfy `start <= end`, the lazy form is exact;
            // otherwise control falls through to the materialised
            // boxed path which surfaces the original error.
            if (node.CollectionNode is RangeNode rn
                && rn.Step == null
                && TryGetLiteralLong(rn.Start, out long startLit)
                && TryGetLiteralLong(rn.End, out long endLit)
                && startLit <= endLit)
            {
                bool inclusive = rn.Operator.Type == TokenType.DOUBLE_DOT_EQ;
                CompileForEachLazyIntRange(node, iterName, iterNameIdx,
                    startLit, endLit, inclusive, st, ref topSlot, scratchSlot);
                return;
            }

            byte collSlot = AllocTemp(ref topSlot);
            CompileExpression(node.CollectionNode, collSlot, st, ref topSlot);

            EmitPushScope(st); // iter scope

            // Iter-name binding placeholder shared between both code paths.
            byte nullSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.LoadNull, nullSlot, 0, 0);
            st.Code.Emit2(Opcode.SetLocalDirect, nullSlot, iterNameIdx);

            // M82-streams: runtime dispatch between two foreach shapes —
            //   * fall-through  →  materialising IR fast path (List/Set/Map/
            //                      Tuple). Cheap when the collection is
            //                      already eager.
            //   * stream branch →  per-iteration lazy pull through
            //                      Opcode.ForEachStreamPull. Required for
            //                      sync `StreamValue` so infinite producers
            //                      + body `break` terminate without
            //                      draining the source first.
            // The body is emitted twice (once per path) because each path
            // has its own break/continue exit labels; bytecode cost is
            // bounded and avoids needing a polymorphic iterator object.
            int streamBranchPc = st.Code.EmitForwardJump(Opcode.JmpIfStream, collSlot);

            // ---- materialising path (List / Tuple / Set / Map) ----------
            byte iterListSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.ForEachIterable, iterListSlot, collSlot, 0);

            byte lenSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.ListLen, lenSlot, iterListSlot, 0);

            byte idxSlot = AllocTemp(ref topSlot);
            ushort zeroIdx = st.Consts.Add(NumberValue.Zero);
            st.Code.Emit2(Opcode.LoadConst, idxSlot, zeroIdx);

            byte oneSlot = AllocTemp(ref topSlot);
            ushort oneIdx = st.Consts.Add(NumberValue.One);
            st.Code.Emit2(Opcode.LoadConst, oneSlot, oneIdx);

            EmitPushScope(st); // body scope (materialising)
            int baselineDepthMat = st.ScopeDepth;
            int loopTopMatPc = st.Code.Pc;
            st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            byte cmpSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.Lt, cmpSlot, idxSlot, lenSlot);
            int exitJmpMat = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpSlot);

            byte itemSlotMat = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.ListGet, itemSlotMat, iterListSlot, idxSlot);
            st.Code.Emit2(Opcode.AssignBinding, itemSlotMat, iterNameIdx);

            // Increment BEFORE the body runs so `continue` (which jumps back
            // to loop_top) doesn't skip the iteration step. Mirrors the
            // For-loop fix that landed in M4.
            st.Code.Emit3(Opcode.Add, idxSlot, idxSlot, oneSlot);

            var loopMat = new LoopContext(loopTopMatPc, baselineDepthMat);
            st.Loops.Push(loopMat);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopMatPc);

            st.Code.PatchJumpToHere(exitJmpMat);
            foreach (var p in loopMat.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loopMat.ContinueFixups, loopTopMatPc);

            EmitPopScope(st); // body scope (materialising)

            // Skip the lazy-stream path on the materialising fall-through.
            int doneJmp = st.Code.EmitForwardJump(Opcode.Jmp);

            // ---- lazy stream path ----------------------------------------
            st.Code.PatchJumpToHere(streamBranchPc);

            EmitPushScope(st); // body scope (stream)
            int baselineDepthStream = st.ScopeDepth;
            int loopTopStreamPc = st.Code.Pc;
            st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            byte itemSlotStream = AllocTemp(ref topSlot);
            byte continueSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.ForEachStreamPull, itemSlotStream, collSlot, continueSlot);
            int exitJmpStream = st.Code.EmitForwardJump(Opcode.JmpIfNot, continueSlot);
            st.Code.Emit2(Opcode.AssignBinding, itemSlotStream, iterNameIdx);

            var loopStream = new LoopContext(loopTopStreamPc, baselineDepthStream);
            st.Loops.Push(loopStream);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopStreamPc);

            st.Code.PatchJumpToHere(exitJmpStream);
            foreach (var p in loopStream.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loopStream.ContinueFixups, loopTopStreamPc);

            EmitPopScope(st); // body scope (stream)

            // ---- join point ---------------------------------------------
            st.Code.PatchJumpToHere(doneJmp);
            EmitPopScope(st); // iter scope
            MarkAllTypedAccsDirty(st);
        }

        // M66.6 helpers ----------------------------------------------------
        //
        // Extracts an int64 value from any constant-foldable arithmetic
        // sub-tree (literal, literal+literal, -literal, etc.) using
        // `TryConstEvalNumber`. Returns false for non-foldable or
        // out-of-int64-range values. Accepts:
        //   * Scale == 0: trivial integer literal.
        //   * Scale  > 0 but Unscaled divisible by 10^Scale: math-
        //     integer "float-like" literals such as `1.0`, `2.000` —
        //     these are integer-valued NumberValues whose scaled
        //     representation just happens to carry trailing zeros.
        // Truly non-integer values (e.g. `1.5`) return false and the
        // caller falls through to `TryReduceNonIntIterCompareLit` for
        // the static rewrite path.
        private static bool TryGetLiteralLongFromConstExpr(AstNode node, out long value)
        {
            value = 0;
            if (!TryConstEvalNumber(node, out var nv)) return false;
            var bn = nv.Value;
            var bnMin = (System.Numerics.BigInteger)long.MinValue;
            var bnMax = (System.Numerics.BigInteger)long.MaxValue;
            if (bn.Scale.IsZero)
            {
                if (bn.Unscaled < bnMin || bn.Unscaled > bnMax) return false;
                value = (long)bn.Unscaled;
                return true;
            }
            if (bn.Scale.Sign <= 0) return false;
            if (bn.Scale > 30) return false;
            int scaleInt = (int)bn.Scale;
            var divisor = System.Numerics.BigInteger.Pow(10, scaleInt);
            var rem = bn.Unscaled % divisor;
            if (!rem.IsZero) return false; // truly non-integer
            var normalized = bn.Unscaled / divisor;
            if (normalized < bnMin || normalized > bnMax) return false;
            value = (long)normalized;
            return true;
        }

        // For a constant-foldable NumberValue that is TRULY non-integer
        // (e.g. `1.5`, `-0.25`), compute the integer-equivalent rewrite
        // of an `iter ⋈ rhs` comparison. Returns one of:
        //   * RewriteOk    → emit `iter newOp newLit` (typed II compare).
        //   * ConstFalse   → emit `LoadFalse` directly (iter is integer,
        //                    cannot equal a non-integer NumberValue).
        //   * ConstTrue    → emit `LoadTrue` directly.
        //   * CantRewrite  → caller falls back to boxed dispatch.
        // `op` is the original comparison token; `iterOnLeft` records
        // whether `iter` is on the LHS (so direction-sensitive ops are
        // adjusted on swap).
        private enum NonIntCompareResult { CantRewrite, RewriteOk, ConstFalse, ConstTrue }

        private static NonIntCompareResult TryReduceNonIntIterCompareLit(
            Lexer.Tokens.TokenType op, AstNode rhsConst, bool iterOnLeft,
            out Lexer.Tokens.TokenType newOp, out long newLit)
        {
            newOp = op;
            newLit = 0;
            if (!TryConstEvalNumber(rhsConst, out var nv)) return NonIntCompareResult.CantRewrite;
            var bn = nv.Value;
            if (bn.Scale.IsZero) return NonIntCompareResult.CantRewrite; // integer — caller uses normal path
            if (bn.Scale.Sign <= 0) return NonIntCompareResult.CantRewrite;
            if (bn.Scale > 30) return NonIntCompareResult.CantRewrite;
            int sc = (int)bn.Scale;
            var divisor = System.Numerics.BigInteger.Pow(10, sc);
            var rem = bn.Unscaled % divisor;
            if (rem.IsZero) return NonIntCompareResult.CantRewrite; // actually math-integer; caller uses normal path

            // Mathematical floor: BigInteger division rounds toward zero;
            // for negative non-divisible values we subtract 1 to get the
            // mathematical floor. ceil = floor + 1.
            var floor = bn.Unscaled / divisor;
            if (bn.Unscaled.Sign < 0) floor -= 1;
            var ceil = floor + 1;
            var bnMin = (System.Numerics.BigInteger)long.MinValue;
            var bnMax = (System.Numerics.BigInteger)long.MaxValue;
            if (floor < bnMin || ceil > bnMax) return NonIntCompareResult.CantRewrite;
            long floorL = (long)floor;
            long ceilL = (long)ceil;

            // For `iter ⋈ N.M`:
            //   <    → <= floor
            //   <=   → <= floor
            //   >    → >= ceil  (equivalent to > floor; pick >= ceil for
            //                    a single LE-family opcode reuse)
            //   >=   → >= ceil
            //   ==   → false
            //   !=   → true
            // When iter is on the RIGHT (e.g. `1.5 < iter`), invert the
            // comparison direction first.
            Lexer.Tokens.TokenType effective = op;
            if (!iterOnLeft)
            {
                effective = op switch
                {
                    Lexer.Tokens.TokenType.LT  => Lexer.Tokens.TokenType.GT,
                    Lexer.Tokens.TokenType.LTE => Lexer.Tokens.TokenType.GTE,
                    Lexer.Tokens.TokenType.GT  => Lexer.Tokens.TokenType.LT,
                    Lexer.Tokens.TokenType.GTE => Lexer.Tokens.TokenType.LTE,
                    _ => op,
                };
            }
            switch (effective)
            {
                case Lexer.Tokens.TokenType.LT:
                case Lexer.Tokens.TokenType.LTE:
                    newOp = Lexer.Tokens.TokenType.LTE;
                    newLit = floorL;
                    return NonIntCompareResult.RewriteOk;
                case Lexer.Tokens.TokenType.GT:
                case Lexer.Tokens.TokenType.GTE:
                    newOp = Lexer.Tokens.TokenType.GTE;
                    newLit = ceilL;
                    return NonIntCompareResult.RewriteOk;
                case Lexer.Tokens.TokenType.EE:
                    return NonIntCompareResult.ConstFalse;
                case Lexer.Tokens.TokenType.NE:
                    return NonIntCompareResult.ConstTrue;
            }
            return NonIntCompareResult.CantRewrite;
        }

        // `TryGetLiteralLong` extracts the int64 value of a numeric literal
        // when it is unsuffixed (a `NumberValue`) and integer-valued. Used
        // by the lazy-Range fast path; non-literal or non-int bounds fall
        // back to the boxed Range materialisation.
        private static bool TryGetLiteralLong(AstNode node, out long value)
        {
            value = 0;
            if (node is not NumberNode nn) return false;
            if (nn.CachedValue == null)
                nn.CachedValue = ParseNumberLiteralForIr(nn);
            if (nn.CachedValue is not NumberValue nv) return false;
            if (!nv.Value.Scale.IsZero) return false;
            if (nv.Value.Unscaled < (System.Numerics.BigInteger)long.MinValue
                || nv.Value.Unscaled > (System.Numerics.BigInteger)long.MaxValue) return false;
            value = (long)nv.Value.Unscaled;
            return true;
        }

        // Pushes either a `LoadIntS64` (slot-sized immediate fits int16)
        // or a boxed `LoadConst` followed by an `UnboxI` into the given
        // long slot. The boxed-then-unbox path lets the chain analyzer
        // promote the LoadConst itself when the wider int still fits the
        // M66.5 LoadIntS64 promotion criteria, and otherwise leaves the
        // `NumberValue` allocation as a one-shot cost paid before the
        // loop body runs.
        private static void EmitLiteralLongLoad(long value, byte longSlot, State st, ref byte topSlot)
        {
            if (value >= short.MinValue && value <= short.MaxValue)
            {
                st.Code.Emit2(Opcode.LoadIntS64, longSlot, unchecked((ushort)(short)value));
                return;
            }
            byte boxedSlot = AllocTemp(ref topSlot);
            var bn = new BigNumber(new System.Numerics.BigInteger(value), System.Numerics.BigInteger.Zero);
            ushort idx = st.Consts.Add(new NumberValue(bn));
            st.Code.Emit2(Opcode.LoadConst, boxedSlot, idx);
            st.Code.Emit3(Opcode.UnboxI, longSlot, boxedSlot, 0);
        }

        // Lazy lowering of `for v in start_lit..end_lit` (or `..=`). The
        // iterator counter stays in `LongLocals` and walks the range
        // without materialising any intermediate collection. Per-iter the
        // counter is boxed once (BoxI → AssignBinding) so the body's
        // user-level `v` reads still resolve through the SymbolEntry.
        //
        //   <load start, end, one as longs>
        //   PushScope (iter)
        //   LoadNull placeholder; SetLocalDirect placeholder, iterName
        //   PushScope (body)
        //   loop_top:
        //     ClearScope
        //     LtII/LeII cmp, iter_long, end_long
        //     JmpIfNot exit
        //     BoxI iter_box, iter_long
        //     AssignBinding iter_box, iterName
        //     AddII iter_long, iter_long, one_long
        //     <body>
        //     Jmp loop_top
        //   exit:
        //   PopScope (body); PopScope (iter)
        private static void CompileForEachLazyIntRange(
            ForEachNode node, string iterName, ushort iterNameIdx,
            long startLit, long endLit, bool inclusive,
            State st, ref byte topSlot, byte scratchSlot)
        {
            // M84 — tiny-trip-count unroll deferred. First-pass
            // implementation revealed a subtle interaction with
            // top-level statement boundaries (a second unrolled
            // for-loop following the first one in the same script
            // body left the "i" SymbolEntry slot cache pointing at
            // an orphaned entry from the first loop's iter scope,
            // surfacing as "VM:NullOperand" at runtime). The
            // diagnostic walker `BodyHasBreakOrContinueAtThisLevel`
            // is kept in place for future re-enablement, but the
            // unroll emission path stays gated off until a clean
            // slot-cache invalidation strategy lands. Tier 9
            // analyses (escape analysis / IPCP / PRE / SROA /
            // DSE-across-phi / loop unrolling) are tracked as
            // separate future milestones.
            // M87: snapshot for nested-loop containment.
            var feSnapshot = new TypedPromotionSnapshot(st);

            // M88: caller (`CompileForEach`) already gates this lowering
            // on `startLit <= endLit`. For the inclusive form (`..=`),
            // equality means a single iteration. For the half-open form
            // (`..`), the body runs only when `startLit < endLit`.
            bool prevFeGuaranteed = st.LoopGuaranteedToEnter;
            st.LoopGuaranteedToEnter = inclusive
                ? (startLit <= endLit)
                : (startLit < endLit);

            byte iterLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(startLit, iterLongSlot, st, ref topSlot);

            byte endLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(endLit, endLongSlot, st, ref topSlot);

            byte oneLongSlot = AllocTemp(ref topSlot);
            EmitLiteralLongLoad(1, oneLongSlot, st, ref topSlot);

            EmitPushScope(st); // iter scope

            // Placeholder binding so the body's `AssignBinding` survives
            // `ClearScope` at every iteration top.
            byte nullSlot = AllocTemp(ref topSlot);
            st.Code.Emit3(Opcode.LoadNull, nullSlot, 0, 0);
            st.Code.Emit2(Opcode.SetLocalDirect, nullSlot, iterNameIdx);

            bool foreachBodyNeedsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(node.BodyNode);
            if (foreachBodyNeedsScope)
                EmitPushScope(st); // body scope
            int baselineDepth = st.ScopeDepth;

            // M87.5: lift the same typed-promotion plumbing as
            // `CompileForLazyLong` into the foreach lazy-range path.
            // Previously the foreach lowered to AddII for the iter
            // advance but the body's `sum = sum + i` re-routed through
            // `AddIntoSlot[I]` (boxed dispatch, NumberValue per iter)
            // because no `TypedAccumulators` registration existed.
            //
            // Step 1: classify iter accesses to decide whether the boxed
            // `BoxI + AssignBinding` publish is dead. Mirrors
            // `CompileForLazyLong`'s redirect counting.
            // Pre-compute which string accumulators THIS loop will promote, so
            // `s = s + iter` counts as a redirectable (publish-eliding) access
            // below (it lowers to StrAccAppendI reading the typed slot).
            PopulatePromotableStrAccNames(node.BodyNode, st);
            int feTotalIterAccess = CountVariableAccess(node.BodyNode, iterName);
            int feRedirectableIterAccess = CountRedirectableIterAccess(node.BodyNode, iterName, st);
            int feTypedComparableIterAccess = CountTypedIterComparisonAccess(node.BodyNode, iterName);
            int feTotalRedirectable = feRedirectableIterAccess + feTypedComparableIterAccess;
            bool feIterPublished = feTotalIterAccess > 0
                && (feTotalIterAccess < 0 || feTotalRedirectable < feTotalIterAccess);
            if (feTotalIterAccess < 0) feIterPublished = BodyReadsBinding(node.BodyNode, iterName);

            // Step 2: register iter as active typed iter so body-side
            // `iter ⋈ lit` comparisons and `acc = acc ± iter` redirects
            // can pick it up.
            bool addedFeTyped = false;
            if (iterLongSlot <= byte.MaxValue && !st.ActiveTypedIters.ContainsKey(iterName))
            {
                st.ActiveTypedIters[iterName] = iterLongSlot;
                addedFeTyped = true;
            }

            // Step 3: collect typed accumulator candidates from the body
            // and pre-load each into a typed Int64 slot. Mirrors
            // `CompileForLazyLong` line 2169-2199.
            var feTypedAccs = CollectTypedAccumulatorCandidates(node.BodyNode, iterName, st);
            foreach (var acc in feTypedAccs)
            {
                if (acc.Binding.Offset > ushort.MaxValue) continue;
                byte accLong = AllocTemp(ref topSlot);
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, accBoxedTmp, (ushort)acc.Binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, accLong, accBoxedTmp, 0);
                st.TypedAccumulators[acc.Name] = (accLong, acc.Binding);
            }

            // Step 4: pre-load every distinct int64 literal used by
            // typed-acc self-additives or typed-iter comparisons in
            // the body. Adds the foreach's own `0` (when accumulator
            // RHS happens to be it) — harmless duplicate.
            var feLitValuesAll = new HashSet<long>();
            if (feTypedAccs.Count > 0)
            {
                var feAccNames = new HashSet<string>();
                foreach (var acc in feTypedAccs) feAccNames.Add(acc.Name);
                CollectAccumulatorLiteralRhsValues(node.BodyNode, feAccNames, feLitValuesAll);
            }
            CollectIterComparisonLiterals(node.BodyNode, iterName, feLitValuesAll);
            // M87: also feed the generalised typed-Int64 redirect by
            // pre-loading every integer literal anywhere in the body.
            CollectAllConstIntLiterals(node.BodyNode, feLitValuesAll);
            foreach (var lit in feLitValuesAll)
            {
                byte litSlot = AllocTemp(ref topSlot);
                EmitLiteralLongLoad(lit, litSlot, st, ref topSlot);
                st.TypedAccumulatorLiterals[lit] = litSlot;
            }

            // Step 5: loop-invariant pure-expression RHS pre-load.
            if (feTypedAccs.Count > 0)
            {
                var feAccNameSet = new HashSet<string>();
                foreach (var acc in feTypedAccs) feAccNameSet.Add(acc.Name);
                CollectAccumulatorLoopInvariantExprs(
                    node.BodyNode, node.BodyNode, iterName, feAccNameSet, st, ref topSlot);
            }

            // Step 6: typed-long binding pre-load for `iter ⋈ name`
            // comparison sites where `name` is a never-mutated local.
            var feBindingNames = new HashSet<string>();
            CollectIterComparisonBindingNames(node.BodyNode, iterName, feBindingNames);
            foreach (var nm in feBindingNames)
            {
                if (st.ActiveTypedIters.ContainsKey(nm)) continue;
                if (st.TypedAccumulators.ContainsKey(nm)) continue;
                if (st.TypedLongBindings.ContainsKey(nm)) continue;
                if (HasAnyAssignmentTo(node.BodyNode, nm)) continue;
                var binding = FindFirstBindingOfName(node.BodyNode, nm);
                if (!binding.IsResolved) continue;
                if (binding.Offset > ushort.MaxValue) continue;
                if (!IsSlotEligible(binding, BindingKind.Local, st)
                    && !IsSlotEligible(binding, BindingKind.Global, st)
                    && !IsSlotEligible(binding, BindingKind.Parameter, st))
                    continue;
                byte bndLong = AllocTemp(ref topSlot);
                byte bndBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, bndBoxedTmp, (ushort)binding.Offset);
                st.Code.Emit3(Opcode.UnboxI, bndLong, bndBoxedTmp, 0);
                st.TypedLongBindings[nm] = (bndLong, binding);
            }

            // Step 7: O(n) string building. Promote loop string accumulators
            // (`s = s + x`) to a per-frame StringBuilder seeded from `s`'s
            // current value; the body self-appends; loop exit materialises back
            // into `s`. Mirrors `CompileForLazyLong`'s Step. `feStrAccs` records
            // only accumulators THIS loop registers, so a nested loop reusing
            // the name appends into the outer builder, materialised once outside.
            var feStrAccs = new List<(string Name, Pipeline.BindingId Binding, int AccIdx)>();
            foreach (var sa in CollectStringAccumulatorCandidates(node.BodyNode, st))
            {
                if (sa.Binding.Offset > ushort.MaxValue) continue;
                if (st.StringAccumulators.ContainsKey(sa.Name)) continue;
                int accIdx = st.NextStrAcc++;
                byte sBoxed = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.LoadLocalS, sBoxed, (ushort)sa.Binding.Offset);
                st.Code.Emit2(Opcode.StrAccBegin, sBoxed, (ushort)accIdx);
                st.StringAccumulators[sa.Name] = accIdx;
                feStrAccs.Add((sa.Name, sa.Binding, accIdx));
            }

            int loopTopPc = st.Code.Pc;
            if (foreachBodyNeedsScope)
                st.Code.Emit3(Opcode.ClearScope, 0, 0, 0);

            byte cmpSlot = AllocTemp(ref topSlot);
            Opcode testOp = inclusive ? Opcode.LeII : Opcode.LtII;
            st.Code.Emit3(testOp, cmpSlot, iterLongSlot, endLongSlot);
            int exitJmp = st.Code.EmitForwardJump(Opcode.JmpIfNot, cmpSlot);

            if (feIterPublished)
            {
                byte iterBoxSlot = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.BoxI, iterBoxSlot, iterLongSlot, 0);
                st.Code.Emit2(Opcode.AssignBinding, iterBoxSlot, iterNameIdx);
            }

            // Body runs BEFORE the advance so both the boxed publish
            // (when emitted) and the typed `AddIntoSlotI` reads inside
            // the body observe the SAME iteration value (pre-advance).
            // `continue` jumps to `continueTargetPc` (the advance) so the
            // loop still makes progress through any early exit. Matches
            // `for i in 0..N` semantics: body sees i = 0, 1, …, N-1.
            //
            // Initialise dirty-tracking: at body entry the typed slot
            // matches the SymbolEntry (pre-loop UnboxI synced them).
            // ACROSS iterations, the previous iter may have left the
            // SymbolEntry stale though — conservatively mark every
            // typed acc dirty at body start so the first boxed read in
            // an iter always publishes.
            MarkAllTypedAccsDirty(st);
            var loop = new LoopContext(loopTopPc, baselineDepth);
            st.Loops.Push(loop);
            try
            {
                CompileBodyStrictInline(node.BodyNode, st, ref topSlot, scratchSlot);
            }
            finally
            {
                st.Loops.Pop();
            }

            int continueTargetPc = st.Code.Pc;
            st.Code.Emit3(Opcode.AddII, iterLongSlot, iterLongSlot, oneLongSlot);
            st.Code.EmitBackwardJump(Opcode.Jmp, 0, loopTopPc);
            st.Code.PatchJumpToHere(exitJmp);
            foreach (var p in loop.BreakFixups) st.Code.PatchJumpToHere(p);
            PatchJumpsBackward(st, loop.ContinueFixups, continueTargetPc);

            // Box accumulators back to their SymbolEntry slots so
            // post-loop reads observe the fresh value. Skip entries we
            // inherited from the entry snapshot — those belong to an
            // outer loop and its post-emission will handle them.
            foreach (var acc in feTypedAccs)
            {
                if (feSnapshot.TypedAccumulators.ContainsKey(acc.Name)) continue;
                if (acc.Binding.Offset > ushort.MaxValue) continue;
                if (!st.TypedAccumulators.ContainsKey(acc.Name)) continue;
                byte accBoxedTmp = AllocTemp(ref topSlot);
                st.Code.Emit3(Opcode.BoxI, accBoxedTmp, st.TypedAccumulators[acc.Name].LongSlot, 0);
                st.Code.Emit2(Opcode.StoreLocalS, accBoxedTmp, (ushort)acc.Binding.Offset);
            }
            // O(n) string building: materialise each string accumulator THIS
            // loop owns back into its boxed `s` SymbolEntry, then retire it from
            // the active set so post-loop code reads the finished string.
            foreach (var sa in feStrAccs)
            {
                byte matTmp = AllocTemp(ref topSlot);
                st.Code.Emit2(Opcode.StrAccMaterialize, matTmp, (ushort)sa.AccIdx);
                st.Code.Emit2(Opcode.StoreLocalS, matTmp, (ushort)sa.Binding.Offset);
                st.StringAccumulators.Remove(sa.Name);
            }
            // M87: restore the entry-snapshot rather than blow away the
            // outer loop's typed dicts. Preserves correctness of nested
            // loop compilation.
            feSnapshot.RestoreInto(st);
            // M88: restore the guaranteed-enter flag too.
            st.LoopGuaranteedToEnter = prevFeGuaranteed;

            if (foreachBodyNeedsScope)
                EmitPopScope(st); // body
            EmitPopScope(st); // iter
            MarkAllTypedAccsDirty(st);
        }

        // Try / Catch lowering (no Finally support yet — Finally falls back).
        //
        //   pre_try_depth = state.ScopeDepth captured
        //   try_start_pc:
        //     <try body>
        //   try_end_pc:
        //     Jmp after_catch
        //   catch_pc:
        //     PushScope                              [new scope for catch var]
        //     SetLocalDirect catchVar, catchSlot     [TryRaise pre-populated catchSlot]
        //     <catch body>
        //     PopScope
        //   after_catch:
        //
        // EhTable entry: {StartPc=try_start, EndPc=try_end, CatchPc=catch_pc,
        // FinallyPc=-1, CatchSlot, ScopeDepth=pre_try_depth}.
        //
        // The dispatch loop's try/catch RaUserError handler scans EhTable on
        // any opcode raise, pops the runtime Context down to ScopeDepth, and
        // jumps to CatchPc with the error-message string in CatchSlot.
        private static void CompileTry(Parser.Nodes.Special.TryNode node, State st, ref byte topSlot, byte scratchSlot)
        {
            // Finally needs the full TryNodeVisitor state machine (which
            // runs finally on every exit path including return/break/
            // continue/throw). Route via OP_NATIVE_DEFINE → TryNodeVisitor.Apply
            // so the dispatch loop calls the visitor's static helper
            // directly (no interpreter._visitors[] indexing).
            if (node.FinallyBody != null)
            {
                if (st.DefineRefs.Count > ushort.MaxValue)
                    throw new IrCompileException("DefineRefs overflow");
                ushort refIdx = (ushort)st.DefineRefs.Count;
                st.DefineRefs.Add(node);
                st.Code.Emit2(Opcode.NativeDefine, scratchSlot, refIdx);
                return;
            }
            // M21.1: pre-checks dropped — strict-mode body compile raises
            // IrCompileException directly when needed.
            byte catchSlot = AllocTemp(ref topSlot);
            int scopeDepthAtTry = st.ScopeDepth;

            // Wrap try body in its own scope so `var` declarations inside
            // the try block die at try-end (matching ScopeNodeVisitor's
            // context.Copy()). On raise the dispatch loop pops ctx back to
            // scopeDepthAtTry — *before* this push — so the catch sees the
            // outer-scope state.
            EmitPushScope(st);
            int tryStartPc = st.Code.Pc;
            CompileBodyStrictInline(node.TryBody, st, ref topSlot, scratchSlot);
            int tryEndPc = st.Code.Pc;
            EmitPopScope(st);

            int afterCatchJmp = st.Code.EmitForwardJump(Opcode.Jmp);

            int catchPc = st.Code.Pc;
            if (node.CatchBody != null)
            {
                EmitPushScope(st);
                if (node.CatchVarTok != null)
                {
                    string varName = node.CatchVarTok.Value!.ToString()!;
                    ushort nameIdx = st.Names.Add(varName);
                    st.Code.Emit2(Opcode.SetLocalDirect, catchSlot, nameIdx);
                }
                CompileBodyStrictInline(node.CatchBody, st, ref topSlot, scratchSlot);
                EmitPopScope(st);
            }

            st.Code.PatchJumpToHere(afterCatchJmp);

            st.EhTable.Add(new ExceptionHandler(
                start: tryStartPc, end: tryEndPc,
                catchPc: catchPc, finallyPc: -1,
                catchSlot: catchSlot, scopeDepth: scopeDepthAtTry));
            // Try/catch may have taken either path at runtime; conservatively
            // re-dirty every typed accumulator.
            MarkAllTypedAccsDirty(st);
        }

        // Compile a body inside a fresh PushScope/PopScope pair. The strict
        // mode TryCompileStatement is used for the body children so any
        // unsupported construct aborts the whole enclosing statement.
        private static void CompileBodyScoped(AstNode body, State st, ref byte topSlot, byte scratchSlot)
        {
            // Skip PushScope/PopScope when body introduces no bindings.
            // Avoids per-fire Context.Copy allocations for hot `if cond
            // { body }` shapes inside loops (e.g. `if i < max { sum =
            // sum + i; }` triggers 500K Context allocations on a 1M-iter
            // loop with 50% branch true-rate).
            bool needsScope = Parser.Nodes.AstScopeAnalysis.NeedsFreshScope(body);
            if (needsScope) EmitPushScope(st);
            if (body is ScopeNode sc)
            {
                foreach (var child in sc.Nodes)
                {
                    if (!TryCompileStatement(child, st, ref topSlot, scratchSlot, strict: true))
                        throw new IrCompileException($"body child not compilable: {child.NodeType}");
                }
            }
            else
            {
                if (!TryCompileStatement(body, st, ref topSlot, scratchSlot, strict: true))
                    throw new IrCompileException($"body not compilable: {body.NodeType}");
            }
            if (needsScope) EmitPopScope(st);
        }

        // Compile a body without an additional scope push (the caller has
        // already pushed). Used by While / DoWhile / For where the loop has
        // already established a body scope.
        private static void CompileBodyStrictInline(AstNode body, State st, ref byte topSlot, byte scratchSlot)
        {
            if (body is ScopeNode sc)
            {
                foreach (var child in sc.Nodes)
                {
                    if (!TryCompileStatement(child, st, ref topSlot, scratchSlot, strict: true))
                        throw new IrCompileException($"body child not compilable: {child.NodeType}");
                }
            }
            else
            {
                if (!TryCompileStatement(body, st, ref topSlot, scratchSlot, strict: true))
                    throw new IrCompileException($"body not compilable: {body.NodeType}");
            }
        }

        // Emit OP_POP_SCOPE opcodes to bring the *static* scope depth down
        // to `targetDepth`. Does NOT mutate st.ScopeDepth — the caller is
        // about to emit an unconditional jump, so the surrounding
        // compilation continues with the original ScopeDepth.
        private static void EmitPopsDownTo(State st, int targetDepth)
        {
            int n = st.ScopeDepth - targetDepth;
            for (int i = 0; i < n; i++) st.Code.Emit3(Opcode.PopScope, 0, 0, 0);
        }

        // L7: the nearest enclosing context that `continue` / `retry` target —
        // a real loop, skipping any `switch` break-barrier contexts (which only
        // catch `break`). Stack enumerates top → bottom (innermost first).
        private static LoopContext? NearestRealLoop(State st)
        {
            foreach (var ctx in st.Loops)
                if (!ctx.BreakBarrierOnly) return ctx;
            return null;
        }

        private static void EmitPushScope(State st)
        {
            st.Code.Emit3(Opcode.PushScope, 0, 0, 0);
            st.ScopeDepth++;
        }

        private static void EmitPopScope(State st)
        {
            st.Code.Emit3(Opcode.PopScope, 0, 0, 0);
            st.ScopeDepth--;
        }


        // Emits opcodes that evaluate `expr` and leave the result in
        // `destSlot`. Throws IrCompileException for any unsupported subtree.
        private static void CompileExpression(
            AstNode expr, byte destSlot, State st, ref byte topSlot)
        {
            switch (expr.NodeType)
            {
                case AstNodeType.Number:
                    EmitNumberLoad((NumberNode)expr, destSlot, st);
                    return;

                case AstNodeType.Boolean:
                {
                    var bn = (BooleanNode)expr;
                    bool truthy = bn.Token.Value is Keyword kw && kw == Keyword.True;
                    st.Code.Emit3(truthy ? Opcode.LoadTrue : Opcode.LoadFalse, destSlot, 0, 0);
                    return;
                }

                case AstNodeType.Null:
                    st.Code.Emit3(Opcode.LoadNull, destSlot, 0, 0);
                    return;

                case AstNodeType.VariableAccess:
                {
                    var va = (Parser.Nodes.Variables.VariableAccessNode)expr;
                    if (string.IsNullOrEmpty(va.Name))
                        throw new IrCompileException("variable access with empty name");
                    // Hybrid typed accumulator: when a non-redirectable
                    // boxed read of a typed accumulator emerges, publish
                    // the typed slot's current value into the SymbolEntry
                    // — but ONLY when the accumulator is in the compile-
                    // time dirty set. Already-published reads (multiple
                    // accesses after a single typed write) skip the
                    // BoxI + StoreLocalS dispatch + alloc.
                    if (st.TypedAccumulators.TryGetValue(va.Name, out var accHybrid)
                        && accHybrid.Binding.Offset <= ushort.MaxValue
                        && st.DirtyTypedAccs.Contains(va.Name))
                    {
                        byte boxedTmpH = AllocTemp(ref topSlot);
                        st.Code.Emit3(Opcode.BoxI, boxedTmpH, accHybrid.LongSlot, 0);
                        st.Code.Emit2(Opcode.StoreLocalS, boxedTmpH, (ushort)accHybrid.Binding.Offset);
                        st.DirtyTypedAccs.Remove(va.Name);
                    }
                    if (IsSlotEligible(va.Binding, va.BindingKind, st))
                    {
                        st.RegisterSlot(va.Binding.Offset, va.Name);
                        st.Code.Emit2(Opcode.LoadLocalS, destSlot, (ushort)va.Binding.Offset);
                        return;
                    }
                    ushort nameIdx = st.Names.Add(va.Name);
                    st.Code.Emit2(Opcode.LoadGlobal, destSlot, nameIdx);
                    return;
                }

                case AstNodeType.UnaryOperation:
                {
                    var un = (UnaryOperationNode)expr;
                    // M27.1 — Fold `-<constexpr>` at compile time when the subtree is
                    // a pure-arithmetic literal expression. Mirrors the BinaryOperation
                    // folder so `-(2*3)` collapses to LoadConst(-6).
                    if (un.OpTok.Type == TokenType.MINUS && TryConstEvalNumber(un, out var foldedUn))
                    {
                        ushort cidx = st.Consts.Add(foldedUn);
                        st.Code.Emit2(Opcode.LoadConst, destSlot, cidx);
                        return;
                    }
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(un.Node, src, st, ref topSlot);
                    if (un.OpTok.Type == TokenType.MINUS)
                    {
                        st.Code.Emit3(Opcode.Neg, destSlot, src, 0);
                        return;
                    }
                    var unop = MapUnary(un.OpTok);
                    st.Code.Emit3(unop, destSlot, src, 0);
                    return;
                }

                case AstNodeType.BinaryOperation:
                {
                    var bo = (BinaryOperationNode)expr;
                    if (bo.OpTok.Type == TokenType.KEYWORD && bo.OpTok.Value is Keyword kw)
                    {
                        if (kw == Keyword.And)
                        {
                            CompileShortCircuitAnd(bo, destSlot, st, ref topSlot);
                            return;
                        }
                        if (kw == Keyword.Or)
                        {
                            CompileShortCircuitOr(bo, destSlot, st, ref topSlot);
                            return;
                        }
                    }
                    // M27.1 — Compile-time constant folding for literal arithmetic.
                    // Pure `+`, `-`, `*` over two suffix-free NumberValue literals
                    // is value-equivalent at compile time; pre-compute and emit a
                    // single LoadConst so the interpreter doesn't allocate two temp
                    // slots + perform a runtime arithmetic dispatch per execution.
                    // Conservatively skipped for `/`, `%`, `**` (runtime errors must
                    // still trigger at the original source position) and for typed
                    // primitive literals (mixed-type promotion rules differ).
                    if (TryFoldBinaryArith(bo, out var foldedConst))
                    {
                        ushort cidx = st.Consts.Add(foldedConst);
                        st.Code.Emit2(Opcode.LoadConst, destSlot, cidx);
                        return;
                    }
                    // M87: typed-Int64 generalised redirect. Fires when both
                    // operands of an arith/bitwise/compare op reduce to typed
                    // Int64 trees (every leaf is a typed slot or int64 literal).
                    // Covers shapes like `(i % 2) == 0`, `i & 1`, `(i + 5) % N`
                    // that the narrower iter-compare redirect below can't match
                    // because one operand is a nested BinaryOp instead of a
                    // bare `VariableAccess`. Result tag is Int64 (arith /
                    // bitwise) or Bool (compare) — both consumable by
                    // downstream `JmpIfNot` / typed-acc paths without an
                    // intermediate alloc.
                    if (IsTypedBinaryOp(bo.OpTok.Type)
                        && IsTypedInt64Expression(bo, st))
                    {
                        byte lhsT = EmitTypedInt64Operand(bo.LeftNode, st, ref topSlot);
                        byte rhsT = EmitTypedInt64Operand(bo.RightNode, st, ref topSlot);
                        st.Code.Emit3(MapTypedBinary(bo.OpTok.Type), destSlot, lhsT, rhsT);
                        if (IsTypedComparableOp(bo.OpTok.Type))
                            st.RedirectedTypedIterAccessCount++;
                        return;
                    }
                    // Typed-iter comparison fast path. Emits a pure typed II
                    // compare when one operand is `VariableAccess(ActiveTypedIter)`
                    // and the other is one of:
                    //   * const-foldable int64 literal (pre-loaded into
                    //     `TypedAccumulatorLiterals`),
                    //   * `VariableAccess` of a `TypedLongBinding` (a
                    //     never-mutated local pre-loaded into a typed slot).
                    //
                    // Both operands read directly from their typed slots —
                    // no boxed mirror, no iter publish, no per-iter alloc.
                    if (IsTypedComparableOp(bo.OpTok.Type))
                    {
                        bool leftIter = bo.LeftNode is VariableAccessNode lvi
                                        && st.ActiveTypedIters.TryGetValue(lvi.Name, out _);
                        bool rightIter = bo.RightNode is VariableAccessNode rvi
                                         && st.ActiveTypedIters.TryGetValue(rvi.Name, out _);

                        // Path A: iter ⋈ literal-int.
                        if (leftIter && !rightIter
                            && TryGetLiteralLongFromConstExpr(bo.RightNode, out long litR)
                            && st.TypedAccumulatorLiterals.TryGetValue(litR, out byte litRSlot))
                        {
                            string iterNm = ((VariableAccessNode)bo.LeftNode).Name;
                            byte iterSlot = st.ActiveTypedIters[iterNm];
                            var opII = TypedComparisonOpcode(bo.OpTok.Type, swapped: false);
                            st.Code.Emit3(opII, destSlot, iterSlot, litRSlot);
                            st.RedirectedTypedIterAccessCount++;
                            return;
                        }
                        if (rightIter && !leftIter
                            && TryGetLiteralLongFromConstExpr(bo.LeftNode, out long litL)
                            && st.TypedAccumulatorLiterals.TryGetValue(litL, out byte litLSlot))
                        {
                            string iterNm = ((VariableAccessNode)bo.RightNode).Name;
                            byte iterSlot = st.ActiveTypedIters[iterNm];
                            var opII = TypedComparisonOpcode(bo.OpTok.Type, swapped: true);
                            st.Code.Emit3(opII, destSlot, iterSlot, litLSlot);
                            st.RedirectedTypedIterAccessCount++;
                            return;
                        }

                        // Path B: iter ⋈ typed-long binding.
                        if (leftIter && !rightIter
                            && bo.RightNode is VariableAccessNode rBinding
                            && st.TypedLongBindings.TryGetValue(rBinding.Name, out var rTypedBnd))
                        {
                            string iterNm = ((VariableAccessNode)bo.LeftNode).Name;
                            byte iterSlot = st.ActiveTypedIters[iterNm];
                            var opII = TypedComparisonOpcode(bo.OpTok.Type, swapped: false);
                            st.Code.Emit3(opII, destSlot, iterSlot, rTypedBnd.LongSlot);
                            st.RedirectedTypedIterAccessCount++;
                            return;
                        }
                        if (rightIter && !leftIter
                            && bo.LeftNode is VariableAccessNode lBinding
                            && st.TypedLongBindings.TryGetValue(lBinding.Name, out var lTypedBnd))
                        {
                            string iterNm = ((VariableAccessNode)bo.RightNode).Name;
                            byte iterSlot = st.ActiveTypedIters[iterNm];
                            var opII = TypedComparisonOpcode(bo.OpTok.Type, swapped: true);
                            st.Code.Emit3(opII, destSlot, iterSlot, lTypedBnd.LongSlot);
                            st.RedirectedTypedIterAccessCount++;
                            return;
                        }

                        // Path C: iter ⋈ truly-non-integer constant
                        // (e.g. `i < 1.5`). Statically rewrite to
                        // typed integer compare via floor/ceil math.
                        // ConstFalse / ConstTrue paths (==/!= against
                        // non-int) are NOT emitted as LoadFalse /
                        // LoadTrue at this layer because the downstream
                        // LICM hoist sees them as loop-invariant and
                        // moves them, after which their slot identity
                        // can break the surrounding If's JmpIfNot
                        // offset patching. Such comparisons fall back
                        // to boxed dispatch — they're rare in hot loops
                        // and correctness is preserved.
                        if (leftIter && !rightIter)
                        {
                            var r = TryReduceNonIntIterCompareLit(
                                bo.OpTok.Type, bo.RightNode, iterOnLeft: true,
                                out var newOp, out long newLit);
                            if (r == NonIntCompareResult.RewriteOk
                                && st.TypedAccumulatorLiterals.TryGetValue(newLit, out byte nlSlot))
                            {
                                string iterNm = ((VariableAccessNode)bo.LeftNode).Name;
                                byte iterSlot = st.ActiveTypedIters[iterNm];
                                var opII = TypedComparisonOpcode(newOp, swapped: false);
                                st.Code.Emit3(opII, destSlot, iterSlot, nlSlot);
                                st.RedirectedTypedIterAccessCount++;
                                return;
                            }
                        }
                        if (rightIter && !leftIter)
                        {
                            var r = TryReduceNonIntIterCompareLit(
                                bo.OpTok.Type, bo.LeftNode, iterOnLeft: false,
                                out var newOp, out long newLit);
                            if (r == NonIntCompareResult.RewriteOk
                                && st.TypedAccumulatorLiterals.TryGetValue(newLit, out byte nlSlot))
                            {
                                string iterNm = ((VariableAccessNode)bo.RightNode).Name;
                                byte iterSlot = st.ActiveTypedIters[iterNm];
                                // `iterOnLeft=false` already inverted the
                                // direction in `TryReduceNon...`; emit the
                                // rewritten op directly (not swapped).
                                var opII = TypedComparisonOpcode(newOp, swapped: false);
                                st.Code.Emit3(opII, destSlot, iterSlot, nlSlot);
                                st.RedirectedTypedIterAccessCount++;
                                return;
                            }
                        }
                    }
                    var binop = MapBinary(bo.OpTok);
                    byte lhs = AllocTemp(ref topSlot);
                    CompileExpression(bo.LeftNode, lhs, st, ref topSlot);
                    byte rhs = AllocTemp(ref topSlot);
                    CompileExpression(bo.RightNode, rhs, st, ref topSlot);
                    st.Code.Emit3(binop, destSlot, lhs, rhs);
                    return;
                }

                case AstNodeType.Cast:
                {
                    var cast = (CastNode)expr;
                    if (st.CastRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("CastRefs pool exhausted (>65535 cast sites)");
                    int refIdx = st.CastRefs.Count;
                    st.CastRefs.Add(cast);
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(cast.Expression, src, st, ref topSlot);
                    // M82 — Wide prefix when refIdx > 255.
                    st.Code.Emit3WideC(Opcode.Cast, destSlot, src, refIdx);
                    return;
                }

                case AstNodeType.List:
                {
                    var ln = (Parser.Nodes.Primitives.ListNode)expr;
                    CompileCollectionLiteral(ln.ElementNodes, destSlot, Opcode.NewList, st, ref topSlot);
                    return;
                }
                case AstNodeType.Set:
                {
                    var sn = (Parser.Nodes.Primitives.SetNode)expr;
                    CompileCollectionLiteral(sn.ElementNodes, destSlot, Opcode.NewSet, st, ref topSlot);
                    return;
                }
                case AstNodeType.Tuple:
                {
                    var tn = (Parser.Nodes.Primitives.TupleNode)expr;
                    CompileCollectionLiteral(tn.ElementNodes, destSlot, Opcode.NewTuple, st, ref topSlot);
                    return;
                }
                case AstNodeType.Map:
                {
                    var mn = (Parser.Nodes.Primitives.MapNode)expr;
                    int pairCount = mn.Pairs.Count;
                    if (pairCount > byte.MaxValue)
                        throw new IrCompileException("map literal has too many pairs (>255)");
                    byte baseSlot = topSlot;
                    for (int i = 0; i < pairCount * 2; i++) AllocTemp(ref topSlot);
                    for (int i = 0; i < pairCount; i++)
                    {
                        CompileExpression(mn.Pairs[i].Item1, (byte)(baseSlot + 2 * i), st, ref topSlot);
                        CompileExpression(mn.Pairs[i].Item2, (byte)(baseSlot + 2 * i + 1), st, ref topSlot);
                    }
                    st.Code.Emit3(Opcode.NewMap, destSlot, baseSlot, (byte)pairCount);
                    return;
                }
                case AstNodeType.Range:
                {
                    var rn = (RangeNode)expr;
                    bool inclusive = rn.Operator.Type == TokenType.DOUBLE_DOT_EQ;
                    byte baseSlot = topSlot;
                    AllocTemp(ref topSlot); // start
                    AllocTemp(ref topSlot); // end
                    AllocTemp(ref topSlot); // step
                    CompileExpression(rn.Start, baseSlot, st, ref topSlot);
                    CompileExpression(rn.End, (byte)(baseSlot + 1), st, ref topSlot);
                    if (rn.Step != null)
                        CompileExpression(rn.Step, (byte)(baseSlot + 2), st, ref topSlot);
                    else
                    {
                        ushort oneIdx = st.Consts.Add(NumberValue.One);
                        st.Code.Emit2(Opcode.LoadConst, (byte)(baseSlot + 2), oneIdx);
                    }
                    st.Code.Emit3(Opcode.Range, destSlot, baseSlot, inclusive ? (byte)1 : (byte)0);
                    return;
                }
                case AstNodeType.ListAccess:
                {
                    var la = (Parser.Nodes.Variables.ListAccessNode)expr;
                    byte tgtSlot = AllocTemp(ref topSlot);
                    CompileExpression(la.Target, tgtSlot, st, ref topSlot);
                    byte idxSlot = AllocTemp(ref topSlot);
                    CompileExpression(la.Index, idxSlot, st, ref topSlot);
                    st.Code.Emit3(Opcode.ListGet, destSlot, tgtSlot, idxSlot);
                    return;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (TernaryNode)expr;
                    // M22.1: constant-fold a literal ternary condition.
                    bool? tnFold = TryFoldCondition(tn.Condition);
                    if (tnFold == true)
                    {
                        CompileExpression(tn.TrueExpression, destSlot, st, ref topSlot);
                        return;
                    }
                    if (tnFold == false)
                    {
                        CompileExpression(tn.FalseExpression, destSlot, st, ref topSlot);
                        return;
                    }
                    byte condSlot = AllocTemp(ref topSlot);
                    CompileExpression(tn.Condition, condSlot, st, ref topSlot);
                    int jmpElse = st.Code.EmitForwardJump(Opcode.JmpIfNot, condSlot);
                    CompileExpression(tn.TrueExpression, destSlot, st, ref topSlot);
                    int jmpEnd = st.Code.EmitForwardJump(Opcode.Jmp);
                    st.Code.PatchJumpToHere(jmpElse);
                    CompileExpression(tn.FalseExpression, destSlot, st, ref topSlot);
                    st.Code.PatchJumpToHere(jmpEnd);
                    return;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (NullCoalescingNode)expr;
                    byte lhsSlot = AllocTemp(ref topSlot);
                    CompileExpression(nc.Left, lhsSlot, st, ref topSlot);
                    byte rhsSlot = AllocTemp(ref topSlot);
                    CompileExpression(nc.Right, rhsSlot, st, ref topSlot);
                    st.Code.Emit3(Opcode.NullCoal, destSlot, lhsSlot, rhsSlot);
                    return;
                }
                case AstNodeType.String:
                {
                    var sn = (StringNode)expr;
                    if (sn.CachedValue != null)
                    {
                        ushort idx = st.Consts.Add(sn.CachedValue);
                        st.Code.Emit2(Opcode.LoadConst, destSlot, idx);
                        return;
                    }
                    // Determine if all parts are literal (cacheable as StringValue).
                    var parts = sn.Parts;
                    bool allLit = true;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (parts[i].NodeType != AstNodeType.StringPart) { allLit = false; break; }
                    }
                    if (allLit)
                    {
                        string text;
                        if (parts.Count == 1)
                            text = ((Parser.Nodes.Primitives.StringTextNode)parts[0]).Text;
                        else
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < parts.Count; i++)
                                sb.Append(((Parser.Nodes.Primitives.StringTextNode)parts[i]).Text);
                            text = sb.ToString();
                        }
                        sn.CachedValue = new StringValue(text);
                        ushort idx = st.Consts.Add(sn.CachedValue);
                        st.Code.Emit2(Opcode.LoadConst, destSlot, idx);
                        return;
                    }

                    // Interpolated: lay each part into consecutive slots,
                    // then OP_INTERP builds the final string.
                    int count = parts.Count;
                    if (count > byte.MaxValue)
                        throw new IrCompileException("string interpolation has too many parts (>255)");
                    byte baseSlot = topSlot;
                    for (int i = 0; i < count; i++) AllocTemp(ref topSlot);
                    for (int i = 0; i < count; i++)
                    {
                        if (parts[i].NodeType == AstNodeType.StringPart)
                        {
                            var sv = new StringValue(((Parser.Nodes.Primitives.StringTextNode)parts[i]).Text);
                            ushort cidx = st.Consts.Add(sv);
                            st.Code.Emit2(Opcode.LoadConst, (byte)(baseSlot + i), cidx);
                        }
                        else
                        {
                            CompileExpression(parts[i], (byte)(baseSlot + i), st, ref topSlot);
                        }
                    }
                    st.Code.Emit3(Opcode.Interp, destSlot, baseSlot, (byte)count);
                    return;
                }

                case AstNodeType.Self:
                {
                    // `self` is just LoadGlobal "self" — the method-call
                    // machinery (FunctionCallExecutor / BoundMethodValue)
                    // binds self in the method scope via SetLocal before
                    // body execution, so the VM read finds it via parent
                    // walk.
                    ushort nameIdx = st.Names.Add("self");
                    st.Code.Emit2(Opcode.LoadGlobal, destSlot, nameIdx);
                    return;
                }

                case AstNodeType.MemberAccess:
                {
                    var ma = (Parser.Nodes.Structs.MemberAccessNode)expr;
                    if (st.MemberAccessRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("MemberAccessRefs overflow (>65535)");
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(ma.TargetNode, src, st, ref topSlot);
                    int refIdx = st.MemberAccessRefs.Count;
                    st.MemberAccessRefs.Add(ma);
                    // M82 — Wide prefix when refIdx > 255.
                    st.Code.Emit3WideC(Opcode.GetMember, destSlot, src, refIdx);
                    return;
                }

                case AstNodeType.EnumAccess:
                {
                    var ea = (Parser.Nodes.Enums.EnumAccessNode)expr;
                    if (st.EnumAccessRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("EnumAccessRefs overflow (>65535)");
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(ea.EnumNode, src, st, ref topSlot);
                    int refIdx = st.EnumAccessRefs.Count;
                    st.EnumAccessRefs.Add(ea);
                    st.Code.Emit3WideC(Opcode.EnumAccess, destSlot, src, refIdx);
                    return;
                }

                case AstNodeType.Typeof:
                {
                    var tn = (Parser.Nodes.Special.TypeofNode)expr;
                    if (st.TypeofRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("TypeofRefs overflow (>65535)");
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(tn.Node, src, st, ref topSlot);
                    int refIdx = st.TypeofRefs.Count;
                    st.TypeofRefs.Add(tn);
                    st.Code.Emit3WideC(Opcode.Typeof, destSlot, src, refIdx);
                    return;
                }
                case AstNodeType.Nameof:
                {
                    var nn = (Parser.Nodes.Special.NameofNode)expr;
                    if (st.NameofRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("NameofRefs overflow (>65535)");
                    ushort refIdx = (ushort)st.NameofRefs.Count;
                    st.NameofRefs.Add(nn);
                    st.Code.Emit2(Opcode.Nameof, destSlot, refIdx);
                    return;
                }
                case AstNodeType.Dereference:
                {
                    var dn = (Parser.Nodes.Operations.DereferenceNode)expr;
                    if (st.DerefRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DerefRefs overflow (>65535)");
                    byte src = AllocTemp(ref topSlot);
                    CompileExpression(dn.Target, src, st, ref topSlot);
                    int refIdx = st.DerefRefs.Count;
                    st.DerefRefs.Add(dn);
                    // M82 — Wide prefix when refIdx > 255.
                    st.Code.Emit3WideC(Opcode.Deref, destSlot, src, refIdx);
                    return;
                }
                case AstNodeType.Super:
                {
                    var sn = (Parser.Nodes.Classes.SuperNode)expr;
                    if (st.SuperRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("SuperRefs overflow (>65535)");
                    ushort refIdx = (ushort)st.SuperRefs.Count;
                    st.SuperRefs.Add(sn);
                    st.Code.Emit2(Opcode.GetSuper, destSlot, refIdx);
                    return;
                }
                case AstNodeType.FunctionDefinition:
                {
                    var fd = (Parser.Nodes.Functions.FunctionDefinitionNode)expr;
                    if (st.FuncDefRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("FuncDefRefs overflow (>65535)");
                    ushort refIdx = (ushort)st.FuncDefRefs.Count;
                    st.FuncDefRefs.Add(fd);
                    st.Code.Emit2(Opcode.DefineFunction, destSlot, refIdx);
                    return;
                }

                // Long-tail expressions routed via OP_NATIVE_DEFINE — the
                // VM calls the visitor's static Apply directly, never
                // hitting interpreter._visitors[].
                case AstNodeType.DestructuringDeclaration:
                case AstNodeType.TryUnwrap:
                case AstNodeType.Await:
                case AstNodeType.Spawn:
                case AstNodeType.Emit:
                case AstNodeType.ForAwait:
                case AstNodeType.SuperFor:
                case AstNodeType.AsmBlock:
                case AstNodeType.Yield:
                case AstNodeType.AnnotationApplication:
                case AstNodeType.IsType:
                {
                    if (st.DefineRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DefineRefs overflow (>65535)");
                    ushort refIdx = (ushort)st.DefineRefs.Count;
                    st.DefineRefs.Add(expr);
                    st.Code.Emit2(Opcode.NativeDefine, destSlot, refIdx);
                    return;
                }

                // L4: formatted interpolation `${expr:spec}` (only ever emitted
                // by the parser inside an interpolated string, so this is
                // reached via the StringNode part loop). Lower to OP_FMT:
                // compile the inner expression into a temp, intern the parsed
                // FormatSpec as a packed-int const, emit `Fmt dst, exprSlot,
                // specConstIdx`. The spec is built ONCE at compile time — no
                // textual re-parse at runtime, even inside a hot loop. A spec
                // const index that overflows the u8 `c` operand → fallback.
                case AstNodeType.FormattedInterpolation:
                {
                    var fin = (Parser.Nodes.Primitives.FormattedInterpolationNode)expr;
                    byte exprSlot = AllocTemp(ref topSlot);
                    CompileExpression(fin.Expression, exprSlot, st, ref topSlot);
                    int packed = fin.FormatSpec.Pack();
                    ushort specIdx = st.Consts.Add(IntegerValue.Of(packed));
                    if (specIdx > byte.MaxValue)
                        throw new IrCompileException("format-spec const index exceeds u8 -> fallback");
                    st.Code.Emit3(Opcode.Fmt, destSlot, exprSlot, (byte)specIdx);
                    return;
                }

                // L4: record copy-update `recv with { f: v, ... }`. Lower to
                // OP_WITH: lay the receiver into `base` and each update value
                // into the contiguous slots `base+1 .. base+N` (eval order =
                // visitor's: receiver, then values in source order), park the
                // node in DefineRefs for its static field names / positions /
                // declared types, and emit `With dst, base, defineRefIdx`. The
                // shallow-clone + validation + field-set runs in the shared
                // WithExpressionOps helper (identical to the visitor). >255
                // updates, or a DefineRefs index past the u8 `c` operand →
                // fallback.
                case AstNodeType.WithExpression:
                {
                    var wn = (Parser.Nodes.Operations.WithExpressionNode)expr;
                    int count = wn.Updates.Count;
                    if (count > byte.MaxValue - 1)
                        throw new IrCompileException("with-expression has too many updates -> fallback");

                    // Reserve the contiguous band: base (recv), then `count`
                    // value slots. Sub-expression temps allocate ABOVE it.
                    byte baseSlot = AllocTemp(ref topSlot);
                    for (int i = 0; i < count; i++) AllocTemp(ref topSlot);

                    CompileExpression(wn.Receiver, baseSlot, st, ref topSlot);
                    for (int i = 0; i < count; i++)
                        CompileExpression(wn.Updates[i].Item2, (byte)(baseSlot + 1 + i), st, ref topSlot);

                    if (st.DefineRefs.Count > byte.MaxValue)
                        throw new IrCompileException("DefineRefs index exceeds u8 for OP_WITH -> fallback");
                    byte refIdx = (byte)st.DefineRefs.Count;
                    st.DefineRefs.Add(wn);

                    st.Code.Emit3(Opcode.With, destSlot, baseSlot, refIdx);
                    return;
                }

                // L4: `re"pattern"flags`. Pattern + flags are compile-time
                // literals, so build the RegexValue once at compile time and
                // emit a plain LoadConst — zero runtime build cost, even inside
                // a hot loop (beats the visitor's first-iteration compile).
                // An invalid pattern/flags defers to the visitor (fallback) so
                // the exact runtime regex-compile error surfaces, and a
                // dead-code literal never errors.
                case AstNodeType.RegexLiteral:
                {
                    var rxn = (Parser.Nodes.Primitives.RegexLiteralNode)expr;
                    RuntimeValue regexConst;
                    try
                    {
                        var opts = RegexValue.ParseFlags(rxn.Flags);
                        var rx = RegexValue.Compile(rxn.Pattern, opts);
                        regexConst = new RegexValue(rxn.Pattern, rxn.Flags, opts, rx)
                            .SetPos(rxn.PositionStart, rxn.PositionEnd);
                    }
                    catch (System.ArgumentException)
                    {
                        throw new IrCompileException("regex literal failed to compile -> fallback");
                    }
                    ushort rcIdx = st.Consts.Add(regexConst);
                    st.Code.Emit2(Opcode.LoadConst, destSlot, rcIdx);
                    return;
                }

                // L3: `*ref op= value`. Reserve refSlot + valSlot consecutively
                // (the DerefStore handler reads the RHS from refSlot+1), compile
                // both operands in source order, then emit OP_DEREF_STORE with
                // the assignment operator in `c`. Unsupported operators fall back.
                case AstNodeType.DereferenceAssignment:
                {
                    var da = (Parser.Nodes.Operations.DereferenceAssignmentNode)expr;
                    var opTok = da.AssignmentToken.Type;
                    if (!Runtime.DerefStoreOps.IsSupported(opTok))
                        throw new IrCompileException("deref-store: unsupported operator -> fallback");
                    byte refSlot = AllocTemp(ref topSlot);
                    byte valSlot = AllocTemp(ref topSlot); // == refSlot + 1 (contiguous)
                    CompileExpression(da.RefTarget, refSlot, st, ref topSlot);
                    CompileExpression(da.ValueNode, valSlot, st, ref topSlot);
                    st.Code.Emit3(Opcode.DerefStore, destSlot, refSlot, (byte)opTok);
                    return;
                }

                // L3: `&name` / `&mut name`. Bare-variable, lifetime-free borrows
                // lower to OP_BORROW / OP_BORROW_MUT carrying the name's Names[]
                // index; the dispatch handler resolves the SymbolEntry and runs
                // BorrowOps.TryBorrow. Member / index targets (rejected at runtime
                // anyway) and explicit lifetimes fall back to OP_NATIVE_DEFINE.
                case AstNodeType.Borrow:
                {
                    var bn = (Parser.Nodes.Operations.BorrowNode)expr;
                    if (bn.Lifetime != null || bn.Target.NodeType != AstNodeType.VariableAccess)
                        throw new IrCompileException("borrow: explicit lifetime or non-variable target -> fallback");
                    var bva = (Parser.Nodes.Variables.VariableAccessNode)bn.Target;
                    string bname = bva.VarNameTok.Value?.ToString() ?? "";
                    if (string.IsNullOrEmpty(bname))
                        throw new IrCompileException("borrow: empty target name -> fallback");
                    ushort bNameIdx = st.Names.Add(bname);
                    st.Code.Emit2(bn.IsMutable ? Opcode.BorrowMut : Opcode.Borrow, destSlot, bNameIdx);
                    return;
                }

                case AstNodeType.FunctionCall:
                {
                    var fc = (FunctionCallNode)expr;
                    if (!IsCallNativelyCompilable(fc))
                        throw new IrCompileException("call has named/ref/spread/generic args -> fallback");
                    int argCount = fc.ArgNodes.Count;
                    if (argCount > byte.MaxValue)
                        throw new IrCompileException("call has too many args (>255)");

                    // Reserve consecutive slots: fnSlot, then argCount slots
                    // for positional args. Any sub-expression temps allocate
                    // ABOVE this band so the contiguous layout the VM's
                    // OP_CALL relies on is preserved.
                    byte fnSlot = AllocTemp(ref topSlot);
                    byte argsBase = (byte)(fnSlot + 1);
                    for (int i = 0; i < argCount; i++) AllocTemp(ref topSlot);

                    CompileExpression(fc.NodeToCall, fnSlot, st, ref topSlot);
                    for (int i = 0; i < argCount; i++)
                    {
                        CompileExpression(fc.ArgNodes[i].Expr, (byte)(argsBase + i), st, ref topSlot);
                    }
                    st.Code.Emit3(Opcode.Call, destSlot, fnSlot, (byte)argCount);
                    return;
                }

                // L4: pipeline `x |> f(a,b)` desugars to a direct OP_CALL
                // `f(x, a, b)` (and `x |> f` to `f(x)`) — reusing the existing
                // call dispatch, ZERO new opcodes / side-tables / rewriter or
                // .rac changes. The eval order mirrors the visitor EXACTLY:
                // the LHS is evaluated once FIRST (prepended arg0), then the
                // callee, then the RHS call's own positional args.
                //
                // Named / generic / spread / ref RHS-call args are not natively
                // compilable -> fallback (matches the plain FunctionCall path).
                //
                // (Earlier this was gated to non-script frames to dodge a
                // `for x in stream` spin after many stream pipelines; that was a
                // latent SCCP bug — ForEachStreamPull's continueSlot was an
                // unmodelled 2nd SSA def, so a stale constant in the reused slot
                // let SCCP fold the loop-exit branch. Fixed in SsaForm /
                // Sccp via SecondaryDefinedSlot; the gate is no longer needed.)
                case AstNodeType.Pipeline:
                {
                    var pn = (Parser.Nodes.Operations.PipelineNode)expr;

                    FunctionCallNode? rhsCall = pn.RightNode as FunctionCallNode;
                    int rhsArgCount = 0;
                    AstNode calleeNode;
                    if (rhsCall != null)
                    {
                        if (!IsCallNativelyCompilable(rhsCall))
                            throw new IrCompileException("pipeline RHS call has named/ref/spread/generic args -> fallback");
                        rhsArgCount = rhsCall.ArgNodes.Count;
                        calleeNode = rhsCall.NodeToCall;
                    }
                    else
                    {
                        calleeNode = pn.RightNode;
                    }

                    int totalArgs = rhsArgCount + 1; // piped LHS prepended as arg0
                    if (totalArgs > byte.MaxValue)
                        throw new IrCompileException("pipeline produces too many args (>255)");

                    // Reserve the contiguous OP_CALL band: fnSlot, then
                    // totalArgs positional slots. Sub-expression temps allocate
                    // ABOVE the band so the layout the VM relies on is intact.
                    byte fnSlot = AllocTemp(ref topSlot);
                    byte argsBase = (byte)(fnSlot + 1);
                    for (int i = 0; i < totalArgs; i++) AllocTemp(ref topSlot);

                    // (1) LHS once -> arg0.
                    CompileExpression(pn.LeftNode, argsBase, st, ref topSlot);
                    // (2) callee -> fnSlot.
                    CompileExpression(calleeNode, fnSlot, st, ref topSlot);
                    // (3) RHS call's own args -> arg1..argN.
                    if (rhsCall != null)
                    {
                        for (int i = 0; i < rhsArgCount; i++)
                            CompileExpression(rhsCall.ArgNodes[i].Expr, (byte)(argsBase + 1 + i), st, ref topSlot);
                    }

                    st.Code.Emit3(Opcode.Call, destSlot, fnSlot, (byte)totalArgs);
                    return;
                }

                // L7: expression-position switch (`let y = switch x { … }`).
                // Lowers the arrow-expr subset inline; throws → whole-statement
                // OP_NATIVE_DEFINE fallback (as before this case existed).
                case AstNodeType.Switch:
                    CompileSwitchExpr((SwitchNode)expr, destSlot, st, ref topSlot);
                    return;

                // L7: expression-position match (`let y = match x { … }`).
                // Lowers the literal/wildcard subset inline; on a non-lowerable
                // match, fall back at the EXPRESSION level to OP_NATIVE_DEFINE
                // (into destSlot) rather than propagating the throw — so the
                // SURROUNDING statement (e.g. `ret match …`) still lowers. (This
                // mirrors the long-tail expression-native group above, which
                // used to carry Match.)
                case AstNodeType.Match:
                {
                    int savedPc = st.Code.Pc;
                    byte savedTop = topSlot;
                    int savedRefs = st.DefineRefs.Count;
                    try
                    {
                        CompileMatchExpr((Parser.Nodes.Patterns.MatchNode)expr, destSlot, st, ref topSlot);
                        return;
                    }
                    catch (IrCompileException)
                    {
                        st.Code.Truncate(savedPc);
                        topSlot = savedTop;
                        if (st.DefineRefs.Count > savedRefs)
                            st.DefineRefs.RemoveRange(savedRefs, st.DefineRefs.Count - savedRefs);
                    }
                    if (st.DefineRefs.Count > ushort.MaxValue)
                        throw new IrCompileException("DefineRefs overflow (>65535)");
                    ushort mRefIdx = (ushort)st.DefineRefs.Count;
                    st.DefineRefs.Add(expr);
                    st.Code.Emit2(Opcode.NativeDefine, destSlot, mRefIdx);
                    return;
                }

                default:
                    throw new IrCompileException($"unsupported expression node: {expr.NodeType}");
            }
        }

        private static void CompileShortCircuitAnd(
            BinaryOperationNode bo, byte destSlot, State st, ref byte topSlot)
        {
            CompileExpression(bo.LeftNode, destSlot, st, ref topSlot);
            int j1 = st.Code.EmitForwardJump(Opcode.AndJz, destSlot);
            CompileExpression(bo.RightNode, destSlot, st, ref topSlot);
            int j2 = st.Code.EmitForwardJump(Opcode.AndJz, destSlot);
            st.Code.Emit3(Opcode.LoadTrue, destSlot, 0, 0);
            int jEnd = st.Code.EmitForwardJump(Opcode.Jmp);
            st.Code.PatchJumpToHere(j1);
            st.Code.PatchJumpToHere(j2);
            st.Code.Emit3(Opcode.LoadFalse, destSlot, 0, 0);
            st.Code.PatchJumpToHere(jEnd);
        }

        private static void CompileShortCircuitOr(
            BinaryOperationNode bo, byte destSlot, State st, ref byte topSlot)
        {
            CompileExpression(bo.LeftNode, destSlot, st, ref topSlot);
            int j1 = st.Code.EmitForwardJump(Opcode.OrJnz, destSlot);
            CompileExpression(bo.RightNode, destSlot, st, ref topSlot);
            int j2 = st.Code.EmitForwardJump(Opcode.OrJnz, destSlot);
            st.Code.Emit3(Opcode.LoadFalse, destSlot, 0, 0);
            int jEnd = st.Code.EmitForwardJump(Opcode.Jmp);
            st.Code.PatchJumpToHere(j1);
            st.Code.PatchJumpToHere(j2);
            st.Code.Emit3(Opcode.LoadTrue, destSlot, 0, 0);
            st.Code.PatchJumpToHere(jEnd);
        }

        private static void EmitNumberLoad(NumberNode node, byte destSlot, State st)
        {
            if (node.CachedValue == null)
                node.CachedValue = ParseNumberLiteralForIr(node);
            ushort idx = st.Consts.Add(node.CachedValue);
            st.Code.Emit2(Opcode.LoadConst, destSlot, idx);
        }

        private static RuntimeValue ParseNumberLiteralForIr(NumberNode node)
        {
            var raw = node.Tok.Value?.ToString() ?? "";
            if (raw.Length == 0) throw new IrCompileException("empty number literal");

            // M24: delegate to the canonical NumberNodeVisitor.ParseLiteral
            // so suffix / base-prefix handling stays in one place. Earlier
            // path rejected suffixed numerics so the IrExpressionEvaluator
            // wrapper fell back to OP_NATIVE_DEFINE — but the dispatch
            // switch has no case for plain Number nodes, so we'd hit
            // "VM: NativeDefine unsupported NodeType Number" at runtime.
            return Visitors.Primitives.NumberNodeVisitor.ParseLiteral(node);
        }

        // M27.1 — Compile-time folder for `literal + literal`, `literal - literal`,
        // `literal * literal` over suffix-free numeric literals that resolve to
        // `NumberValue`. Recurses through nested arithmetic + unary-minus subtrees
        // so multi-term constant expressions (`2 + 3 * 4`, `-5 + 1`) collapse to a
        // single LoadConst. Typed primitives (`1.5f`, `10us`) are left alone — their
        // promotion rules differ from the pure-NumberValue path. Division/modulo/
        // power and bitwise ops are excluded because runtime errors must surface at
        // the original source position; only purely-total arithmetic is folded.
        private static bool TryFoldBinaryArith(BinaryOperationNode bo, out RuntimeValue folded)
        {
            folded = null!;
            if (!TryConstEvalNumber(bo, out var nv)) return false;
            folded = nv;
            return true;
        }

        private static bool TryConstEvalNumber(AstNode node, out NumberValue value)
        {
            switch (node)
            {
                case NumberNode nn:
                {
                    var v = nn.CachedValue ?? NumberNodeVisitor.ParseLiteral(nn);
                    nn.CachedValue = v;
                    if (v is NumberValue nv) { value = nv; return true; }
                    value = null!;
                    return false;
                }
                case BinaryOperationNode bo:
                {
                    var t = bo.OpTok.Type;
                    if (t != TokenType.PLUS && t != TokenType.MINUS && t != TokenType.MUL) { value = null!; return false; }
                    if (!TryConstEvalNumber(bo.LeftNode, out var l) || !TryConstEvalNumber(bo.RightNode, out var r))
                    { value = null!; return false; }
                    BigNumber result = t switch
                    {
                        TokenType.PLUS  => l.Value + r.Value,
                        TokenType.MINUS => l.Value - r.Value,
                        TokenType.MUL   => l.Value * r.Value,
                        _ => default,
                    };
                    value = NumberValue.OfBigNumber(result);
                    return true;
                }
                case UnaryOperationNode un when un.OpTok.Type == TokenType.MINUS:
                {
                    if (!TryConstEvalNumber(un.Node, out var inner)) { value = null!; return false; }
                    value = NumberValue.OfBigNumber(BigNumber.Zero - inner.Value);
                    return true;
                }
                default:
                    value = null!;
                    return false;
            }
        }

        // M28.3 — Emit OP_TAIL_CALL for a FunctionCallNode in tail position.
        // The opcode layout `[op][a:fn][b:argBase][c:argCount]` requires the
        // callee + positional args to occupy a contiguous slot band, matching
        // the OP_CALL convention. Compile the callee into fnSlot, the args
        // into the next argCount slots, then emit. Returns false if any
        // sub-compilation throws — caller falls back to OP_CALL + OP_RET.
        private static bool TryEmitTailCall(FunctionCallNode fc, State st, ref byte topSlot)
        {
            int argCount = fc.ArgNodes.Count;
            int savedPc = st.Code.Pc;
            byte savedTop = topSlot;
            try
            {
                byte fnSlot = AllocTemp(ref topSlot);
                byte argsBase = (byte)(fnSlot + 1);
                for (int i = 0; i < argCount; i++) AllocTemp(ref topSlot);
                CompileExpression(fc.NodeToCall, fnSlot, st, ref topSlot);
                for (int i = 0; i < argCount; i++)
                {
                    CompileExpression(fc.ArgNodes[i].Expr, (byte)(argsBase + i), st, ref topSlot);
                }
                st.Code.Emit3(Opcode.TailCall, fnSlot, argsBase, (byte)argCount);
                return true;
            }
            catch (IrCompileException)
            {
                st.Code.Truncate(savedPc);
                topSlot = savedTop;
                return false;
            }
        }

        private static Opcode MapBinary(Token op)
        {
            return op.Type switch
            {
                TokenType.PLUS              => Opcode.Add,
                TokenType.MINUS             => Opcode.Sub,
                TokenType.MUL               => Opcode.Mul,
                TokenType.DIV               => Opcode.Div,
                TokenType.MODULO            => Opcode.Mod,
                TokenType.POW               => Opcode.Pow,
                TokenType.BITWISE_LEFT_SHIFT  => Opcode.Shl,
                TokenType.BITWISE_RIGHT_SHIFT => Opcode.Shr,
                // `<<<` shares Opcode.Shl — identical bit pattern, distinct
                // only at the token level so the parser can preserve the
                // unsigned intent for diagnostics. (See RA_SHIFTS_DESIGN.md.)
                TokenType.BITWISE_LOGICAL_LEFT_SHIFT  => Opcode.Shl,
                TokenType.BITWISE_LOGICAL_RIGHT_SHIFT => Opcode.Ushr,
                TokenType.BITWISE_ROTATE_LEFT         => Opcode.Rol,
                TokenType.BITWISE_ROTATE_RIGHT        => Opcode.Ror,
                TokenType.BITWISE_AND       => Opcode.BAnd,
                TokenType.BITWISE_OR        => Opcode.BOr,
                TokenType.EE                => Opcode.Eq,
                TokenType.NE                => Opcode.Ne,
                TokenType.STRICT_EE         => Opcode.SEq,
                TokenType.STRICT_NE         => Opcode.SNe,
                TokenType.LT                => Opcode.Lt,
                TokenType.LTE               => Opcode.Le,
                TokenType.GT                => Opcode.Gt,
                TokenType.GTE               => Opcode.Ge,
                _ => throw new IrCompileException($"binary op {op.Type} not yet lowered"),
            };
        }

        private static Opcode MapUnary(Token op)
        {
            return op.Type switch
            {
                TokenType.BITWISE_NOT => Opcode.BNot,
                TokenType.KEYWORD when op.Value is Keyword kw && kw == Keyword.Not => Opcode.Not,
                _ => throw new IrCompileException($"unary op {op.Type} not yet lowered"),
            };
        }

        private static byte AllocTemp(ref byte topSlot)
        {
            if (topSlot == byte.MaxValue)
                throw new IrCompileException("temp-slot allocator exhausted (>255 in a single expression)");
            return topSlot++;
        }

        // Recursive AST scan: returns true iff `node` contains any
        // VariableAccess of the given `name`, OR any nested function /
        // class / closure / try body that could capture/reference it.
        // Used by CompileFor to elide the per-iter `AssignBinding`
        // publish when the loop iter name is dead in the body.
        //
        // Collects candidate variables in `body` that can be lifted to a
        // typed Int64 slot for the for-loop's lifetime. A candidate must:
        //   1. Be the LHS of at least one self-additive assignment whose
        //      RHS is the active typed iter (so the whole RHS evaluates
        //      to a known long without any boxed lookup).
        //   2. Have EVERY other appearance in the body also be inside
        //      such a redirectable self-additive (i.e. no `print(acc)`,
        //      no `if acc > 0`, no nested assignment to `acc` outside
        //      this shape).
        //   3. Not be redeclared in the body (would shadow the outer
        //      binding; semantics would diverge).
        //   4. Have a slot-eligible Resolver binding (frame-local).
        //
        // Returns the de-duplicated list of qualifying candidates.
        private static List<(string Name, BindingId Binding)> CollectTypedAccumulatorCandidates(
            AstNode body, string iterName, State st)
        {
            var result = new List<(string Name, BindingId Binding)>();
            // Step 1: gather LHS names of self-additive assignments
            // whose RHS reduces to the active typed iter.
            var seen = new HashSet<string>();
            GatherAccumulatorAssignmentNames(body, body, iterName, st, seen);
            if (seen.Count == 0) return result;

            // Step 2: for each candidate, verify it's safe to promote.
            // The strict gate (all accesses redirectable) is split into:
            //   (a) NO non-redirectable WRITE — guarantees the typed
            //       slot stays the source of truth.
            //   (b) Non-redirectable READS are allowed; each one gets a
            //       lazy `BoxI + StoreLocalS` publish emitted before the
            //       boxed access (see `CompileExpression` VariableAccess
            //       case). This unlocks bodies like `sum = sum + i;
            //       print(sum);` and `if cond { print(sum); }`.
            //   (c) No `var name` declaration in body (shadowing).
            //   (d) Slot-eligible Resolver binding.
            foreach (var name in seen)
            {
                // Soundness gate: only promote an accumulator to a typed
                // Int64 slot when its binding is provably numeric. A string
                // accumulator (`var out = ""`) would otherwise UnboxI to 0
                // and miscompile `out = out + x` into a numeric AddII.
                if (!st.NumericInitBindings.Contains(name)) continue;
                if (HasNonRedirectableAccumulatorWrite(body, body, name, iterName, st)) continue;
                if (BodyDeclaresName(body, name)) continue;
                var binding = FindFirstBindingOfName(body, name);
                if (!binding.IsResolved) continue;
                if (!IsSlotEligible(binding, BindingKind.Local, st)
                    && !IsSlotEligible(binding, BindingKind.Global, st)
                    && !IsSlotEligible(binding, BindingKind.Parameter, st))
                    continue;
                result.Add((name, binding));
            }
            return result;
        }

        // Recursively walks `body`, adding the LHS name to `out` for every
        // `LHS = LHS ± rhs` assignment whose `rhs` is either:
        //   1. A `VariableAccessNode` with name == `iterName` (the lazy-
        //      long for-loop iter binding), OR
        //   2. A constant-foldable expression that yields an `int64`
        //      literal value (e.g. `1`, `-1`, `10 - 7`).
        // Both forms compile to a pure `AddII / SubII` against typed slots
        // — the iter slot for (1), or a pre-loaded literal slot for (2).
        private static void GatherAccumulatorAssignmentNames(
            AstNode? node, AstNode bodyRoot, string iterName, State st, HashSet<string> outNames)
        {
            if (node == null) return;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) GatherAccumulatorAssignmentNames(c, bodyRoot, iterName, st, outNames);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                        GatherAccumulatorAssignmentNames(cs.Expr, bodyRoot, iterName, st, outNames);
                    if (ifn.ElseCase.HasValue)
                        GatherAccumulatorAssignmentNames(ifn.ElseCase.Value.Expr, bodyRoot, iterName, st, outNames);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return;
                    if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return;
                    var opT = bo.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return;
                    if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return;
                    if (lvn.Name != va.Name) return;
                    // RHS: VariableAccess to typed iter, OR constant int64 literal.
                    if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvn
                        && rvn.Name == iterName)
                    {
                        outNames.Add(va.Name);
                        return;
                    }
                    if (TryGetLiteralLongFromConstExpr(bo.RightNode, out _))
                    {
                        outNames.Add(va.Name);
                        return;
                    }
                    // Loop-invariant pure-expression RHS (e.g.
                    // `(a + b) * c` where a, b, c are never reassigned
                    // in the loop body). Admits the LHS as a typed
                    // accumulator candidate; the pre-loop setup
                    // compiles the RHS once and stashes it in
                    // `TypedAccumulatorExprs`.
                    if (IsLoopInvariantPureNumericExpr(bo.RightNode, bodyRoot, iterName, va.Name, st))
                    {
                        outNames.Add(va.Name);
                        return;
                    }
                    // M87: RHS = another typed-acc-candidate. Chains like
                    // `b = b + a` where `a` is itself an iter-additive
                    // accumulator (`a = a + i`) become pure typed AddII
                    // when both ends are typed-promoted. The chain check
                    // (`IsTypedAccumulatorCandidateName`) confirms the
                    // RHS will be promoted alongside; the caller's
                    // post-collection validator (HasNonRedirectable...)
                    // still gates the LHS final admission.
                    if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvnChain
                        && rvnChain.Name != va.Name
                        && rvnChain.Name != iterName
                        && IsTypedAccumulatorCandidateName(bodyRoot, rvnChain.Name, iterName, st))
                    {
                        outNames.Add(va.Name);
                        return;
                    }
                    return;
                }
                // Nested loops, sub-blocks, etc. don't contribute candidates
                // (their bodies handled by their own CompileFor / CompileWhile).
                default:
                    return;
            }
        }

        // Pure / loop-invariant numeric expression predicate. Used by
        // typed-accumulator promotion to admit RHS shapes like
        // `(a + b) * c` where every free name resolves to a binding
        // that is NEVER reassigned anywhere in the for-loop body.
        //
        // Returns true iff `node` is a tree of:
        //   * Number / Boolean literals (Boolean coerced to int via
        //     UnboxI's fallback if the runtime semantics expect it).
        //   * VariableAccess to a name that:
        //       - is not the loop iter,
        //       - is not the accumulator itself,
        //       - has no `VariableAssignment` to it anywhere in
        //         `bodyRoot`, AND
        //       - has no `VariableDeclaration` re-shadowing it inside
        //         `bodyRoot` (would create a per-iter SE replacement).
        //   * UnaryOperation Neg / BNot of such.
        //   * BinaryOperation Add / Sub / Mul / Shl / Shr / BAnd /
        //     BOr / BXor of such (Div / Mod excluded — error edges).
        private static bool IsLoopInvariantPureNumericExpr(
            AstNode? node, AstNode bodyRoot, string iterName, string accName, State st)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                    return true;
                case AstNodeType.VariableAccess:
                {
                    var vn = (Parser.Nodes.Variables.VariableAccessNode)node;
                    if (string.IsNullOrEmpty(vn.Name)) return false;
                    if (vn.Name == iterName) return false;
                    if (vn.Name == accName) return false;
                    if (HasAnyAssignmentTo(bodyRoot, vn.Name)) return false;
                    if (BodyDeclaresName(bodyRoot, vn.Name)) return false;
                    return true;
                }
                case AstNodeType.UnaryOperation:
                {
                    var un = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    var opT = un.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.MINUS
                        && opT != Lexer.Tokens.TokenType.PLUS
                        && opT != Lexer.Tokens.TokenType.BITWISE_NOT)
                        return false;
                    return IsLoopInvariantPureNumericExpr(un.Node, bodyRoot, iterName, accName, st);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    var opT = bo.OpTok.Type;
                    // Arith / shift / bitwise — no error edges, safe to
                    // hoist as a single pre-loop evaluation.
                    bool safe = opT == Lexer.Tokens.TokenType.PLUS
                        || opT == Lexer.Tokens.TokenType.MINUS
                        || opT == Lexer.Tokens.TokenType.MUL
                        || opT == Lexer.Tokens.TokenType.BITWISE_LEFT_SHIFT
                        || opT == Lexer.Tokens.TokenType.BITWISE_RIGHT_SHIFT
                        || opT == Lexer.Tokens.TokenType.BITWISE_LOGICAL_LEFT_SHIFT
                        || opT == Lexer.Tokens.TokenType.BITWISE_LOGICAL_RIGHT_SHIFT
                        || opT == Lexer.Tokens.TokenType.BITWISE_ROTATE_LEFT
                        || opT == Lexer.Tokens.TokenType.BITWISE_ROTATE_RIGHT
                        || opT == Lexer.Tokens.TokenType.BITWISE_AND
                        || opT == Lexer.Tokens.TokenType.BITWISE_OR;
                    // M88 (#29): Div / Mod / Pow admit only when the
                    // loop is statically known (or runtime-guarded) to
                    // execute its body at least once. Then any error
                    // their pre-load evaluation raises is one the
                    // original boxed dispatch would also raise on
                    // iteration 0 — diagnostic-equivalent at the
                    // user's eyes. Comparisons (EE / NE / LT / LE /
                    // GT / GE) excluded — they yield Bool, not Int64.
                    bool errorEdge = opT == Lexer.Tokens.TokenType.DIV
                        || opT == Lexer.Tokens.TokenType.MODULO
                        || opT == Lexer.Tokens.TokenType.POW;
                    if (errorEdge && st.LoopGuaranteedToEnter) safe = true;
                    if (!safe) return false;
                    return IsLoopInvariantPureNumericExpr(bo.LeftNode, bodyRoot, iterName, accName, st)
                        && IsLoopInvariantPureNumericExpr(bo.RightNode, bodyRoot, iterName, accName, st);
                }
                default:
                    return false;
            }
        }

        // Counts how many times `name` appears as the LHS read of a
        // redirectable self-additive `name = name ± iterName` assignment.
        // Each such pattern contributes 1 VariableAccess of `name` (the
        // LHS read inside the binary op).
        private static int CountSelfAdditivePatternLhsAccess(
            AstNode? node, string name, string iterName, State st)
        {
            if (node == null) return 0;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    int sum = 0;
                    foreach (var c in sc.Nodes)
                        sum += CountSelfAdditivePatternLhsAccess(c, name, iterName, st);
                    return sum;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    int sum = 0;
                    foreach (var cs in ifn.Cases)
                        sum += CountSelfAdditivePatternLhsAccess(cs.Expr, name, iterName, st);
                    if (ifn.ElseCase.HasValue)
                        sum += CountSelfAdditivePatternLhsAccess(ifn.ElseCase.Value.Expr, name, iterName, st);
                    return sum;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return 0;
                    if (va.Name != name) return 0;
                    if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return 0;
                    var opT = bo.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return 0;
                    if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return 0;
                    if (lvn.Name != name) return 0;
                    // Match either typed-iter RHS or constant int64 RHS.
                    if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvn
                        && rvn.Name == iterName)
                        return 1;
                    if (TryGetLiteralLongFromConstExpr(bo.RightNode, out _))
                        return 1;
                    return 0;
                }
                default:
                    return 0;
            }
        }

        // Returns true iff `body` declares (var/let/const/final) a binding
        // with name `name` anywhere in its subtree. Used to guard against
        // shadowing the outer accumulator.
        private static bool BodyDeclaresName(AstNode? node, string name)
        {
            if (node == null) return false;
            if (node.NodeType == AstNodeType.VariableDeclaration)
            {
                var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                foreach (var d in vd.Declarations)
                {
                    string? declName = d.Item1.Value?.ToString();
                    if (declName == name) return true;
                }
                return false;
            }
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) if (BodyDeclaresName(c, name)) return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                        if (BodyDeclaresName(cs.Expr, name)) return true;
                    if (ifn.ElseCase.HasValue && BodyDeclaresName(ifn.ElseCase.Value.Expr, name)) return true;
                    return false;
                }
                default:
                    return false;
            }
        }

        // Finds the first `VariableAccessNode` matching `name` in `node`
        // and returns its Resolver-assigned BindingId. Used to recover
        // the accumulator's binding without re-running the Resolver.
        private static BindingId FindFirstBindingOfName(AstNode? node, string name)
        {
            if (node == null) return BindingId.Unresolved;
            if (node is Parser.Nodes.Variables.VariableAccessNode va && va.Name == name)
                return va.Binding;
            if (node is Parser.Nodes.Variables.VariableAssignmentNode vasn && vasn.Name == name)
                return vasn.Binding;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                    {
                        var r = FindFirstBindingOfName(c, name);
                        if (r.IsResolved) return r;
                    }
                    return BindingId.Unresolved;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        var r1 = FindFirstBindingOfName(cs.Condition, name); if (r1.IsResolved) return r1;
                        var r2 = FindFirstBindingOfName(cs.Expr, name); if (r2.IsResolved) return r2;
                    }
                    if (ifn.ElseCase.HasValue)
                    {
                        var r = FindFirstBindingOfName(ifn.ElseCase.Value.Expr, name);
                        if (r.IsResolved) return r;
                    }
                    return BindingId.Unresolved;
                }
                case AstNodeType.VariableAssignment:
                {
                    var vasn2 = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    var r = FindFirstBindingOfName(vasn2.ValueNode, name);
                    return r;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    var r1 = FindFirstBindingOfName(bo.LeftNode, name); if (r1.IsResolved) return r1;
                    return FindFirstBindingOfName(bo.RightNode, name);
                }
                default:
                    return BindingId.Unresolved;
            }
        }

        // Returns true iff `va` matches the redirectable self-additive
        // shape `name = name ± typedRhs` (where typedRhs is either the
        // active typed iter or a const-foldable int64 literal). Used
        // by `HasNonRedirectableAccumulatorWrite` to classify writes.
        private static bool IsRedirectableSelfAdditive(
            Parser.Nodes.Variables.VariableAssignmentNode va, string name, string iterName,
            AstNode? bodyRoot, State? st)
        {
            if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return false;
            if (va.Name != name) return false;
            if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return false;
            var opT = bo.OpTok.Type;
            if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return false;
            if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return false;
            if (lvn.Name != name) return false;
            if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvn
                && rvn.Name == iterName) return true;
            if (TryGetLiteralLongFromConstExpr(bo.RightNode, out _)) return true;
            // M87: RHS = another acc / typed-binding. The other acc must
            // itself be a redirectable accumulator OR a never-mutated
            // local — checked indirectly via `IsTypedAccumulatorCandidateName`
            // when `bodyRoot != null` so the caller's two-phase collection
            // (find candidates, then validate) accepts the chain.
            if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvnAccChain
                && bodyRoot != null
                && rvnAccChain.Name != name
                && rvnAccChain.Name != iterName
                && IsTypedAccumulatorCandidateName(bodyRoot, rvnAccChain.Name, iterName, st))
                return true;
            // Loop-invariant pure-expression RHS — redirectable via
            // the pre-loop typed slot path. Only validated when both
            // bodyRoot and st are provided; legacy call sites pass
            // null and skip this branch.
            if (bodyRoot != null && st != null
                && IsLoopInvariantPureNumericExpr(bo.RightNode, bodyRoot, iterName, name, st))
                return true;
            return false;
        }

        // M87: cheap recursive predicate — returns true if `name` would be
        // admitted as a typed accumulator candidate or as a typed-long
        // binding (never-mutated local). Used by the chain-redirectable
        // gate in `IsRedirectableSelfAdditive` so `b = b + a` is accepted
        // when `a` itself is a redirectable accumulator (`a = a + iter`).
        //
        // Conservative: does NOT call `CollectTypedAccumulatorCandidates`
        // recursively (would cycle). Instead inspects each assignment to
        // `name` for the canonical redirectable shapes.
        private static bool IsTypedAccumulatorCandidateName(
            AstNode bodyRoot, string name, string iterName, State? st)
        {
            // Body-declared names are rejected by
            // `CollectTypedAccumulatorCandidates.BodyDeclaresName`, so
            // the chain check must reject them too — otherwise the LHS
            // of `b = b + loc` (where `loc` is `var loc = ...` inside
            // the body) is admitted as a redirectable acc, gets a
            // typed slot, and the post-loop box-back overwrites whatever
            // the boxed `AddIntoSlot` actually computed.
            if (BodyDeclaresName(bodyRoot, name)) return false;
            // Never-mutated bindings are valid typed-long sources.
            if (!HasAnyAssignmentTo(bodyRoot, name)) return true;
            return IsCandidateAccumulatorOnly(bodyRoot, bodyRoot, name, iterName, st);
        }

        // Walks `node` and returns true iff every write to `name` is one of
        // the canonical typed-accumulator shapes:
        //   name = name ± iter        (iter-redirect)
        //   name = name ± literal     (literal-redirect)
        //   name = name ± (invariant) (invariant-expr-redirect)
        // Used by `IsTypedAccumulatorCandidateName` without the recursion
        // hazard of `HasNonRedirectableAccumulatorWrite` (which itself
        // calls `IsRedirectableSelfAdditive` and would loop on chained
        // accumulators).
        private static bool IsCandidateAccumulatorOnly(
            AstNode? node, AstNode bodyRoot, string name, string iterName, State? st)
        {
            if (node == null) return true;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        if (!IsCandidateAccumulatorOnly(c, bodyRoot, name, iterName, st)) return false;
                    return true;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (!IsCandidateAccumulatorOnly(cs.Condition, bodyRoot, name, iterName, st)) return false;
                        if (!IsCandidateAccumulatorOnly(cs.Expr, bodyRoot, name, iterName, st)) return false;
                    }
                    if (ifn.ElseCase.HasValue
                        && !IsCandidateAccumulatorOnly(ifn.ElseCase.Value.Expr, bodyRoot, name, iterName, st))
                        return false;
                    return true;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.Name == name)
                    {
                        if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return false;
                        // A plain const-int64 reset (e.g. `b = 1` inside a
                        // conditional) is redirectable to the typed slot by the
                        // assignment lowering — accept it here so it does not
                        // demote the accumulator chain. Stays in lock-step with
                        // the same const-int64 test in the lowering and in
                        // HasNonRedirectableAccumulatorWrite.
                        if (TryGetLiteralLongFromConstExpr(va.ValueNode, out _))
                            return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st);
                        if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return false;
                        var opT = bo.OpTok.Type;
                        if (opT != Lexer.Tokens.TokenType.PLUS
                            && opT != Lexer.Tokens.TokenType.MINUS) return false;
                        if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return false;
                        if (lvn.Name != name) return false;
                        if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvn
                            && rvn.Name == iterName) { return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st); }
                        if (TryGetLiteralLongFromConstExpr(bo.RightNode, out _))
                            return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st);
                        // RHS could itself be a typed-accumulator candidate
                        // OR a never-mutated local. Don't recurse to avoid
                        // cycles; let CollectTypedAccumulatorCandidates
                        // validate the OUTER chain.
                        if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvnAny
                            && rvnAny.Name != name && rvnAny.Name != iterName
                            && !HasAnyAssignmentTo(bodyRoot, rvnAny.Name))
                            return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st);
                        if (st != null
                            && IsLoopInvariantPureNumericExpr(bo.RightNode, bodyRoot, iterName, name, st))
                            return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st);
                        return false;
                    }
                    return IsCandidateAccumulatorOnly(va.ValueNode, bodyRoot, name, iterName, st);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    return IsCandidateAccumulatorOnly(bo.LeftNode, bodyRoot, name, iterName, st)
                        && IsCandidateAccumulatorOnly(bo.RightNode, bodyRoot, name, iterName, st);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return IsCandidateAccumulatorOnly(uo.Node, bodyRoot, name, iterName, st);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    if (!IsCandidateAccumulatorOnly(fc.NodeToCall, bodyRoot, name, iterName, st)) return false;
                    foreach (var arg in fc.ArgNodes)
                        if (!IsCandidateAccumulatorOnly(arg.Expr, bodyRoot, name, iterName, st)) return false;
                    return true;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    return IsCandidateAccumulatorOnly(tn.Condition, bodyRoot, name, iterName, st)
                        && IsCandidateAccumulatorOnly(tn.TrueExpression, bodyRoot, name, iterName, st)
                        && IsCandidateAccumulatorOnly(tn.FalseExpression, bodyRoot, name, iterName, st);
                }
                default:
                    return true;
            }
        }

        // Returns true iff any `VariableAssignment` whose LHS is `name`
        // in `node`'s subtree fails the redirectable self-additive
        // pattern. Used by the hybrid promotion gate: typed-accumulator
        // promotion is safe only when EVERY write to `name` lands in a
        // pattern the IR compiler can rewrite to typed AddII / SubII.
        // Non-redirectable READS (e.g. `print(name)`, `if name > 0`)
        // are allowed — they get a lazy `BoxI + StoreLocalS` publish
        // emitted right before the boxed access.
        private static bool HasNonRedirectableAccumulatorWrite(
            AstNode? node, AstNode bodyRoot, string name, string iterName, State st)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        if (HasNonRedirectableAccumulatorWrite(c, bodyRoot, name, iterName, st)) return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (HasNonRedirectableAccumulatorWrite(cs.Condition, bodyRoot, name, iterName, st)) return true;
                        if (HasNonRedirectableAccumulatorWrite(cs.Expr, bodyRoot, name, iterName, st)) return true;
                    }
                    if (ifn.ElseCase.HasValue
                        && HasNonRedirectableAccumulatorWrite(ifn.ElseCase.Value.Expr, bodyRoot, name, iterName, st))
                        return true;
                    return false;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    // A plain const-int64 write (`a = 0`) is now REDIRECTABLE:
                    // the assignment lowering writes it straight into the typed
                    // Int64 slot (see the VariableAssignment compile path), so
                    // it does NOT break the typed accumulator's source-of-truth
                    // invariant. Must stay in lock-step with that lowering.
                    bool redirectableConstWrite =
                        va.AssignmentToken.Type == Lexer.Tokens.TokenType.EQ
                        && TryGetLiteralLongFromConstExpr(va.ValueNode, out _);
                    if (va.Name == name
                        && !IsRedirectableSelfAdditive(va, name, iterName, bodyRoot, st)
                        && !redirectableConstWrite)
                        return true;
                    // Recurse into RHS for nested assignments.
                    return HasNonRedirectableAccumulatorWrite(va.ValueNode, bodyRoot, name, iterName, st);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    return HasNonRedirectableAccumulatorWrite(bo.LeftNode, bodyRoot, name, iterName, st)
                        || HasNonRedirectableAccumulatorWrite(bo.RightNode, bodyRoot, name, iterName, st);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return HasNonRedirectableAccumulatorWrite(uo.Node, bodyRoot, name, iterName, st);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    if (HasNonRedirectableAccumulatorWrite(fc.NodeToCall, bodyRoot, name, iterName, st)) return true;
                    foreach (var arg in fc.ArgNodes)
                        if (HasNonRedirectableAccumulatorWrite(arg.Expr, bodyRoot, name, iterName, st)) return true;
                    return false;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    return HasNonRedirectableAccumulatorWrite(tn.Condition, bodyRoot, name, iterName, st)
                        || HasNonRedirectableAccumulatorWrite(tn.TrueExpression, bodyRoot, name, iterName, st)
                        || HasNonRedirectableAccumulatorWrite(tn.FalseExpression, bodyRoot, name, iterName, st);
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    return HasNonRedirectableAccumulatorWrite(nc.Left, bodyRoot, name, iterName, st)
                        || HasNonRedirectableAccumulatorWrite(nc.Right, bodyRoot, name, iterName, st);
                }
                case AstNodeType.MemberAssignment:
                case AstNodeType.ListAssignment:
                    // These write to a field/index of an object, not to
                    // a binding named `name` directly. The target may
                    // EVALUATE `name` (read), but the binding itself is
                    // unchanged. Safe.
                    return false;
                // Leaves + unhandled — conservatively assume no write
                // (we'll catch real misses via the regression suite).
                default:
                    return false;
            }
        }

        // Collects every distinct int64 literal value used as RHS of a
        // typed-accumulator self-additive in `body`. The caller pre-loads
        // each unique value into a typed slot exactly once before the
        // loop, so per-iter dispatch is a single `AddII / SubII`.
        // Accepts both literal nodes (`1`) and constant-foldable
        // arithmetic (`10 - 7` → 3).
        private static void CollectAccumulatorLiteralRhsValues(
            AstNode? node, IReadOnlyCollection<string> accumulatorNames, HashSet<long> outValues)
        {
            if (node == null || accumulatorNames.Count == 0) return;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        CollectAccumulatorLiteralRhsValues(c, accumulatorNames, outValues);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                        CollectAccumulatorLiteralRhsValues(cs.Expr, accumulatorNames, outValues);
                    if (ifn.ElseCase.HasValue)
                        CollectAccumulatorLiteralRhsValues(ifn.ElseCase.Value.Expr, accumulatorNames, outValues);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return;
                    if (!accumulatorNames.Contains(va.Name)) return;
                    if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return;
                    var opT = bo.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return;
                    if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return;
                    if (lvn.Name != va.Name) return;
                    if (TryGetLiteralLongFromConstExpr(bo.RightNode, out long lit))
                        outValues.Add(lit);
                    return;
                }
                default:
                    return;
            }
        }

        // Pre-loop emitter for loop-invariant pure-expression RHS slots
        // of typed accumulators. Walks `node` looking for self-additive
        // assignments `acc = acc ± expr` where `acc` is in
        // `accumulatorNames` and `expr` passes
        // `IsLoopInvariantPureNumericExpr`. For each match, compile the
        // RHS expression into a fresh boxed slot, then UnboxI into a
        // typed Int64 slot and register the typed slot in
        // `st.TypedAccumulatorExprs[expr] = typedSlot`. The body's
        // `TryEmitSelfAdditiveSlot` then emits a pure AddII / SubII
        // reading the typed slot.
        //
        // De-duplicates by AstNode identity: the same RHS reference
        // appearing twice (impossible in practice since each
        // AstNode is unique) would reuse the typed slot.
        private static void CollectAccumulatorLoopInvariantExprs(
            AstNode? node, AstNode bodyRoot, string iterName,
            IReadOnlyCollection<string> accumulatorNames, State st, ref byte topSlot)
        {
            if (node == null || accumulatorNames.Count == 0) return;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        CollectAccumulatorLoopInvariantExprs(c, bodyRoot, iterName, accumulatorNames, st, ref topSlot);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                        CollectAccumulatorLoopInvariantExprs(cs.Expr, bodyRoot, iterName, accumulatorNames, st, ref topSlot);
                    if (ifn.ElseCase.HasValue)
                        CollectAccumulatorLoopInvariantExprs(ifn.ElseCase.Value.Expr, bodyRoot, iterName, accumulatorNames, st, ref topSlot);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return;
                    if (!accumulatorNames.Contains(va.Name)) return;
                    if (va.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return;
                    var opT = bo.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return;
                    if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return;
                    if (lvn.Name != va.Name) return;
                    // RHS already covered by typed-iter / literal paths?
                    if (bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvn
                        && rvn.Name == iterName) return;
                    if (TryGetLiteralLongFromConstExpr(bo.RightNode, out _)) return;
                    // Loop-invariant pure expression — compile once and
                    // pin its typed slot.
                    if (!IsLoopInvariantPureNumericExpr(bo.RightNode, bodyRoot, iterName, va.Name, st)) return;
                    if (st.TypedAccumulatorExprs.ContainsKey(bo.RightNode)) return;
                    byte boxedTmp = AllocTemp(ref topSlot);
                    CompileExpression(bo.RightNode, boxedTmp, st, ref topSlot);
                    byte typedSlot = AllocTemp(ref topSlot);
                    st.Code.Emit3(Opcode.UnboxI, typedSlot, boxedTmp, 0);
                    st.TypedAccumulatorExprs[bo.RightNode] = typedSlot;
                    return;
                }
                default:
                    return;
            }
        }

        // Returns true iff `t` is a token comparison op the IR compiler
        // can lower to a typed II compare opcode (EqII / NeII / LtII /
        // LeII / GtII / GeII).
        private static bool IsTypedComparableOp(Lexer.Tokens.TokenType t)
        {
            return t == Lexer.Tokens.TokenType.EE
                || t == Lexer.Tokens.TokenType.NE
                || t == Lexer.Tokens.TokenType.LT
                || t == Lexer.Tokens.TokenType.LTE
                || t == Lexer.Tokens.TokenType.GT
                || t == Lexer.Tokens.TokenType.GTE;
        }

        // Returns the typed-II compare opcode corresponding to a token
        // comparison op. Caller has already verified via `IsTypedComparableOp`.
        private static Opcode TypedComparisonOpcode(Lexer.Tokens.TokenType t, bool swapped)
        {
            // When operands are swapped (`lit ⋈ iter` instead of `iter ⋈ lit`),
            // the comparison direction inverts: `a < b` ↔ `b > a`, etc.
            if (!swapped)
            {
                return t switch
                {
                    Lexer.Tokens.TokenType.EE  => Opcode.EqII,
                    Lexer.Tokens.TokenType.NE  => Opcode.NeII,
                    Lexer.Tokens.TokenType.LT  => Opcode.LtII,
                    Lexer.Tokens.TokenType.LTE => Opcode.LeII,
                    Lexer.Tokens.TokenType.GT  => Opcode.GtII,
                    Lexer.Tokens.TokenType.GTE => Opcode.GeII,
                    _ => Opcode.EqII,
                };
            }
            return t switch
            {
                Lexer.Tokens.TokenType.EE  => Opcode.EqII,
                Lexer.Tokens.TokenType.NE  => Opcode.NeII,
                Lexer.Tokens.TokenType.LT  => Opcode.GtII,
                Lexer.Tokens.TokenType.LTE => Opcode.GeII,
                Lexer.Tokens.TokenType.GT  => Opcode.LtII,
                Lexer.Tokens.TokenType.GTE => Opcode.LeII,
                _ => Opcode.EqII,
            };
        }

        // M87: typed-Int64 expression family. Generalises the typed-iter
        // comparison redirect so that ARBITRARY arithmetic / bitwise /
        // comparison trees whose every leaf is either a typed slot
        // (ActiveTypedIters / TypedAccumulators / TypedLongBindings) or
        // a constant-foldable int64 literal compile to pure *II opcodes
        // — no boxed mirror touches, zero NumberValue per iter.
        //
        // Used by `bench_branchy.ra`-style bodies such as
        // `if (i % 2) == 0 { ... }` where the boxed `Mod` + `Eq`
        // dispatch was the per-iter allocation tax.
        private static bool IsTypedBinaryOp(Lexer.Tokens.TokenType t)
        {
            return t == Lexer.Tokens.TokenType.PLUS
                || t == Lexer.Tokens.TokenType.MINUS
                || t == Lexer.Tokens.TokenType.MUL
                || t == Lexer.Tokens.TokenType.DIV
                || t == Lexer.Tokens.TokenType.MODULO
                || t == Lexer.Tokens.TokenType.BITWISE_AND
                || t == Lexer.Tokens.TokenType.BITWISE_OR
                || t == Lexer.Tokens.TokenType.BITWISE_LEFT_SHIFT
                || t == Lexer.Tokens.TokenType.BITWISE_RIGHT_SHIFT
                // `<<<` (logical left) shares ShlII with `<<` — identical bit
                // pattern, no semantic divergence on the typed path.
                || t == Lexer.Tokens.TokenType.BITWISE_LOGICAL_LEFT_SHIFT
                // `>>>`, `<<<<`, `>>>>` are intentionally EXCLUDED from the
                // typed-Int64 promotion. Their semantics differ from `>>` /
                // `<<` for the same bit pattern (logical vs arithmetic right
                // shift; rotate vs shift) AND the boxed NumberValue path
                // surfaces specific diagnostics ("rotate undefined on number",
                // "logical right shift undefined on negative number"). Forcing
                // the typed promotion at the IR level would silently swap a
                // 64-bit rotation for those diagnostics on small literals like
                // `1 <<<< 4`. Users who want the int64 fast path cast to `long`
                // explicitly — the LongValue overload then routes to the
                // boxed-path-equivalent 64-bit operation.
                || t == Lexer.Tokens.TokenType.POW
                || IsTypedComparableOp(t);
        }

        private static Opcode MapTypedBinary(Lexer.Tokens.TokenType t)
        {
            return t switch
            {
                Lexer.Tokens.TokenType.PLUS                  => Opcode.AddII,
                Lexer.Tokens.TokenType.MINUS                 => Opcode.SubII,
                Lexer.Tokens.TokenType.MUL                   => Opcode.MulII,
                Lexer.Tokens.TokenType.DIV                   => Opcode.DivII,
                Lexer.Tokens.TokenType.MODULO                => Opcode.ModII,
                Lexer.Tokens.TokenType.BITWISE_AND           => Opcode.BAndII,
                Lexer.Tokens.TokenType.BITWISE_OR            => Opcode.BOrII,
                Lexer.Tokens.TokenType.BITWISE_LEFT_SHIFT    => Opcode.ShlII,
                Lexer.Tokens.TokenType.BITWISE_RIGHT_SHIFT   => Opcode.ShrII,
                // `<<<` shares the ShlII opcode (same bit pattern as `<<`).
                // `>>>`, `<<<<`, `>>>>` deliberately have no typed-II mapping
                // — see IsTypedBinaryOp for the rationale.
                Lexer.Tokens.TokenType.BITWISE_LOGICAL_LEFT_SHIFT  => Opcode.ShlII,
                Lexer.Tokens.TokenType.POW                   => Opcode.PowII,
                Lexer.Tokens.TokenType.EE                    => Opcode.EqII,
                Lexer.Tokens.TokenType.NE                    => Opcode.NeII,
                Lexer.Tokens.TokenType.LT                    => Opcode.LtII,
                Lexer.Tokens.TokenType.LTE                   => Opcode.LeII,
                Lexer.Tokens.TokenType.GT                    => Opcode.GtII,
                Lexer.Tokens.TokenType.GTE                   => Opcode.GeII,
                _ => throw new IrCompileException($"typed binary op {t} not mappable"),
            };
        }

        // Pure recursive predicate. Returns true when `node` evaluates to
        // a typed `Int64` (or `Bool` for the comparison variants) at
        // runtime without any boxed dispatch — every leaf resolves to a
        // typed slot, every interior op is in `IsTypedBinaryOp`, and
        // every unary is the typed-supported `MINUS`. The result tag
        // matters less than the absence of allocation: the typed family
        // either keeps the slot Int64 / Bool, or deopts in place
        // preserving error PC.
        //
        // Conservative: rejects POW / DIV / MOD trees if either operand
        // is non-literal AND non-typed (would need wider analysis to
        // prove overflow safety). The conservative rejection just routes
        // to the existing boxed path with no correctness impact.
        private static bool IsTypedInt64Expression(AstNode? node, State st)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                    return TryGetLiteralLongFromConstExpr(node, out _);
                case AstNodeType.VariableAccess:
                {
                    var va = (Parser.Nodes.Variables.VariableAccessNode)node;
                    if (string.IsNullOrEmpty(va.Name)) return false;
                    return st.ActiveTypedIters.ContainsKey(va.Name)
                        || st.TypedLongBindings.ContainsKey(va.Name)
                        || st.TypedAccumulators.ContainsKey(va.Name);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    // Only typed-MINUS is supported (NegI). BITWISE_NOT
                    // currently has no typed unary opcode; reject.
                    if (uo.OpTok.Type != Lexer.Tokens.TokenType.MINUS) return false;
                    return IsTypedInt64Expression(uo.Node, st);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    if (!IsTypedBinaryOp(bo.OpTok.Type)) return false;
                    return IsTypedInt64Expression(bo.LeftNode, st)
                        && IsTypedInt64Expression(bo.RightNode, st);
                }
                default:
                    return false;
            }
        }

        // Emits a typed-Int64 sub-expression. Returns the slot index
        // where the value lives — either an existing typed slot (no
        // allocation, no opcode emitted) or a freshly-allocated temp
        // holding the computed result. Caller has already validated
        // `IsTypedInt64Expression(node, st) == true`.
        //
        // Designed to be called from the typed redirect inside
        // `CompileExpression`'s `BinaryOperation` case so it can produce
        // operand slots without going through the boxed VA / Number
        // codepaths.
        private static byte EmitTypedInt64Operand(AstNode node, State st, ref byte topSlot)
        {
            switch (node.NodeType)
            {
                case AstNodeType.Number:
                {
                    TryGetLiteralLongFromConstExpr(node, out long v);
                    if (st.TypedAccumulatorLiterals.TryGetValue(v, out byte preloaded))
                        return preloaded;
                    byte s = AllocTemp(ref topSlot);
                    EmitLiteralLongLoad(v, s, st, ref topSlot);
                    return s;
                }
                case AstNodeType.VariableAccess:
                {
                    var va = (Parser.Nodes.Variables.VariableAccessNode)node;
                    if (st.ActiveTypedIters.TryGetValue(va.Name, out byte itSlot)) return itSlot;
                    if (st.TypedLongBindings.TryGetValue(va.Name, out var tlb)) return tlb.LongSlot;
                    if (st.TypedAccumulators.TryGetValue(va.Name, out var ta)) return ta.LongSlot;
                    throw new IrCompileException("typed Int64 operand: VA binding not in typed registry");
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    byte src = EmitTypedInt64Operand(uo.Node, st, ref topSlot);
                    byte dst = AllocTemp(ref topSlot);
                    st.Code.Emit3(Opcode.NegI, dst, src, 0);
                    return dst;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    byte lhs = EmitTypedInt64Operand(bo.LeftNode, st, ref topSlot);
                    byte rhs = EmitTypedInt64Operand(bo.RightNode, st, ref topSlot);
                    byte dst = AllocTemp(ref topSlot);
                    st.Code.Emit3(MapTypedBinary(bo.OpTok.Type), dst, lhs, rhs);
                    return dst;
                }
                default:
                    throw new IrCompileException($"typed Int64 operand: unhandled node {node.NodeType}");
            }
        }

        // Counts iter `VariableAccess` nodes that appear as one operand
        // of a comparison BinaryOp whose OTHER operand is either:
        //   * a constant-foldable int64 literal, OR
        //   * a `VariableAccess` to a binding that is NEVER mutated in
        //     `bodyRoot` (the enclosing for-loop body, passed top-down
        //     so the mutation check can scan the right scope).
        // Each such site lowers to a typed II compare reading the iter
        // long slot directly and either a pre-loaded literal slot or a
        // pre-loaded typed-long binding slot. No boxed mirror needed.
        private static int CountTypedIterComparisonAccess(AstNode? node, string iterName)
            => CountTypedIterComparisonAccess(node, iterName, node);

        private static int CountTypedIterComparisonAccess(
            AstNode? node, string iterName, AstNode? bodyRoot)
        {
            if (node == null || string.IsNullOrEmpty(iterName)) return 0;
            int sum = 0;
            if (node is Parser.Nodes.Operations.BinaryOperationNode bo
                && IsTypedComparableOp(bo.OpTok.Type))
            {
                bool leftIter = bo.LeftNode is Parser.Nodes.Variables.VariableAccessNode lvi
                                && lvi.Name == iterName;
                bool rightIter = bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvi
                                 && rvi.Name == iterName;
                if (leftIter && !rightIter && TryGetLiteralLongFromConstExpr(bo.RightNode, out _))
                    return 1 + CountTypedIterComparisonAccess(bo.RightNode, iterName, bodyRoot);
                if (rightIter && !leftIter && TryGetLiteralLongFromConstExpr(bo.LeftNode, out _))
                    return 1 + CountTypedIterComparisonAccess(bo.LeftNode, iterName, bodyRoot);
                // Non-integer literal RHS that the static rewrite can
                // collapse to a typed int compare. ConstFalse / ConstTrue
                // (==/!= against non-int) are intentionally excluded —
                // they aren't lowered to typed at the emission layer.
                if (leftIter && !rightIter)
                {
                    var r = TryReduceNonIntIterCompareLit(bo.OpTok.Type, bo.RightNode,
                        iterOnLeft: true, out _, out _);
                    if (r == NonIntCompareResult.RewriteOk)
                        return 1 + CountTypedIterComparisonAccess(bo.RightNode, iterName, bodyRoot);
                }
                if (rightIter && !leftIter)
                {
                    var r = TryReduceNonIntIterCompareLit(bo.OpTok.Type, bo.LeftNode,
                        iterOnLeft: false, out _, out _);
                    if (r == NonIntCompareResult.RewriteOk)
                        return 1 + CountTypedIterComparisonAccess(bo.LeftNode, iterName, bodyRoot);
                }
                // Binding-operand path: `iter ⋈ binding` where binding
                // is never mutated in the for-loop body.
                if (leftIter && !rightIter
                    && bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rBnd
                    && !string.IsNullOrEmpty(rBnd.Name)
                    && rBnd.Name != iterName
                    && !HasAnyAssignmentTo(bodyRoot, rBnd.Name))
                    return 1 + CountTypedIterComparisonAccess(bo.RightNode, iterName, bodyRoot);
                if (rightIter && !leftIter
                    && bo.LeftNode is Parser.Nodes.Variables.VariableAccessNode lBnd
                    && !string.IsNullOrEmpty(lBnd.Name)
                    && lBnd.Name != iterName
                    && !HasAnyAssignmentTo(bodyRoot, lBnd.Name))
                    return 1 + CountTypedIterComparisonAccess(bo.LeftNode, iterName, bodyRoot);
                // Comparisons not matching the pattern fall through to
                // generic recursion below.
            }
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) sum += CountTypedIterComparisonAccess(c, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        sum += CountTypedIterComparisonAccess(cs.Condition, iterName, bodyRoot);
                        sum += CountTypedIterComparisonAccess(cs.Expr, iterName, bodyRoot);
                    }
                    if (ifn.ElseCase.HasValue)
                        sum += CountTypedIterComparisonAccess(ifn.ElseCase.Value.Expr, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    sum += CountTypedIterComparisonAccess(wn.ConditionNode, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(wn.BodyNode, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    sum += CountTypedIterComparisonAccess(dw.ConditionNode, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(dw.BodyNode, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    return CountTypedIterComparisonAccess(va.ValueNode, iterName, bodyRoot);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo2 = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    sum += CountTypedIterComparisonAccess(bo2.LeftNode, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(bo2.RightNode, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return CountTypedIterComparisonAccess(uo.Node, iterName, bodyRoot);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    sum += CountTypedIterComparisonAccess(fc.NodeToCall, iterName, bodyRoot);
                    foreach (var arg in fc.ArgNodes)
                        sum += CountTypedIterComparisonAccess(arg.Expr, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    sum += CountTypedIterComparisonAccess(tn.Condition, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(tn.TrueExpression, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(tn.FalseExpression, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    sum += CountTypedIterComparisonAccess(nc.Left, iterName, bodyRoot);
                    sum += CountTypedIterComparisonAccess(nc.Right, iterName, bodyRoot);
                    return sum;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    if (rn.NodeToReturn != null)
                        sum += CountTypedIterComparisonAccess(rn.NodeToReturn, iterName, bodyRoot);
                    return sum;
                }
                default:
                    return 0;
            }
        }

        // Returns true iff `body` contains any `VariableAssignment` or
        // compound assignment that writes the binding `name`. Used to
        // gate typed-long-binding promotion: only never-mutated bindings
        // can stay in a typed slot for the loop's lifetime.
        private static bool HasAnyAssignmentTo(AstNode? node, string name)
        {
            if (node == null) return false;
            if (node is Parser.Nodes.Variables.VariableAssignmentNode va
                && va.Name == name)
                return true;
            if (node is Parser.Nodes.Variables.VariableDeclarationNode vd)
            {
                foreach (var d in vd.Declarations)
                {
                    if (d.Item1.Value?.ToString() == name) return true;
                }
            }
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) if (HasAnyAssignmentTo(c, name)) return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (HasAnyAssignmentTo(cs.Condition, name)) return true;
                        if (HasAnyAssignmentTo(cs.Expr, name)) return true;
                    }
                    if (ifn.ElseCase.HasValue
                        && HasAnyAssignmentTo(ifn.ElseCase.Value.Expr, name)) return true;
                    return false;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    return HasAnyAssignmentTo(wn.ConditionNode, name)
                        || HasAnyAssignmentTo(wn.BodyNode, name);
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    return HasAnyAssignmentTo(dw.ConditionNode, name)
                        || HasAnyAssignmentTo(dw.BodyNode, name);
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    if (fn.VarNameTok.Value?.ToString() == name) return true;
                    if (HasAnyAssignmentTo(fn.StartValueNode, name)) return true;
                    if (HasAnyAssignmentTo(fn.EndValueNode, name)) return true;
                    if (fn.StepValueNode != null && HasAnyAssignmentTo(fn.StepValueNode, name)) return true;
                    return HasAnyAssignmentTo(fn.BodyNode, name);
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    if (fe.VarNameToken.Value?.ToString() == name) return true;
                    return HasAnyAssignmentTo(fe.CollectionNode, name)
                        || HasAnyAssignmentTo(fe.BodyNode, name);
                }
                case AstNodeType.VariableAssignment:
                {
                    var v = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    return HasAnyAssignmentTo(v.ValueNode, name);
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    return HasAnyAssignmentTo(bo.LeftNode, name)
                        || HasAnyAssignmentTo(bo.RightNode, name);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return HasAnyAssignmentTo(uo.Node, name);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    if (HasAnyAssignmentTo(fc.NodeToCall, name)) return true;
                    foreach (var arg in fc.ArgNodes)
                        if (HasAnyAssignmentTo(arg.Expr, name)) return true;
                    return false;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    return HasAnyAssignmentTo(tn.Condition, name)
                        || HasAnyAssignmentTo(tn.TrueExpression, name)
                        || HasAnyAssignmentTo(tn.FalseExpression, name);
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    return HasAnyAssignmentTo(nc.Left, name)
                        || HasAnyAssignmentTo(nc.Right, name);
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    return rn.NodeToReturn != null && HasAnyAssignmentTo(rn.NodeToReturn, name);
                }
                case AstNodeType.Throw:
                {
                    var tn = (Parser.Nodes.Statements.ThrowNode)node;
                    return tn.Expression != null && HasAnyAssignmentTo(tn.Expression, name);
                }
                case AstNodeType.MemberAssignment:
                {
                    var ma = (Parser.Nodes.Structs.MemberAssignmentNode)node;
                    return HasAnyAssignmentTo(ma.TargetNode, name)
                        || HasAnyAssignmentTo(ma.ValueNode, name);
                }
                case AstNodeType.ListAssignment:
                {
                    var la = (Parser.Nodes.Variables.ListAssignmentNode)node;
                    return HasAnyAssignmentTo(la.Target, name)
                        || HasAnyAssignmentTo(la.Value, name);
                }
                default:
                    return false; // simple expressions / literals — no nested assignments
            }
        }

        // Walks `body` for typed-iter comparison sites and collects
        // every distinct VARIABLE name used as the non-iter operand.
        // Result feeds the typed-long-binding promotion: each name
        // gets validated (never mutated, slot-eligible) and pre-loaded
        // into a typed Int64 slot for the loop's lifetime.
        private static void CollectIterComparisonBindingNames(
            AstNode? node, string iterName, HashSet<string> outNames)
        {
            if (node == null || string.IsNullOrEmpty(iterName)) return;
            if (node is Parser.Nodes.Operations.BinaryOperationNode bo
                && IsTypedComparableOp(bo.OpTok.Type))
            {
                bool leftIter = bo.LeftNode is Parser.Nodes.Variables.VariableAccessNode lvi
                                && lvi.Name == iterName;
                bool rightIter = bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvi
                                 && rvi.Name == iterName;
                if (leftIter && !rightIter
                    && bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rOpd
                    && !string.IsNullOrEmpty(rOpd.Name)
                    && rOpd.Name != iterName)
                {
                    outNames.Add(rOpd.Name);
                    return;
                }
                if (rightIter && !leftIter
                    && bo.LeftNode is Parser.Nodes.Variables.VariableAccessNode lOpd
                    && !string.IsNullOrEmpty(lOpd.Name)
                    && lOpd.Name != iterName)
                {
                    outNames.Add(lOpd.Name);
                    return;
                }
            }
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectIterComparisonBindingNames(c, iterName, outNames);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectIterComparisonBindingNames(cs.Condition, iterName, outNames);
                        CollectIterComparisonBindingNames(cs.Expr, iterName, outNames);
                    }
                    if (ifn.ElseCase.HasValue)
                        CollectIterComparisonBindingNames(ifn.ElseCase.Value.Expr, iterName, outNames);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectIterComparisonBindingNames(wn.ConditionNode, iterName, outNames);
                    CollectIterComparisonBindingNames(wn.BodyNode, iterName, outNames);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectIterComparisonBindingNames(dw.ConditionNode, iterName, outNames);
                    CollectIterComparisonBindingNames(dw.BodyNode, iterName, outNames);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    CollectIterComparisonBindingNames(va.ValueNode, iterName, outNames);
                    return;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo2 = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    CollectIterComparisonBindingNames(bo2.LeftNode, iterName, outNames);
                    CollectIterComparisonBindingNames(bo2.RightNode, iterName, outNames);
                    return;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    CollectIterComparisonBindingNames(uo.Node, iterName, outNames);
                    return;
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    CollectIterComparisonBindingNames(fc.NodeToCall, iterName, outNames);
                    foreach (var arg in fc.ArgNodes)
                        CollectIterComparisonBindingNames(arg.Expr, iterName, outNames);
                    return;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    CollectIterComparisonBindingNames(tn.Condition, iterName, outNames);
                    CollectIterComparisonBindingNames(tn.TrueExpression, iterName, outNames);
                    CollectIterComparisonBindingNames(tn.FalseExpression, iterName, outNames);
                    return;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    CollectIterComparisonBindingNames(nc.Left, iterName, outNames);
                    CollectIterComparisonBindingNames(nc.Right, iterName, outNames);
                    return;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    if (rn.NodeToReturn != null)
                        CollectIterComparisonBindingNames(rn.NodeToReturn, iterName, outNames);
                    return;
                }
                default:
                    return;
            }
        }

        // Walks `body` for typed-iter comparison sites and collects every
        // distinct int64 literal value used. Caller pre-loads each into
        // a typed slot before the loop so per-comparison emission needs
        // only a single typed II opcode.
        private static void CollectIterComparisonLiterals(
            AstNode? node, string iterName, HashSet<long> outValues)
        {
            if (node == null || string.IsNullOrEmpty(iterName)) return;
            if (node is Parser.Nodes.Operations.BinaryOperationNode bo
                && IsTypedComparableOp(bo.OpTok.Type))
            {
                bool leftIter = bo.LeftNode is Parser.Nodes.Variables.VariableAccessNode lvi
                                && lvi.Name == iterName;
                bool rightIter = bo.RightNode is Parser.Nodes.Variables.VariableAccessNode rvi
                                 && rvi.Name == iterName;
                if (leftIter && !rightIter && TryGetLiteralLongFromConstExpr(bo.RightNode, out long litR))
                {
                    outValues.Add(litR);
                    return;
                }
                if (rightIter && !leftIter && TryGetLiteralLongFromConstExpr(bo.LeftNode, out long litL))
                {
                    outValues.Add(litL);
                    return;
                }
                // Truly non-integer literal: pre-load the rewritten
                // floor/ceil so the typed compare can resolve at
                // emission time.
                if (leftIter && !rightIter)
                {
                    var r = TryReduceNonIntIterCompareLit(bo.OpTok.Type, bo.RightNode,
                        iterOnLeft: true, out _, out long nonIntLitR);
                    if (r == NonIntCompareResult.RewriteOk)
                    {
                        outValues.Add(nonIntLitR);
                        return;
                    }
                }
                if (rightIter && !leftIter)
                {
                    var r = TryReduceNonIntIterCompareLit(bo.OpTok.Type, bo.LeftNode,
                        iterOnLeft: false, out _, out long nonIntLitL);
                    if (r == NonIntCompareResult.RewriteOk)
                    {
                        outValues.Add(nonIntLitL);
                        return;
                    }
                }
            }
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes) CollectIterComparisonLiterals(c, iterName, outValues);
                    return;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        CollectIterComparisonLiterals(cs.Condition, iterName, outValues);
                        CollectIterComparisonLiterals(cs.Expr, iterName, outValues);
                    }
                    if (ifn.ElseCase.HasValue)
                        CollectIterComparisonLiterals(ifn.ElseCase.Value.Expr, iterName, outValues);
                    return;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    CollectIterComparisonLiterals(wn.ConditionNode, iterName, outValues);
                    CollectIterComparisonLiterals(wn.BodyNode, iterName, outValues);
                    return;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    CollectIterComparisonLiterals(dw.ConditionNode, iterName, outValues);
                    CollectIterComparisonLiterals(dw.BodyNode, iterName, outValues);
                    return;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    CollectIterComparisonLiterals(va.ValueNode, iterName, outValues);
                    return;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo2 = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    CollectIterComparisonLiterals(bo2.LeftNode, iterName, outValues);
                    CollectIterComparisonLiterals(bo2.RightNode, iterName, outValues);
                    return;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    CollectIterComparisonLiterals(uo.Node, iterName, outValues);
                    return;
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    CollectIterComparisonLiterals(fc.NodeToCall, iterName, outValues);
                    foreach (var arg in fc.ArgNodes)
                        CollectIterComparisonLiterals(arg.Expr, iterName, outValues);
                    return;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    CollectIterComparisonLiterals(tn.Condition, iterName, outValues);
                    CollectIterComparisonLiterals(tn.TrueExpression, iterName, outValues);
                    CollectIterComparisonLiterals(tn.FalseExpression, iterName, outValues);
                    return;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    CollectIterComparisonLiterals(nc.Left, iterName, outValues);
                    CollectIterComparisonLiterals(nc.Right, iterName, outValues);
                    return;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    if (rn.NodeToReturn != null)
                        CollectIterComparisonLiterals(rn.NodeToReturn, iterName, outValues);
                    return;
                }
                default:
                    return;
            }
        }

        // Counts iter accesses that the IR compiler will redirect to
        // `AddIntoSlotI` / `SubIntoSlotI` via `TryEmitSelfAdditiveSlot`.
        // The pattern is `selfSlot = selfSlot ± iterName` where `selfSlot`
        // is slot-eligible in the current frame. Walks the body looking
        // for `VariableAssignmentNode` matching that shape.
        private static int CountRedirectableIterAccess(AstNode? node, string name, State st)
        {
            if (node == null) return 0;
            switch (node.NodeType)
            {
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    int sum = 0;
                    foreach (var c in sc.Nodes) sum += CountRedirectableIterAccess(c, name, st);
                    return sum;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    int sum = 0;
                    foreach (var cs in ifn.Cases)
                        sum += CountRedirectableIterAccess(cs.Expr, name, st);
                    if (ifn.ElseCase.HasValue)
                        sum += CountRedirectableIterAccess(ifn.ElseCase.Value.Expr, name, st);
                    return sum;
                }
                case AstNodeType.VariableAssignment:
                {
                    var vasn = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    if (vasn.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ) return 0;
                    if (!vasn.Binding.IsResolved) return 0;
                    if (!IsSlotEligible(vasn.Binding, vasn.BindingKind, st)) return 0;
                    if (vasn.ValueNode is not Parser.Nodes.Operations.BinaryOperationNode bo) return 0;
                    var opT = bo.OpTok.Type;
                    if (opT != Lexer.Tokens.TokenType.PLUS && opT != Lexer.Tokens.TokenType.MINUS) return 0;
                    if (bo.LeftNode is not Parser.Nodes.Variables.VariableAccessNode lvn) return 0;
                    if (!lvn.Binding.IsResolved || lvn.Binding != vasn.Binding) return 0;
                    if (lvn.BindingKind != vasn.BindingKind) return 0;
                    if (bo.RightNode is not Parser.Nodes.Variables.VariableAccessNode rvn) return 0;
                    if (rvn.Name != name) return 0;
                    // String self-append `s = s + i`. When `s` will be PROMOTED,
                    // it lowers to StrAccAppendI which reads the iter's typed
                    // long slot directly — redirectable, no boxed publish. When
                    // `s` is NOT promoted (e.g. an extra in-loop read disqualifies
                    // it) it falls back to the boxed AddIntoSlot reading the iter
                    // MIRROR, so the publish must stay — NOT redirectable.
                    if (st.StringInitBindings.Contains(lvn.Name))
                        return st.PromotableStrAccNames.Contains(lvn.Name) ? 1 : 0;
                    return 1;
                }
                // Other statements may contain non-redirectable iter accesses
                // — those are counted by `CountVariableAccess` and not here.
                default:
                    return 0;
            }
        }

        // Counts the number of `VariableAccessNode` matches for `name` in
        // `node` (recursive). Mirrors `BodyReadsBinding`'s recursion but
        // returns -1 on any conservative-skip subtree (function defs,
        // class defs, asm, etc.) so the caller can refuse the optimization
        // when an unknown construct may reference the iter.
        private static int CountVariableAccess(AstNode? node, string name)
        {
            if (node == null || string.IsNullOrEmpty(name)) return 0;
            switch (node.NodeType)
            {
                case AstNodeType.VariableAccess:
                {
                    var va = (Parser.Nodes.Variables.VariableAccessNode)node;
                    return va.Name == name ? 1 : 0;
                }
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                case AstNodeType.Null:
                case AstNodeType.String:
                case AstNodeType.Pass:
                case AstNodeType.Break:
                case AstNodeType.Continue:
                case AstNodeType.Retry:
                case AstNodeType.RegexLiteral:
                case AstNodeType.Nameof:
                case AstNodeType.Self:
                case AstNodeType.Super:
                case AstNodeType.VariableDelete:
                    return 0;
                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    int sum = 0;
                    foreach (var c in sc.Nodes)
                    {
                        int sub = CountVariableAccess(c, name);
                        if (sub < 0) return -1;
                        sum += sub;
                    }
                    return sum;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    int l = CountVariableAccess(bo.LeftNode, name); if (l < 0) return -1;
                    int r = CountVariableAccess(bo.RightNode, name); if (r < 0) return -1;
                    return l + r;
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return CountVariableAccess(uo.Node, name);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    int sum = CountVariableAccess(fc.NodeToCall, name);
                    if (sum < 0) return -1;
                    foreach (var arg in fc.ArgNodes)
                    {
                        int sub = CountVariableAccess(arg.Expr, name);
                        if (sub < 0) return -1;
                        sum += sub;
                    }
                    return sum;
                }
                case AstNodeType.VariableAssignment:
                {
                    var vasn = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    bool isCompound = vasn.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ;
                    int self = (isCompound && vasn.Name == name) ? 1 : 0;
                    int sub = CountVariableAccess(vasn.ValueNode, name);
                    if (sub < 0) return -1;
                    return self + sub;
                }
                case AstNodeType.VariableDeclaration:
                {
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    int sum = 0;
                    foreach (var d in vd.Declarations)
                    {
                        if (d.Item2 != null)
                        {
                            int sub = CountVariableAccess(d.Item2, name);
                            if (sub < 0) return -1;
                            sum += sub;
                        }
                    }
                    return sum;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    int sum = 0;
                    foreach (var cs in ifn.Cases)
                    {
                        int sc1 = CountVariableAccess(cs.Condition, name); if (sc1 < 0) return -1;
                        int sb = CountVariableAccess(cs.Expr, name); if (sb < 0) return -1;
                        sum += sc1 + sb;
                    }
                    if (ifn.ElseCase.HasValue)
                    {
                        int sb = CountVariableAccess(ifn.ElseCase.Value.Expr, name);
                        if (sb < 0) return -1;
                        sum += sb;
                    }
                    return sum;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    int sc1 = CountVariableAccess(wn.ConditionNode, name); if (sc1 < 0) return -1;
                    int sb = CountVariableAccess(wn.BodyNode, name); if (sb < 0) return -1;
                    return sc1 + sb;
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    int sc1 = CountVariableAccess(dw.ConditionNode, name); if (sc1 < 0) return -1;
                    int sb = CountVariableAccess(dw.BodyNode, name); if (sb < 0) return -1;
                    return sc1 + sb;
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    int s1 = CountVariableAccess(fn.StartValueNode, name); if (s1 < 0) return -1;
                    int s2 = CountVariableAccess(fn.EndValueNode, name); if (s2 < 0) return -1;
                    int s3 = 0;
                    if (fn.StepValueNode != null)
                    {
                        s3 = CountVariableAccess(fn.StepValueNode, name); if (s3 < 0) return -1;
                    }
                    string? inner = fn.VarNameTok.Value?.ToString();
                    int sb = (inner == name) ? 0 : CountVariableAccess(fn.BodyNode, name);
                    if (sb < 0) return -1;
                    return s1 + s2 + s3 + sb;
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    int s1 = CountVariableAccess(fe.CollectionNode, name); if (s1 < 0) return -1;
                    string? inner = fe.VarNameToken.Value?.ToString();
                    int sb = (inner == name) ? 0 : CountVariableAccess(fe.BodyNode, name);
                    if (sb < 0) return -1;
                    return s1 + sb;
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    return rn.NodeToReturn != null ? CountVariableAccess(rn.NodeToReturn, name) : 0;
                }
                case AstNodeType.Throw:
                {
                    var tn = (Parser.Nodes.Statements.ThrowNode)node;
                    return tn.Expression != null ? CountVariableAccess(tn.Expression, name) : 0;
                }
                case AstNodeType.MemberAccess:
                {
                    var ma = (Parser.Nodes.Structs.MemberAccessNode)node;
                    return CountVariableAccess(ma.TargetNode, name);
                }
                case AstNodeType.MemberAssignment:
                {
                    var ma = (Parser.Nodes.Structs.MemberAssignmentNode)node;
                    int l = CountVariableAccess(ma.TargetNode, name); if (l < 0) return -1;
                    int r = CountVariableAccess(ma.ValueNode, name); if (r < 0) return -1;
                    return l + r;
                }
                case AstNodeType.ListAccess:
                {
                    var la = (Parser.Nodes.Variables.ListAccessNode)node;
                    int l = CountVariableAccess(la.Target, name); if (l < 0) return -1;
                    int r = CountVariableAccess(la.Index, name); if (r < 0) return -1;
                    return l + r;
                }
                case AstNodeType.ListAssignment:
                {
                    var la = (Parser.Nodes.Variables.ListAssignmentNode)node;
                    int l = CountVariableAccess(la.Target, name); if (l < 0) return -1;
                    int r = CountVariableAccess(la.Value, name); if (r < 0) return -1;
                    return l + r;
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    int a = CountVariableAccess(tn.Condition, name); if (a < 0) return -1;
                    int b = CountVariableAccess(tn.TrueExpression, name); if (b < 0) return -1;
                    int c2 = CountVariableAccess(tn.FalseExpression, name); if (c2 < 0) return -1;
                    return a + b + c2;
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    int l = CountVariableAccess(nc.Left, name); if (l < 0) return -1;
                    int r = CountVariableAccess(nc.Right, name); if (r < 0) return -1;
                    return l + r;
                }
                case AstNodeType.Cast:
                {
                    var cn = (Parser.Nodes.Operations.CastNode)node;
                    return CountVariableAccess(cn.Expression, name);
                }
                case AstNodeType.IsType:
                {
                    var ist = (Parser.Nodes.Operations.IsTypeNode)node;
                    return CountVariableAccess(ist.Expression, name);
                }
                case AstNodeType.Range:
                {
                    var rn = (Parser.Nodes.Operations.RangeNode)node;
                    int a = CountVariableAccess(rn.Start, name); if (a < 0) return -1;
                    int b = CountVariableAccess(rn.End, name); if (b < 0) return -1;
                    int c2 = (rn.Step != null) ? CountVariableAccess(rn.Step, name) : 0;
                    if (c2 < 0) return -1;
                    return a + b + c2;
                }
                case AstNodeType.List:
                {
                    var ln = (Parser.Nodes.Primitives.ListNode)node;
                    int sum = 0;
                    foreach (var e in ln.ElementNodes)
                    {
                        int sub = CountVariableAccess(e, name); if (sub < 0) return -1;
                        sum += sub;
                    }
                    return sum;
                }
                case AstNodeType.Set:
                {
                    var sn = (Parser.Nodes.Primitives.SetNode)node;
                    int sum = 0;
                    foreach (var e in sn.ElementNodes)
                    {
                        int sub = CountVariableAccess(e, name); if (sub < 0) return -1;
                        sum += sub;
                    }
                    return sum;
                }
                case AstNodeType.Tuple:
                {
                    var tn = (Parser.Nodes.Primitives.TupleNode)node;
                    int sum = 0;
                    foreach (var e in tn.ElementNodes)
                    {
                        int sub = CountVariableAccess(e, name); if (sub < 0) return -1;
                        sum += sub;
                    }
                    return sum;
                }
                case AstNodeType.Typeof:
                {
                    var tn = (Parser.Nodes.Special.TypeofNode)node;
                    return CountVariableAccess(tn.Node, name);
                }
                case AstNodeType.Dereference:
                {
                    var dn = (Parser.Nodes.Operations.DereferenceNode)node;
                    return CountVariableAccess(dn.Target, name);
                }
                case AstNodeType.Spread:
                {
                    var sn = (Parser.Nodes.Operations.SpreadNode)node;
                    return CountVariableAccess(sn.Expression, name);
                }
                case AstNodeType.EnumAccess:
                {
                    var ea = (Parser.Nodes.Enums.EnumAccessNode)node;
                    return CountVariableAccess(ea.EnumNode, name);
                }
                default:
                    // Unknown construct — refuse the optimization to be safe.
                    return -1;
            }
        }

        // Default policy is CONSERVATIVE TRUE on unknown node shapes —
        // a false positive only loses the optimization, while a false
        // negative would corrupt observable semantics. Coverage is
        // tuned for the most common body shapes (Pass, print(...),
        // arithmetic, simple assignments, nested ifs/while/for, member
        // access). Anything not enumerated falls through to TRUE.
        private static bool BodyReadsBinding(AstNode? node, string name)
        {
            if (node == null || string.IsNullOrEmpty(name)) return false;
            switch (node.NodeType)
            {
                case AstNodeType.VariableAccess:
                {
                    var va = (Parser.Nodes.Variables.VariableAccessNode)node;
                    return va.Name == name;
                }
                // Leaves — no descendants that could reference `name`.
                case AstNodeType.Number:
                case AstNodeType.Boolean:
                case AstNodeType.Null:
                case AstNodeType.String:
                case AstNodeType.Pass:
                case AstNodeType.Break:
                case AstNodeType.Continue:
                case AstNodeType.Retry:
                case AstNodeType.RegexLiteral:
                case AstNodeType.Nameof:
                case AstNodeType.Self:
                case AstNodeType.Super:
                case AstNodeType.VariableDelete: // delete by name only, no expr
                    return false;

                case AstNodeType.Scope:
                {
                    var sc = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var c in sc.Nodes)
                        if (BodyReadsBinding(c, name)) return true;
                    return false;
                }
                case AstNodeType.BinaryOperation:
                {
                    var bo = (Parser.Nodes.Operations.BinaryOperationNode)node;
                    return BodyReadsBinding(bo.LeftNode, name)
                        || BodyReadsBinding(bo.RightNode, name);
                }
                case AstNodeType.UnaryOperation:
                {
                    var uo = (Parser.Nodes.Operations.UnaryOperationNode)node;
                    return BodyReadsBinding(uo.Node, name);
                }
                case AstNodeType.FunctionCall:
                {
                    var fc = (Parser.Nodes.Functions.FunctionCallNode)node;
                    if (BodyReadsBinding(fc.NodeToCall, name)) return true;
                    foreach (var arg in fc.ArgNodes)
                        if (BodyReadsBinding(arg.Expr, name)) return true;
                    return false;
                }
                case AstNodeType.VariableAssignment:
                {
                    var va = (Parser.Nodes.Variables.VariableAssignmentNode)node;
                    // Compound assignment (`i += x`) reads the lhs binding.
                    // Detect via AssignmentToken != EQ.
                    bool isCompound = va.AssignmentToken.Type != Lexer.Tokens.TokenType.EQ;
                    if (isCompound && va.Name == name) return true;
                    return BodyReadsBinding(va.ValueNode, name);
                }
                case AstNodeType.VariableDeclaration:
                {
                    var vd = (Parser.Nodes.Variables.VariableDeclarationNode)node;
                    foreach (var d in vd.Declarations)
                        if (d.Item2 != null && BodyReadsBinding(d.Item2, name))
                            return true;
                    return false;
                }
                case AstNodeType.If:
                {
                    var ifn = (Parser.Nodes.Statements.IfNode)node;
                    foreach (var cs in ifn.Cases)
                    {
                        if (BodyReadsBinding(cs.Condition, name)) return true;
                        if (BodyReadsBinding(cs.Expr, name)) return true;
                    }
                    if (ifn.ElseCase.HasValue && BodyReadsBinding(ifn.ElseCase.Value.Expr, name))
                        return true;
                    return false;
                }
                case AstNodeType.While:
                {
                    var wn = (Parser.Nodes.Statements.WhileNode)node;
                    return BodyReadsBinding(wn.ConditionNode, name)
                        || BodyReadsBinding(wn.BodyNode, name);
                }
                case AstNodeType.DoWhile:
                {
                    var dw = (Parser.Nodes.Statements.DoWhileNode)node;
                    return BodyReadsBinding(dw.ConditionNode, name)
                        || BodyReadsBinding(dw.BodyNode, name);
                }
                case AstNodeType.For:
                {
                    var fn = (Parser.Nodes.Statements.ForNode)node;
                    string? innerName = fn.VarNameTok.Value?.ToString();
                    if (BodyReadsBinding(fn.StartValueNode, name)) return true;
                    if (BodyReadsBinding(fn.EndValueNode, name)) return true;
                    if (fn.StepValueNode != null && BodyReadsBinding(fn.StepValueNode, name)) return true;
                    if (innerName == name) return false; // shadowed
                    return BodyReadsBinding(fn.BodyNode, name);
                }
                case AstNodeType.ForEach:
                {
                    var fe = (Parser.Nodes.Statements.ForEachNode)node;
                    string? innerName = fe.VarNameToken.Value?.ToString();
                    if (BodyReadsBinding(fe.CollectionNode, name)) return true;
                    if (innerName == name) return false; // shadowed
                    return BodyReadsBinding(fe.BodyNode, name);
                }
                case AstNodeType.Return:
                {
                    var rn = (Parser.Nodes.Functions.ReturnNode)node;
                    return rn.NodeToReturn != null && BodyReadsBinding(rn.NodeToReturn, name);
                }
                case AstNodeType.Throw:
                {
                    var tn = (Parser.Nodes.Statements.ThrowNode)node;
                    return tn.Expression != null && BodyReadsBinding(tn.Expression, name);
                }
                case AstNodeType.MemberAccess:
                {
                    var ma = (Parser.Nodes.Structs.MemberAccessNode)node;
                    return BodyReadsBinding(ma.TargetNode, name);
                }
                case AstNodeType.MemberAssignment:
                {
                    var ma = (Parser.Nodes.Structs.MemberAssignmentNode)node;
                    return BodyReadsBinding(ma.TargetNode, name)
                        || BodyReadsBinding(ma.ValueNode, name);
                }
                case AstNodeType.ListAccess:
                {
                    var la = (Parser.Nodes.Variables.ListAccessNode)node;
                    return BodyReadsBinding(la.Target, name)
                        || BodyReadsBinding(la.Index, name);
                }
                case AstNodeType.ListAssignment:
                {
                    var la = (Parser.Nodes.Variables.ListAssignmentNode)node;
                    return BodyReadsBinding(la.Target, name)
                        || BodyReadsBinding(la.Value, name);
                }
                case AstNodeType.Ternary:
                {
                    var tn = (Parser.Nodes.Operations.TernaryNode)node;
                    return BodyReadsBinding(tn.Condition, name)
                        || BodyReadsBinding(tn.TrueExpression, name)
                        || BodyReadsBinding(tn.FalseExpression, name);
                }
                case AstNodeType.NullCoalescing:
                {
                    var nc = (Parser.Nodes.Operations.NullCoalescingNode)node;
                    return BodyReadsBinding(nc.Left, name)
                        || BodyReadsBinding(nc.Right, name);
                }
                case AstNodeType.Cast:
                {
                    var cn = (Parser.Nodes.Operations.CastNode)node;
                    return BodyReadsBinding(cn.Expression, name);
                }
                case AstNodeType.IsType:
                {
                    var ist = (Parser.Nodes.Operations.IsTypeNode)node;
                    return BodyReadsBinding(ist.Expression, name);
                }
                case AstNodeType.Range:
                {
                    var rn = (Parser.Nodes.Operations.RangeNode)node;
                    if (BodyReadsBinding(rn.Start, name)) return true;
                    if (BodyReadsBinding(rn.End, name)) return true;
                    if (rn.Step != null && BodyReadsBinding(rn.Step, name)) return true;
                    return false;
                }
                case AstNodeType.List:
                {
                    var ln = (Parser.Nodes.Primitives.ListNode)node;
                    foreach (var e in ln.ElementNodes)
                        if (BodyReadsBinding(e, name)) return true;
                    return false;
                }
                case AstNodeType.Set:
                {
                    var sn = (Parser.Nodes.Primitives.SetNode)node;
                    foreach (var e in sn.ElementNodes)
                        if (BodyReadsBinding(e, name)) return true;
                    return false;
                }
                case AstNodeType.Tuple:
                {
                    var tn = (Parser.Nodes.Primitives.TupleNode)node;
                    foreach (var e in tn.ElementNodes)
                        if (BodyReadsBinding(e, name)) return true;
                    return false;
                }
                case AstNodeType.Typeof:
                {
                    var tn = (Parser.Nodes.Special.TypeofNode)node;
                    return BodyReadsBinding(tn.Node, name);
                }
                case AstNodeType.Dereference:
                {
                    var dn = (Parser.Nodes.Operations.DereferenceNode)node;
                    return BodyReadsBinding(dn.Target, name);
                }
                case AstNodeType.Spread:
                {
                    var sn = (Parser.Nodes.Operations.SpreadNode)node;
                    return BodyReadsBinding(sn.Expression, name);
                }
                case AstNodeType.EnumAccess:
                {
                    var ea = (Parser.Nodes.Enums.EnumAccessNode)node;
                    return BodyReadsBinding(ea.EnumNode, name);
                }
                // Conservative TRUE for any node kind not enumerated —
                // closures (could capture), Try/Match (binding shapes),
                // imports, definitions, async/yield, inline asm, etc.
                default:
                    return true;
            }
        }

        // M84 — conservative scan for break/continue at THIS loop's
        // level. Returns true if either keyword would target the
        // enclosing loop (rejecting unroll). Nested loops and
        // function bodies absorb their own break/continue and stop
        // recursion. Unknown node types return TRUE (conservative —
        // we'd rather skip unroll than miscompile a body that uses
        // break/continue through a shape we don't recognise).
        private static bool BodyHasBreakOrContinueAtThisLevel(AstNode? node)
        {
            if (node == null) return false;
            switch (node.NodeType)
            {
                case AstNodeType.Break:
                case AstNodeType.Continue:
                    return true;
                // Nested loops absorb their own break/continue —
                // safe to skip.
                case AstNodeType.For:
                case AstNodeType.ForEach:
                case AstNodeType.While:
                case AstNodeType.DoWhile:
                case AstNodeType.SuperFor:
                case AstNodeType.ForAwait:
                // Function / closure bodies introduce their own
                // loop context — break/continue inside them refers
                // to inner loops only.
                case AstNodeType.FunctionDefinition:
                // Match has its own case-switching control flow.
                case AstNodeType.Match:
                    return false;
                case AstNodeType.Scope:
                {
                    var sn = (Parser.Nodes.Special.ScopeNode)node;
                    foreach (var n in sn.Nodes)
                    {
                        if (BodyHasBreakOrContinueAtThisLevel(n)) return true;
                    }
                    return false;
                }
                // Leaf statements with no nested break/continue
                // possibility.
                case AstNodeType.Number:
                case AstNodeType.String:
                case AstNodeType.Null:
                case AstNodeType.Boolean:
                case AstNodeType.VariableAccess:
                case AstNodeType.Pass:
                case AstNodeType.Return:
                case AstNodeType.Throw:
                case AstNodeType.RegexLiteral:
                    return false;
                // Conservative for everything else — assume
                // break/continue might be reachable via a sub-tree
                // we don't statically know how to walk.
                default:
                    return true;
            }
        }

        // Compile a List/Set/Tuple literal: lay each element in a
        // consecutive slot, then emit the corresponding NewX opcode. Spread
        // expansion (`...x`) is not yet supported — the eligibility check
        // above rejects literals that contain it.
        private static void CompileCollectionLiteral(
            List<AstNode> elements, byte destSlot, Opcode newOp,
            State st, ref byte topSlot)
        {
            int count = elements.Count;
            if (count > byte.MaxValue)
                throw new IrCompileException($"{newOp} literal has too many elements (>255)");
            foreach (var e in elements)
                if (e.NodeType == AstNodeType.Spread)
                    throw new IrCompileException("collection literal with spread not yet lowered");
            byte baseSlot = topSlot;
            for (int i = 0; i < count; i++) AllocTemp(ref topSlot);
            for (int i = 0; i < count; i++)
                CompileExpression(elements[i], (byte)(baseSlot + i), st, ref topSlot);
            st.Code.Emit3(newOp, destSlot, baseSlot, (byte)count);
        }

        // M5 eligibility for native FunctionCall: only the simple positional
        // form. Named args, ref args, spread expansion, and explicit
        // generic-type arguments all bail to OP_VISIT_AST; they require
        // dedicated argument-list infrastructure that lands in later
        // milestones.
        private static bool IsCallNativelyCompilable(FunctionCallNode node)
        {
            if (node.GenericTypeArgs != null && node.GenericTypeArgs.Count > 0) return false;
            foreach (var arg in node.ArgNodes)
            {
                if (arg.IsRef) return false;
                if (arg.NameTok != null) return false;
                if (arg.Expr.NodeType == AstNodeType.Spread) return false;
            }
            return true;
        }

        private static List<AstNode> FlattenStatements(AstNode root)
        {
            if (root is ScopeNode sc) return sc.Nodes;
            return new List<AstNode> { root };
        }

        // Backward-jump patcher (continue fixups in while/dowhile/for). The
        // current InstructionBuilder only patches forward jumps to the
        // current Pc; backward patches need a custom helper that rewrites
        // the imm16 in-place via the snapshot-and-rebuild path.
        private static void PatchJumpsBackward(State st, List<int> jumpPcs, int targetPc)
        {
            foreach (var jpc in jumpPcs)
            {
                int offset = targetPc - (jpc + 1);
                if (offset < short.MinValue || offset > short.MaxValue)
                    throw new IrCompileException($"backward jump out of 16-bit range ({offset})");
                var snapshot = st.Code.ToArray();
                uint instr = snapshot[jpc];
                uint patched = (instr & 0x0000FFFFu) | ((uint)(ushort)(short)offset << 16);
                int total = snapshot.Length;
                st.Code.Truncate(jpc);
                st.Code.Emit(patched);
                for (int k = jpc + 1; k < total; k++) st.Code.Emit(snapshot[k]);
            }
        }
    }
}

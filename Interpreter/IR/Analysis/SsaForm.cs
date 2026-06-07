using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M56: Static-Single-Assignment form over the CFG. Analysis-only:
    // does NOT mutate `RaFunction.Code`. The SSA representation lives
    // alongside the linear bytecode and is consumed by future
    // optimisation passes (DCE, CSE, SCCP, copy propagation) and by the
    // tier-up JIT (M57) which lowers SSA → machine code.
    //
    // "Variables" here are the byte-indexed `locals[a]` temp slots used
    // by the dispatch loop. Each opcode that writes `a` defines a new
    // SSA value; each opcode that reads `a` (b or c operand) picks up
    // the most recent dominating definition.
    //
    // Algorithm (classic Cytron-Ferrante-Rosen-Wegman-Zadeck):
    //   1. Discover defs per slot.
    //   2. For every slot with > 1 defining block, iterate the
    //      dominance frontier to compute the phi-placement set
    //      (`Phis[block][slot]`).
    //   3. Rename: a single dom-tree DFS pushes a version number per
    //      use, pops on exit. Phi args at successor entries are filled
    //      with the current version on exit from each predecessor.
    public sealed class SsaForm
    {
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;
        // Phis[blockId] = mapping of slot -> SSA version of the phi
        // value defined at this block's entry. Empty when the slot has
        // no phi at this block.
        public readonly Dictionary<int, Dictionary<int, int>> Phis = new();
        // For each (blockId, slot, version) phi: the per-predecessor arg
        // versions, indexed parallel to `Cfg.Blocks[blockId].Predecessors`.
        public readonly Dictionary<(int Block, int Slot, int Version), int[]> PhiArgs = new();
        // Slot -> last assigned version across whole function. Used as
        // a monotonic version-counter source during renaming.
        public readonly Dictionary<int, int> SlotVersionCount = new();
        // Per (PC, "is operand a/b/c") -> the SSA version of the slot
        // referenced by that operand. Lookup keyed `(pc, slotByte)`
        // since the dispatch loop already decodes A/B/C inline.
        public readonly Dictionary<(int Pc, int Slot), int> UseVersions = new();
        // Per (PC, defined slot) -> version produced by that PC.
        public readonly Dictionary<(int Pc, int Slot), int> DefVersions = new();

        private SsaForm(ControlFlowGraph cfg, Dominators dom)
        {
            Cfg = cfg;
            Dom = dom;
            foreach (var b in cfg.Blocks) Phis[b.Id] = new Dictionary<int, int>();
        }

        public static SsaForm Build(ControlFlowGraph cfg, Dominators dom)
        {
            var ssa = new SsaForm(cfg, dom);
            ssa.PlacePhis();
            ssa.Rename();
            return ssa;
        }

        // ----- Step 1 + 2: discover defs and place phis. -----
        private void PlacePhis()
        {
            // defsPerSlot[slot] = set of blocks that define `slot`.
            var defsPerSlot = new Dictionary<int, HashSet<int>>();
            foreach (var b in Cfg.Blocks)
            {
                for (int pc = b.StartPc; pc < b.EndPcExclusive; pc++)
                {
                    int? writtenSlot = DefinedSlot(Cfg.Function.Code[pc], Cfg.Function, pc);
                    if (writtenSlot.HasValue)
                    {
                        if (!defsPerSlot.TryGetValue(writtenSlot.Value, out var set))
                        {
                            set = new HashSet<int>();
                            defsPerSlot[writtenSlot.Value] = set;
                        }
                        set.Add(b.Id);
                    }
                    int? secondSlot = SecondaryDefinedSlot(Cfg.Function.Code[pc]);
                    if (secondSlot.HasValue)
                    {
                        if (!defsPerSlot.TryGetValue(secondSlot.Value, out var set2))
                        {
                            set2 = new HashSet<int>();
                            defsPerSlot[secondSlot.Value] = set2;
                        }
                        set2.Add(b.Id);
                    }
                }
            }

            var df = Dom.DominanceFrontiers();
            foreach (var kv in defsPerSlot)
            {
                int slot = kv.Key;
                var defBlocks = kv.Value;
                if (defBlocks.Count < 2) continue; // single-def slot needs no phi
                // Iterated DF: keep adding DF members until fixpoint.
                var worklist = new Queue<int>(defBlocks);
                var phiBlocks = new HashSet<int>();
                while (worklist.Count > 0)
                {
                    int x = worklist.Dequeue();
                    foreach (var y in df[x])
                    {
                        if (phiBlocks.Add(y))
                        {
                            // Reserve a version slot (filled during rename).
                            Phis[y][slot] = -1;
                            if (!defBlocks.Contains(y))
                                worklist.Enqueue(y);
                        }
                    }
                }
            }
        }

        // ----- Step 3: rename via dom-tree DFS. -----
        private void Rename()
        {
            // Stacks per slot of currently-live versions.
            var stacks = new Dictionary<int, Stack<int>>();
            int FreshVersion(int slot)
            {
                if (!SlotVersionCount.TryGetValue(slot, out var v)) v = 0;
                v++;
                SlotVersionCount[slot] = v;
                if (!stacks.TryGetValue(slot, out var st))
                {
                    st = new Stack<int>();
                    stacks[slot] = st;
                }
                st.Push(v);
                return v;
            }
            int CurrentVersion(int slot)
            {
                if (stacks.TryGetValue(slot, out var st) && st.Count > 0) return st.Peek();
                return 0; // "uninitialised" — caller-provided / undefined
            }

            var tree = Dom.BuildDominatorTree();
            // Track per-block which slots got pushes so DFS-exit can
            // pop them in reverse order.
            void Visit(int bId)
            {
                var bb = Cfg.Blocks[bId];
                var pushedSlots = new List<int>();
                // Assign versions to this block's phis FIRST (they
                // dominate every other def in the block).
                foreach (var kv in Phis[bId])
                {
                    int slot = kv.Key;
                    int v = FreshVersion(slot);
                    Phis[bId][slot] = v;
                    pushedSlots.Add(slot);
                }
                // Sequential pass over the block's opcodes:
                // record uses (current version), assign defs.
                for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                {
                    uint instr = Cfg.Function.Code[pc];
                    var op = Encoding.DecodeOp(instr);
                    // Operand reads: b and c slots (for 3-address opcodes
                    // that consume them).
                    foreach (var (slot, isUse) in OperandReads(op, instr, Cfg.Function, pc))
                    {
                        if (!isUse) continue;
                        UseVersions[(pc, slot)] = CurrentVersion(slot);
                    }
                    int? def = DefinedSlot(instr, Cfg.Function, pc);
                    if (def.HasValue)
                    {
                        int v = FreshVersion(def.Value);
                        DefVersions[(pc, def.Value)] = v;
                        pushedSlots.Add(def.Value);
                    }
                    int? def2 = SecondaryDefinedSlot(instr);
                    if (def2.HasValue && def2.Value != (def ?? -1))
                    {
                        int v2 = FreshVersion(def2.Value);
                        DefVersions[(pc, def2.Value)] = v2;
                        pushedSlots.Add(def2.Value);
                    }
                }
                // Fill phi args at every successor whose phis read this
                // block's exiting versions.
                foreach (var s in bb.Successors)
                {
                    int predIdx = Cfg.Blocks[s].Predecessors.IndexOf(bId);
                    if (predIdx < 0) continue;
                    foreach (var kv in Phis[s])
                    {
                        int slot = kv.Key;
                        int phiVersion = kv.Value;
                        var key = (s, slot, phiVersion);
                        if (!PhiArgs.TryGetValue(key, out var args))
                        {
                            args = new int[Cfg.Blocks[s].Predecessors.Count];
                            PhiArgs[key] = args;
                        }
                        args[predIdx] = CurrentVersion(slot);
                    }
                }
                // DFS into children.
                foreach (var c in tree[bId]) Visit(c);
                // Pop versions pushed in this block (reverse order).
                pushedSlots.Reverse();
                foreach (var slot in pushedSlots)
                {
                    if (stacks.TryGetValue(slot, out var st) && st.Count > 0) st.Pop();
                }
            }
            if (Cfg.Blocks.Count > 0) Visit(0);
        }

        // ----- Opcode-shape helpers. -----
        //
        // `DefinedSlot` returns the slot a given opcode writes, or
        // null if the opcode does not produce a tracked slot
        // (control flow, scope mutations, ...).
        //
        // Slot ids are INT. Locals occupy `[0, 256)`. M67 piggybacks
        // a SymbolEntry-slot namespace above `SymbolEntrySlotBase`
        // (= 0x10000) so SSA can place phis and rename per-binding
        // writes. `var x = 5; var y = x + 1;` becomes a two-version
        // chain on slot `SymbolEntrySlotBase + slot(x)`, which SCCP
        // collapses to the constant `6` at the def site of `y`.
        //
        // SymbolEntry resolution differs per opcode:
        //   DeclareLocal       imm16 = AstRef index → DeclSlotByAstRef.
        //   StoreLocalS        imm16 = SlotLocals index directly.
        //   AssignBinding      imm16 = name index   → NameToSlot.
        //   StoreGlobal        imm16 = AstRef index → node.Name → NameToSlot.
        //   AddIntoSlot        imm16 = SlotLocals index directly.
        //   SubIntoSlot        imm16 = SlotLocals index directly.
        //   AddIntoSlotImm     A     = SlotLocals index directly.
        //   SubIntoSlotImm     A     = SlotLocals index directly.
        //   LoadLocalS         imm16 = SlotLocals index directly (read).
        //   LoadGlobal         imm16 = name index   → NameToSlot (read).
        // Resolution failure returns null → SSA leaves the opcode
        // untracked, preserving the pre-M67 conservative behaviour
        // (the existing `locals[A]` write is still tracked via the
        // separate def-slot path on opcodes that ALSO produce one,
        // e.g. LoadLocalS / LoadGlobal).
        //
        // `OperandReads` yields each (slot, true) the opcode reads
        // as an SSA use. Implemented conservatively: only the
        // shapes the dispatch loop actually decodes as slot reads
        // on the hot path.
        public const int SymbolEntrySlotBase = 0x10000;

        // Public so Sccp.VisitPc can compute the def-slot for
        // opcodes that write a SymbolEntry slot rather than (or in
        // addition to) `locals[A]`. The RaFunction reference
        // resolves DeclareLocal's astRef → frame slot via
        // `DeclSlotByAstRef`, and AssignBinding / StoreGlobal /
        // LoadGlobal via the name lookup tables.
        public static int? DefinedSlotOf(uint instr, RaFunction fn, int pc) => DefinedSlot(instr, fn, pc);
        public static int? DefinedSlotOf(uint instr, RaFunction fn) => DefinedSlot(instr, fn, -1);

        // A handful of opcodes write TWO `locals[]` slots, but the SSA
        // backbone (`DefinedSlot`) returns a single slot. This models the
        // SECOND write so it gets its own fresh SSA version.
        //
        // ForEachStreamPull `[op][itemSlot:a][streamSlot:b][continueSlot:c]`
        // writes itemSlot (A, the primary def) AND continueSlot (C, the
        // loop-continue boolean). Without the C def the immediately-following
        // `JmpIfNot continueSlot` resolves its use to whatever earlier write
        // touched that physical slot — and if a prior `LoadConst` left a
        // constant there (common once the surrounding fn is big enough to be
        // optimised), SCCP folds the loop-exit branch away ⇒ the stream
        // foreach spins forever. Giving C a fresh version each pull shadows
        // that stale constant and forces the branch to stay dynamic.
        public static int? SecondaryDefinedSlot(uint instr)
        {
            return Encoding.DecodeOp(instr) == Opcode.ForEachStreamPull
                ? Encoding.C(instr)
                : (int?)null;
        }

        // Helper: resolve the SymbolEntry slot that an opcode reads
        // or writes via its AST-side `node.Name`. Returns null when
        // the AST ref doesn't carry a name we can resolve to a
        // tracked NameToSlot entry.
        private static int? SymbolEntrySlotFromAstRef(uint instr, RaFunction fn)
        {
            if (fn.NameToSlot == null) return null;
            int astIdx = Encoding.Imm16(instr);
            if ((uint)astIdx >= (uint)fn.AstRefs.Length) return null;
            string? name = null;
            if (fn.AstRefs[astIdx] is Parser.Nodes.Variables.VariableAssignmentNode va)
                name = va.Name;
            if (string.IsNullOrEmpty(name)) return null;
            if (!fn.NameToSlot.TryGetValue(name, out int frame)) return null;
            return SymbolEntrySlotBase + frame;
        }

        private static bool HasEh(RaFunction fn) => fn.EhTable != null && fn.EhTable.Length > 0;

        // M67 alias barrier: returns the lowest PC at which the
        // function executes an opcode that can implicitly modify a
        // SymbolEntry behind SSA's back. The classical aliasing
        // issues:
        //
        //   * `Call` / `CallKw` / `CallMethod` / `TailCall` / `Spawn`
        //     / `NewInstance` invoke user code that, via implicit
        //     closure capture (Ra's default), can mutate ANY
        //     reachable binding — `x` is aliasable through any
        //     closure that captured it.
        //   * `NativeDefine` dispatches an AST visitor that mutates
        //     SymbolEntry values directly (extensions / traits /
        //     enums / using-namespace), bypassing the VM dispatch.
        //   * `Await` may suspend then resume on a different fiber
        //     whose body mutated shared state.
        //   * EH transfers (`Throw` / catch handler entry) skip
        //     CFG-modelled control flow; the SCCP solver would
        //     leave catch / finally bodies unreachable and miss
        //     phi merges.
        //
        // M77 — region-based per-PC bitmap. Replaces M67's
        // function-global single-int barrier (`AliasBarrierPc`)
        // which collapsed to 0 (full off) the moment ANY EH region
        // or back-edge existed — meaning real Ra programs that use
        // try / catch + loops never saw Memory-SSA tracking.
        //
        // The bitmap marks each PC eligible (`true`) or ineligible
        // (`false`). The sound rule for the per-PC eligibility is:
        // a PC is eligible iff every PC reachable in the CFG from
        // function entry to this PC is also eligible. That keeps
        // post-region readers from picking up pre-region SCCP
        // values that intervening ineligible writers might have
        // invalidated.
        //
        // For the current pass we approximate the soundness rule
        // by treating eligibility as a LINEAR prefix: PCs strictly
        // before the first aliasing event are eligible; everything
        // else is ineligible. The first aliasing event is:
        //
        //   min(
        //     first EH region's StartPc,
        //     earliest back-edge target PC across all back-edges,
        //     first aliasing call opcode PC
        //   )
        //
        // This recovers the straight-line prefix that M67 lost
        // (M67 would collapse to 0 the moment EH or back-edge
        // existed anywhere in the function — even after a long
        // straight-line preamble). The bitmap representation
        // stays in place so a future pass can refine to per-PC
        // or per-slot eligibility without changing the read API
        // (`MemSsaEligibleAt`).
        //
        // Soundness note for the linear prefix: any PC <
        // firstEvent is dominated by function entry without
        // passing through a loop body, an EH region, or an
        // aliasing call. SE writes / reads in that prefix
        // therefore satisfy the SSA / SCCP per-version invariant.
        private static bool[] BuildMemSsaEligibility(RaFunction fn)
        {
            if (fn._memSsaBarrierCached != 0 && fn._memSsaEligible != null)
                return fn._memSsaEligible;
            int n = fn.Code.Length;
            var elig = new bool[n];
            int firstEvent = n;

            // (a) Earliest EH region start. With EhTable sorted by
            // StartPc (which the IR compiler maintains) the first
            // entry's StartPc is the earliest. Guard against
            // unsorted EhTable just in case.
            for (int e = 0; e < fn.EhTable.Length; e++)
            {
                var eh = fn.EhTable[e];
                if (eh.StartPc >= 0 && eh.StartPc < firstEvent)
                    firstEvent = eh.StartPc;
            }

            // (b) Earliest back-edge target. Scan the whole code
            // because back-edges can appear at any PC and target
            // earlier PCs — the lowest such target marks the
            // start of the earliest loop in linear order.
            for (int pc = 0; pc < n; pc++)
            {
                uint instr = fn.Code[pc];
                var o = Encoding.DecodeOp(instr);
                switch (o)
                {
                    case Opcode.Jmp:
                    case Opcode.JmpIf:
                    case Opcode.JmpIfNot:
                    case Opcode.AndJz:
                    case Opcode.OrJnz:
                    case Opcode.NCJz:
                    case Opcode.ForTest:
                    case Opcode.ForEachNext:
                    case Opcode.JmpIfStream:
                    {
                        short imm = Encoding.SImm16(instr);
                        if (imm < 0)
                        {
                            int target = pc + 1 + imm;
                            if (target < 0) target = 0;
                            if (target < firstEvent) firstEvent = target;
                        }
                        break;
                    }
                }
            }

            // (c) First aliasing call. Linear scan — break on the
            // first match because earlier calls dominate any
            // later eligibility window.
            for (int pc = 0; pc < n; pc++)
            {
                var o = Encoding.DecodeOp(fn.Code[pc]);
                if (o == Opcode.Call || o == Opcode.CallKw
                    || o == Opcode.CallMethod || o == Opcode.TailCall
                    || o == Opcode.Spawn || o == Opcode.NewInstance
                    || o == Opcode.NativeDefine || o == Opcode.Await
                    || o == Opcode.AsmInvoke || o == Opcode.AsmInvokeI
                    || o == Opcode.AnnotationApply || o == Opcode.CallGeneric
                    || o == Opcode.Throw)
                {
                    if (pc < firstEvent) firstEvent = pc;
                    break;
                }
            }

            for (int pc = 0; pc < n; pc++)
                elig[pc] = pc < firstEvent;

            fn._memSsaEligible = elig;
            fn._memSsaBarrierCached = 1;
            return elig;
        }

        // Per-PC eligibility predicate used by `DefinedSlot` /
        // `OperandReads` to admit SE tracking only on PCs the
        // M77 bitmap classifies as safe.
        private static bool MemSsaEligibleAt(RaFunction fn, int pc)
        {
            var elig = BuildMemSsaEligibility(fn);
            return (uint)pc < (uint)elig.Length && elig[pc];
        }

        private static int? SymbolEntrySlotFromName(uint instr, RaFunction fn)
        {
            if (fn.NameToSlot == null) return null;
            int nameIdx = Encoding.Imm16(instr);
            if ((uint)nameIdx >= (uint)fn.Names.Length) return null;
            var nm = fn.Names[nameIdx];
            if (string.IsNullOrEmpty(nm)) return null;
            if (!fn.NameToSlot.TryGetValue(nm, out int frame)) return null;
            return SymbolEntrySlotBase + frame;
        }

        private static int? DefinedSlot(uint instr) => DefinedSlot(instr, null, -1);
        private static int? DefinedSlot(uint instr, RaFunction? fn) => DefinedSlot(instr, fn, -1);
        private static int? DefinedSlot(uint instr, RaFunction? fn, int pc)
        {
            var op = Encoding.DecodeOp(instr);
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.LoadGlobal:
                case Opcode.LoadBuiltin:
                case Opcode.LoadUpval:
                case Opcode.LoadLocalS:
                case Opcode.Move:
                case Opcode.Alias:
                case Opcode.MoveLet:
                case Opcode.Borrow:
                case Opcode.BorrowMut:
                case Opcode.Deref:
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Div: case Opcode.Mod: case Opcode.Pow:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.Neg: case Opcode.Not: case Opcode.BNot:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                // `in` writes the BooleanValue result into locals[A] (like Eq).
                case Opcode.In:
                case Opcode.NullCoal:
                case Opcode.StrConcat: case Opcode.Interp: case Opcode.Fmt:
                case Opcode.With:
                case Opcode.NewList: case Opcode.NewMap:
                case Opcode.NewSet: case Opcode.NewTuple:
                case Opcode.ListGet: case Opcode.MapGet:
                case Opcode.Range:
                case Opcode.GetMember: case Opcode.EnumAccess:
                case Opcode.ForEachIterable: case Opcode.ListLen:
                case Opcode.ForEachStreamPull:
                case Opcode.Cast: case Opcode.Is:
                case Opcode.Typeof: case Opcode.Nameof:
                case Opcode.Closure: case Opcode.DefineFunction:
                case Opcode.GetSelf: case Opcode.GetSuper:
                case Opcode.Call: case Opcode.CallKw: case Opcode.CallMethod:
                case Opcode.CallGeneric:
                case Opcode.NewInstance:
                case Opcode.NativeDefine:
                case Opcode.DefineType:
                case Opcode.Await: case Opcode.Spawn:
                case Opcode.AsmInvoke: case Opcode.AsmInvokeI:
                case Opcode.AnnotationApply:
                case Opcode.EnumTagEq: case Opcode.EnumPayload:
                case Opcode.EnumNameEq:
                case Opcode.TupleShape:
                case Opcode.StructShape: case Opcode.StructFieldGet:
                case Opcode.ListShape: case Opcode.ListElemBack: case Opcode.ListRestSlice:
                case Opcode.IsType:
                case Opcode.MapShape: case Opcode.MapHasKey: case Opcode.MapGetKey:
                case Opcode.TryUnwrap:
                // M66.5 / M66.6: II tagged-union opcodes also write
                // `locals[a]` (or the `LongLocals[a]` shadow). SSA
                // tracks both arrays' writes uniformly so chain
                // analysis covers IR-compiler-emitted II ops too.
                case Opcode.LoadIntS64:
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                // M72 Float64 family writes A as well.
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                // M73 Bool family writes A.
                case Opcode.AndBB: case Opcode.OrBB:
                case Opcode.NotB:
                // M68 extended II/FF writes A.
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI: case Opcode.NegF:
                // M80 typed Pow writes A.
                case Opcode.PowII: case Opcode.PowFF:
                // String accumulator materialize writes the finished string
                // into locals[A]. Begin/Append define no slot (they mutate the
                // off-band per-frame StringBuilder) → they fall to `default`.
                case Opcode.StrAccMaterialize:
                    return Encoding.A(instr);

                // -------- M67 Memory-SSA: SymbolEntry writers --------
                //
                // Functions with EH handlers are exempted because
                // the M54 CFG builder does not model exception
                // edges. SSA would place defs unreachable from the
                // SCCP solver's reachable-edge set, and the missing
                // phi at the post-try merge would let stale Const
                // lattices survive across catch/finally bodies. The
                // gate keeps the optimisation off for any function
                // containing `try` until the CFG learns about
                // exception flow.
                case Opcode.DeclareLocal:
                {
                    if (fn == null || pc < 0 || !MemSsaEligibleAt(fn, pc)) return null;
                    int astIdx = Encoding.Imm16(instr);
                    if ((uint)astIdx >= (uint)fn.DeclSlotByAstRef.Length) return null;
                    int frame = fn.DeclSlotByAstRef[astIdx];
                    if (frame < 0) return null;
                    return SymbolEntrySlotBase + frame;
                }
                case Opcode.StoreLocalS:
                case Opcode.AddIntoSlot:
                case Opcode.SubIntoSlot:
                    if (fn == null || pc < 0 || !MemSsaEligibleAt(fn, pc)) return null;
                    return SymbolEntrySlotBase + Encoding.Imm16(instr);
                case Opcode.AddIntoSlotImm:
                case Opcode.SubIntoSlotImm:
                    if (fn == null || pc < 0 || !MemSsaEligibleAt(fn, pc)) return null;
                    return SymbolEntrySlotBase + Encoding.A(instr);
                case Opcode.AssignBinding:
                    return fn == null || pc < 0 || !MemSsaEligibleAt(fn, pc) ? (int?)null : SymbolEntrySlotFromName(instr, fn);
                case Opcode.StoreGlobal:
                    return fn == null || pc < 0 || !MemSsaEligibleAt(fn, pc) ? (int?)null : SymbolEntrySlotFromAstRef(instr, fn);
                default:
                    return null;
            }
        }

        internal static IEnumerable<(int Slot, bool IsUse)> OperandReads(Opcode op, uint instr) => OperandReads(op, instr, null, -1);
        internal static IEnumerable<(int Slot, bool IsUse)> OperandReads(Opcode op, uint instr, RaFunction? fn) => OperandReads(op, instr, fn, -1);
        internal static IEnumerable<(int Slot, bool IsUse)> OperandReads(Opcode op, uint instr, RaFunction? fn, int pc)
        {
            // 3-address binary / comparison opcodes: read B and C.
            switch (op)
            {
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Div: case Opcode.Mod: case Opcode.Pow:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                // `in` reads B (left) and C (right), same shape as Eq.
                case Opcode.In:
                case Opcode.NullCoal:
                case Opcode.ListGet: case Opcode.MapGet:
                // M66.5 / M66.6: II 3-address ops read B and C as
                // long operands. The chain analyzer relies on these
                // reads to verify slot-use coherence.
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.LtII:  case Opcode.LeII:
                case Opcode.GtII:  case Opcode.GeII:
                case Opcode.EqII:  case Opcode.NeII:
                // M72: FF 3-address ops have identical operand shape.
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF:  case Opcode.LeFF:
                case Opcode.GtFF:  case Opcode.GeFF:
                // M73: BB 3-address logic ops.
                case Opcode.AndBB: case Opcode.OrBB:
                // M68: extended II 3-address ops.
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                // M80: typed Pow.
                case Opcode.PowII: case Opcode.PowFF:
                // L7 map patterns: read B (map) + C (key slot), like Eq.
                case Opcode.MapHasKey: case Opcode.MapGetKey:
                    yield return (Encoding.B(instr), true);
                    yield return (Encoding.C(instr), true);
                    break;
                // II/FF unary bridges + M73 NotB + M68 NegI/NegF:
                // only B is read.
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.UnboxF:
                case Opcode.BoxF:
                case Opcode.NotB:
                case Opcode.NegI:
                case Opcode.NegF:
                    yield return (Encoding.B(instr), true);
                    break;
                // LoadIntS64: no operand reads (imm16 only).
                case Opcode.LoadIntS64:
                // Pure-immediate / no-operand loaders: encoding is
                // layout-2 (slot in A, imm16 in B|C), so the
                // default `yield B,C` fallback would mis-report
                // imm16 bytes as slot reads. Listing them here
                // explicitly emits an empty operand set.
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.LoadGlobal:
                case Opcode.LoadBuiltin:
                case Opcode.LoadUpval:
                case Opcode.Jmp:
                case Opcode.Pass:
                case Opcode.RetNull:
                case Opcode.FinallyEnd: // L10 — no operands (frame-state only)
                case Opcode.PushScope:
                case Opcode.PopScope:
                case Opcode.ClearScope:
                case Opcode.GetSelf:
                case Opcode.Closure:
                case Opcode.DefineFunction:
                case Opcode.GetSuper:
                case Opcode.Nameof:
                case Opcode.NativeDefine:
                // L5: OP_DEFINE_TYPE carries a TypeDefs index in imm16, not a
                // slot — reads no locals (the type is built from the descriptor).
                case Opcode.DefineType:
                // L9: OP_ASM_INVOKE carries a DefineRefs index in imm16, not a
                // slot — the asm source is constant, no operand slots are read.
                case Opcode.AsmInvoke:
                // L10: OP_ANNOTATION_APPLY — DefineRefs idx in imm16, no slot reads.
                case Opcode.AnnotationApply:
                // L3: borrows carry a Names[] index in imm16, not a slot — they
                // read no locals (the place is resolved by name at dispatch).
                case Opcode.Borrow:
                case Opcode.BorrowMut:
                    break;
                case Opcode.Move:
                case Opcode.Alias:
                case Opcode.MoveLet:
                case Opcode.Neg:
                case Opcode.Not:
                case Opcode.BNot:
                case Opcode.GetMember:
                case Opcode.EnumAccess:
                case Opcode.Cast:
                case Opcode.Is:
                case Opcode.Typeof:
                case Opcode.ForEachIterable:
                case Opcode.ListLen:
                case Opcode.Deref:
                case Opcode.Await:
                // L7 variant patterns: read the scrutinee at B; C is an immediate
                // (Names index / payload index / arity), not a slot. MatchArity
                // writes nothing (falls to DefinedSlot's default) but still reads
                // B — model the read so the scrutinee stays live to it.
                case Opcode.EnumTagEq:
                case Opcode.EnumPayload:
                case Opcode.MatchArity:
                case Opcode.EnumNameEq:
                case Opcode.TupleShape:
                case Opcode.StructShape: case Opcode.StructFieldGet:
                case Opcode.ListShape: case Opcode.ListElemBack: case Opcode.ListRestSlice:
                case Opcode.IsType:
                case Opcode.MapShape:
                case Opcode.TryUnwrap:
                    yield return (Encoding.B(instr), true);
                    break;
                case Opcode.Ret:
                case Opcode.RetYield:
                case Opcode.SetPendingFlow: // L10 — reads the value slot (A)
                case Opcode.Throw:
                case Opcode.Halt:
                case Opcode.StoreLocalS:
                case Opcode.SetLocalDirect:
                case Opcode.AssignBinding:
                case Opcode.StoreUpval:
                case Opcode.DeclareLocal:
                case Opcode.DeclareLocalByName:
                case Opcode.Emit:
                case Opcode.JmpIf:
                case Opcode.JmpIfNot:
                case Opcode.AndJz:
                case Opcode.OrJnz:
                case Opcode.NCJz:
                case Opcode.JmpIfStream:
                    yield return (Encoding.A(instr), true);
                    break;
                case Opcode.ForEachStreamPull:
                    // [op][itemSlot:a][streamSlot:b][continueSlot:c]. Reads
                    // the stream slot. The item and continue slots are
                    // outputs (written in WritesToSlot fixups below) — they
                    // are not read here.
                    yield return (Encoding.B(instr), true);
                    break;
                // Variable-arity reads (`base..base+count-1`). Without
                // enumerating these explicitly, DCE would treat their
                // input LoadConst defs as dead — the IR emits a span of
                // LoadConst writes immediately before the build opcode.
                case Opcode.NewList:
                case Opcode.NewSet:
                case Opcode.NewTuple:
                {
                    int b = Encoding.B(instr);
                    int count = Encoding.C(instr);
                    for (int i = 0; i < count; i++) yield return (b + i, true);
                    break;
                }
                case Opcode.NewMap:
                {
                    int b = Encoding.B(instr);
                    int pairCount = Encoding.C(instr);
                    for (int i = 0; i < pairCount * 2; i++) yield return (b + i, true);
                    break;
                }
                case Opcode.Range:
                {
                    // a (dst), b (base) → start, end, step occupy
                    // [base, base+1, base+2].
                    int b = Encoding.B(instr);
                    yield return (b, true);
                    yield return (b + 1, true);
                    yield return (b + 2, true);
                    break;
                }
                case Opcode.SetMember:
                case Opcode.SetIndex:
                {
                    // a (owner/target), b (value/idx). Both are reads;
                    // the write target is the heap object pointed to by
                    // `a`, not `locals[a]` itself.
                    yield return (Encoding.A(instr), true);
                    yield return (Encoding.B(instr), true);
                    // For SetIndex, value slot = idxSlot + 1 (contiguous
                    // layout the IR emitter pins).
                    if (op == Opcode.SetIndex) yield return (Encoding.B(instr) + 1, true);
                    break;
                }
                case Opcode.DerefStore:
                {
                    // L3: a is the result DEST (a def); the reads are the
                    // reference slot `b` and the RHS value slot `b+1` (the
                    // contiguous layout the IR emitter pins). Without modelling
                    // the b+1 read, DCE would delete the value-load as dead.
                    yield return (Encoding.B(instr), true);
                    yield return (Encoding.B(instr) + 1, true);
                    break;
                }
                case Opcode.ListSet:
                case Opcode.ListPush:
                // L10 ListExtend has the SAME shape as ListPush: mutates the
                // list at A, reads the iterable at B, no C operand. Like
                // ListPush it defines no `locals` slot (see DefinedSlot — both
                // fall to default → null) since it mutates a heap object, not
                // the register.
                case Opcode.ListExtend:
                case Opcode.MapSet:
                    yield return (Encoding.A(instr), true);
                    yield return (Encoding.B(instr), true);
                    if (op != Opcode.ListPush && op != Opcode.ListExtend) yield return (Encoding.C(instr), true);
                    break;
                case Opcode.Call:
                case Opcode.Spawn:
                {
                    // a (dst), b (fn slot), c (argCount). Args live at
                    // [fnSlot+1, fnSlot+1+count). The fn slot itself is
                    // also read.
                    int fnSlot = Encoding.B(instr);
                    int argCount = Encoding.C(instr);
                    yield return (fnSlot, true);
                    for (int i = 0; i < argCount; i++) yield return (fnSlot + 1 + i, true);
                    break;
                }
                case Opcode.TailCall:
                {
                    // [op][a:fnSlot][b:argBase][c:argCount].
                    int fnSlot = Encoding.A(instr);
                    int argBase = Encoding.B(instr);
                    int argCount = Encoding.C(instr);
                    yield return (fnSlot, true);
                    for (int i = 0; i < argCount; i++) yield return (argBase + i, true);
                    break;
                }
                case Opcode.AddIntoSlot:
                case Opcode.SubIntoSlot:
                    // RHS slot (A) is a `locals[]` read. The Imm16
                    // slot is the SymbolEntry-bound storage; M67
                    // tracks it in the parallel SE namespace so
                    // self-additive compound assigns participate in
                    // SCCP folding — but only when the function
                    // has no EH (see DefinedSlot's HasEh gate).
                    yield return (Encoding.A(instr), true);
                    if (fn != null && pc >= 0 && MemSsaEligibleAt(fn, pc))
                        yield return (SymbolEntrySlotBase + Encoding.Imm16(instr), true);
                    break;
                case Opcode.AddIntoSlotImm:
                case Opcode.SubIntoSlotImm:
                    // A is the SymbolEntry slot id (self read).
                    // simm16 is the literal RHS — no slot read.
                    if (fn != null && pc >= 0 && MemSsaEligibleAt(fn, pc))
                        yield return (SymbolEntrySlotBase + Encoding.A(instr), true);
                    break;
                // String accumulators: A is a `locals[]` read (the seed for
                // Begin, the append value for Append, the typed iter long slot
                // for AppendI). The StringBuilder side (imm16) is off-band
                // per-frame state, NOT a tracked SE/register slot — so it never
                // appears as a read or a def. Without this case the DCE would
                // drop the producer of the value slot.
                case Opcode.StrAccBegin:
                case Opcode.StrAccAppend:
                case Opcode.StrAccAppendI:
                    yield return (Encoding.A(instr), true);
                    break;
                // Materialize WRITES locals[A] (the finished string) and reads
                // only the off-band builder — no operand reads.
                case Opcode.StrAccMaterialize:
                    break;
                case Opcode.LoadLocalS:
                    // M67: SymbolEntry read of the slot referenced
                    // by imm16. The locals[A] write is tracked via
                    // DefinedSlot. Suppressed when EH is present —
                    // matches the writer-side gate.
                    if (fn != null && pc >= 0 && MemSsaEligibleAt(fn, pc))
                        yield return (SymbolEntrySlotBase + Encoding.Imm16(instr), true);
                    break;
                // LoadGlobal intentionally NOT tracked as a SE
                // read. The dispatcher walks the symbol-table parent
                // chain at runtime — scope pops remove the binding,
                // so SCCP cannot assume the slot still holds the
                // last-written value. LoadLocalS uses the frame's
                // slot cache and is scope-blind by design; that's
                // where the M67 read tracking lives.
                case Opcode.StoreGlobal:
                    // M67: A is the new RHS in `locals[]`. The SE
                    // slot read covers compound assigns (`x += 5`)
                    // where the dispatcher pulls the prior value
                    // before applying the operator. SCCP marks the
                    // resulting SE def Bottom (the operator lives
                    // on the AST node — not modelled here).
                    yield return (Encoding.A(instr), true);
                    if (fn != null && pc >= 0 && MemSsaEligibleAt(fn, pc))
                    {
                        var seSelf = SymbolEntrySlotFromAstRef(instr, fn);
                        if (seSelf.HasValue) yield return (seSelf.Value, true);
                    }
                    break;
                case Opcode.Interp:
                {
                    // a (dst), b (parts base), c (parts count).
                    int b = Encoding.B(instr);
                    int count = Encoding.C(instr);
                    for (int i = 0; i < count; i++) yield return (b + i, true);
                    break;
                }
                case Opcode.StrConcat:
                    yield return (Encoding.B(instr), true);
                    yield return (Encoding.C(instr), true);
                    break;
                case Opcode.Fmt:
                    yield return (Encoding.B(instr), true);
                    break;
                case Opcode.With:
                {
                    // [op][dst:a][base:b][defineRefIdx:c]. Reads the receiver at
                    // `base` plus the N contiguous update values at base+1..base+N.
                    // N comes from the WithExpressionNode parked in DefineRefs[c]
                    // (the c operand is a ref index, NOT a slot — never read it).
                    int wb = Encoding.B(instr);
                    yield return (wb, true);
                    int wc = Encoding.C(instr);
                    int wn = 0;
                    if (fn?.DefineRefs != null && wc < fn.DefineRefs.Length
                        && fn.DefineRefs[wc] is Parser.Nodes.Operations.WithExpressionNode wnode)
                        wn = wnode.Updates.Count;
                    for (int i = 0; i < wn; i++) yield return (wb + 1 + i, true);
                    break;
                }
                case Opcode.CallGeneric:
                {
                    // L10 — [op][dst:a][fnSlot:b][defineRefIdx:c]. Reads the callee
                    // at `fnSlot` plus the N args at fnSlot+1..fnSlot+N (With-shaped).
                    // N = ArgNodes.Count of the FunctionCallNode parked in
                    // DefineRefs[c] (c is a ref index, NOT a slot — never read it).
                    int gb = Encoding.B(instr);
                    yield return (gb, true);
                    int gc = Encoding.C(instr);
                    int gn = 0;
                    if (fn?.DefineRefs != null && gc < fn.DefineRefs.Length
                        && fn.DefineRefs[gc] is Parser.Nodes.Functions.FunctionCallNode gfc)
                        gn = gfc.ArgNodes.Count;
                    for (int i = 0; i < gn; i++) yield return (gb + 1 + i, true);
                    break;
                }
                case Opcode.AsmInvokeI:
                {
                    // L10 — [op][dst:a][argsBase:b][defineRefIdx:c]. Reads the N
                    // interpolation args at argsBase+0..argsBase+N-1 (no receiver,
                    // unlike With). N = count of AsmInterpPartNode in the parked
                    // AsmBlockNode at DefineRefs[c]; the c operand is a ref index,
                    // NOT a slot — never read it.
                    int ab = Encoding.B(instr);
                    int ac = Encoding.C(instr);
                    int an = 0;
                    if (fn?.DefineRefs != null && ac < fn.DefineRefs.Length
                        && fn.DefineRefs[ac] is Parser.Nodes.Asm.AsmBlockNode anode)
                    {
                        for (int i = 0; i < anode.Parts.Count; i++)
                            if (anode.Parts[i].NodeType == RaLanguage.Parser.Nodes.AstNodeType.AsmInterpPart) an++;
                    }
                    for (int i = 0; i < an; i++) yield return (ab + i, true);
                    break;
                }
                // M65 safety net: any opcode not explicitly enumerated
                // above is treated as if it reads BOTH `b` and `c`.
                // Over-approximates the use set, never under-counts —
                // ensures DCE never erases a def whose live use lives
                // in an opcode this enumerator forgot. False uses cost
                // a missed DCE opportunity, not a correctness bug.
                default:
                    yield return (Encoding.B(instr), true);
                    yield return (Encoding.C(instr), true);
                    break;
            }
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# SSA of {Cfg.Function.Name}");
            foreach (var b in Cfg.Blocks)
            {
                if (Phis[b.Id].Count == 0) continue;
                sb.Append($"  BB{b.Id} phis:");
                foreach (var kv in Phis[b.Id])
                {
                    sb.Append($" s{kv.Key}#{kv.Value}");
                    var key = (b.Id, kv.Key, kv.Value);
                    if (PhiArgs.TryGetValue(key, out var args))
                    {
                        sb.Append("(");
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (i > 0) sb.Append(",");
                            sb.Append($"BB{b.Predecessors[i]}:#{args[i]}");
                        }
                        sb.Append(")");
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}

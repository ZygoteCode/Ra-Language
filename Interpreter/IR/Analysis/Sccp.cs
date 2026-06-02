using System.Collections.Generic;
using System.Numerics;
using System.Text;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M62: Sparse Conditional Constant Propagation (Wegman-Zadeck 1991).
    //
    // Flow-sensitive lattice analysis on SSA. Each SSA def carries a
    // three-point lattice:
    //
    //     Top  ──>  Const(v)  ──>  Bottom
    //
    // Top = unanalysed (or unreachable predecessor for phi).
    // Const(v) = provably equal to literal `v` on every reachable path.
    // Bottom = "may be anything" — meet of two distinct constants or any
    // operand whose lattice is Bottom.
    //
    // Two worklists run to fixpoint:
    //
    //   * SSA worklist — (slot, version) pairs to revisit when their
    //     def's lattice value changes. Drives forward propagation
    //     through every use site.
    //   * Flow worklist — CFG edges to mark reachable. Branches whose
    //     condition is a constant suppress one successor edge; that
    //     edge stays unreachable, and any phi argument coming from
    //     it is treated as Top (not Bottom — distinguishing the two
    //     is the "conditional" half of SCCP and is what makes it
    //     strictly stronger than plain SCC propagation).
    //
    // Output:
    //   * `ConstantValues[(pc, slot)] = RuntimeValue` — every def the
    //     analysis proved constant. Codegen can fold the def into a
    //     literal load (or eliminate it entirely when no use remains).
    //   * `ReachableBlocks` — blocks the analysis proved reachable
    //     from entry under the deduced constant constraints.
    //   * `DeadBranches[pc] = bool` — branches whose condition folded;
    //     the bool records whether the branch is statically taken (true)
    //     or fallen-through (false). Lets the codegen eliminate the
    //     other arm.
    //
    // Only constant-folds the pure-arithmetic + comparison subset.
    // Division / modulo / power deliberately excluded — folding them
    // would change the observable error site for divide-by-zero, and
    // the conservative gain over leaving them on the dispatch path is
    // marginal.
    public sealed class Sccp
    {
        public readonly SsaForm Ssa;
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;

        public readonly Dictionary<(int Pc, int Slot), RuntimeValue> ConstantValues = new();
        public readonly HashSet<int> ReachableBlocks = new();
        public readonly Dictionary<int, bool> DeadBranches = new();

        // Three-point lattice.
        private enum LatKind : byte { Top = 0, Const = 1, Bottom = 2 }
        private readonly struct Lat
        {
            public readonly LatKind Kind;
            public readonly RuntimeValue? Value;
            public Lat(LatKind k, RuntimeValue? v) { Kind = k; Value = v; }
            public static readonly Lat Top = new(LatKind.Top, null);
            public static readonly Lat Bottom = new(LatKind.Bottom, null);
            public static Lat OfConst(RuntimeValue v) => new(LatKind.Const, v);
        }

        // Per-(slot, version) lattice. Defaults to Top via missing-key.
        private readonly Dictionary<(int Slot, int Version), Lat> _values = new();
        // CFG edges marked executable. Encoded as (predBlock, succBlock).
        private readonly HashSet<(int Pred, int Succ)> _execEdge = new();
        // Worklists.
        private readonly Queue<(int Pred, int Succ)> _flowWl = new();
        private readonly Queue<(int Slot, int Version)> _ssaWl = new();

        private Sccp(SsaForm ssa) { Ssa = ssa; Cfg = ssa.Cfg; Dom = ssa.Dom; }

        public static Sccp Run(SsaForm ssa)
        {
            var s = new Sccp(ssa);
            s.Solve();
            s.Materialise();
            return s;
        }

        // ----------------------------------------------------------
        // Solver.
        // ----------------------------------------------------------
        private void Solve()
        {
            if (Cfg.Blocks.Count == 0) return;
            // Seed: entry block is reachable from a synthetic
            // pre-entry edge.
            _flowWl.Enqueue((-1, 0));

            while (_flowWl.Count > 0 || _ssaWl.Count > 0)
            {
                if (_flowWl.Count > 0)
                {
                    var (pred, succ) = _flowWl.Dequeue();
                    if (!_execEdge.Add((pred, succ))) continue;
                    bool firstReach = ReachableBlocks.Add(succ);
                    // Visit phis at succ regardless.
                    VisitPhis(succ);
                    // Visit each non-phi instruction in succ on first
                    // reach; on subsequent reach edges only the phis
                    // need re-evaluation (their args change).
                    if (firstReach) VisitBlock(succ);
                }
                else
                {
                    var (slot, version) = _ssaWl.Dequeue();
                    // Re-visit every use site of this (slot, version).
                    foreach (var kv in Ssa.UseVersions)
                    {
                        if (kv.Value != version || kv.Key.Slot != slot) continue;
                        int pc = kv.Key.Pc;
                        int blockId = Cfg.PcToBlock[pc];
                        if (ReachableBlocks.Contains(blockId)) VisitPc(pc);
                    }
                    // Also re-visit any phi that reads this version.
                    foreach (var kv in Ssa.PhiArgs)
                    {
                        if (kv.Key.Slot != slot) continue;
                        foreach (var v in kv.Value)
                            if (v == version) { VisitPhi(kv.Key.Block, kv.Key.Slot, kv.Key.Version); break; }
                    }
                }
            }
        }

        private void VisitPhis(int block)
        {
            foreach (var kv in Ssa.Phis[block])
            {
                VisitPhi(block, kv.Key, kv.Value);
            }
        }

        private void VisitPhi(int block, int slot, int phiVersion)
        {
            var key = (block, slot, phiVersion);
            if (!Ssa.PhiArgs.TryGetValue(key, out var args)) return;
            var preds = Cfg.Blocks[block].Predecessors;
            Lat result = Lat.Top;
            for (int i = 0; i < preds.Count; i++)
            {
                // Only consider arguments coming from reachable
                // predecessor edges — the "conditional" in SCCP.
                if (!_execEdge.Contains((preds[i], block))) continue;
                int argVer = args[i];
                Lat argLat = Get(slot, argVer);
                result = Meet(result, argLat);
                if (result.Kind == LatKind.Bottom) break;
            }
            Set(slot, phiVersion, result);
        }

        private void VisitBlock(int block)
        {
            var bb = Cfg.Blocks[block];
            for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++) VisitPc(pc);
            // Wire successor edges.
            uint last = Cfg.Function.Code[bb.EndPcExclusive - 1];
            var op = Encoding.DecodeOp(last);
            int lastPc = bb.EndPcExclusive - 1;
            switch (bb.Kind)
            {
                case TerminatorKind.FallThrough:
                    if (bb.Successors.Count == 1)
                        _flowWl.Enqueue((block, bb.Successors[0]));
                    break;
                case TerminatorKind.Jump:
                    if (bb.Successors.Count == 1)
                        _flowWl.Enqueue((block, bb.Successors[0]));
                    break;
                case TerminatorKind.CondJump:
                {
                    // Successors[0] = fallthrough, [1] = branch target.
                    int condSlot = Encoding.A(last);
                    int condVersion = Ssa.UseVersions.TryGetValue((lastPc, condSlot), out var cv) ? cv : 0;
                    var condLat = Get(condSlot, condVersion);
                    if (condLat.Kind == LatKind.Const && condLat.Value != null)
                    {
                        bool truth = condLat.Value.IsTrue();
                        bool takeBranch = op switch
                        {
                            Opcode.JmpIfNot => !truth,
                            Opcode.JmpIf    => truth,
                            Opcode.AndJz    => !truth,
                            Opcode.OrJnz    => truth,
                            Opcode.NCJz     => condLat.Value.Type == RuntimeValueType.Null,
                            // ForTest / ForEachNext are loop tests —
                            // conservatively follow both edges; the
                            // iter slot is rarely a static constant.
                            _ => true,
                        };
                        if (op == Opcode.ForTest || op == Opcode.ForEachNext)
                        {
                            foreach (var s in bb.Successors) _flowWl.Enqueue((block, s));
                            break;
                        }
                        DeadBranches[lastPc] = takeBranch;
                        // Successors[0] = fallthrough, Successors[1] = branch target.
                        if (takeBranch && bb.Successors.Count >= 2)
                            _flowWl.Enqueue((block, bb.Successors[1]));
                        else if (!takeBranch && bb.Successors.Count >= 1)
                            _flowWl.Enqueue((block, bb.Successors[0]));
                    }
                    else
                    {
                        foreach (var s in bb.Successors) _flowWl.Enqueue((block, s));
                    }
                    break;
                }
                // Exit terminators: no successor edges.
                default: break;
            }
        }

        private void VisitPc(int pc)
        {
            uint instr = Cfg.Function.Code[pc];
            var op = Encoding.DecodeOp(instr);
            // M67: defer slot resolution to `SsaForm.DefinedSlotOf`
            // because opcodes like DeclareLocal / StoreLocalS /
            // AssignBinding / StoreGlobal write a SymbolEntry slot
            // (id ≥ `SymbolEntrySlotBase`) rather than `locals[A]`.
            // Pre-M67 the def-slot was always `Encoding.A(instr)` —
            // that branch still wins for everything tracked by the
            // legacy code path because `DefinedSlotOf` falls back
            // to A in those cases.
            // A secondary def (ForEachStreamPull's continueSlot) is a runtime
            // flag, never a compile-time constant — pin it to Bottom so the
            // loop-exit `JmpIfNot` is never folded against a stale value.
            int? secondOpt = SsaForm.SecondaryDefinedSlot(instr);
            if (secondOpt.HasValue
                && Ssa.DefVersions.TryGetValue((pc, secondOpt.Value), out var sver))
            {
                Set(secondOpt.Value, sver, Lat.Bottom);
            }

            int? defSlotOpt = SsaForm.DefinedSlotOf(instr, Cfg.Function, pc);
            if (!defSlotOpt.HasValue) return;
            int defSlot = defSlotOpt.Value;
            if (!Ssa.DefVersions.TryGetValue((pc, defSlot), out var version)) return;
            Lat newLat = Evaluate(op, instr, pc);
            Set(defSlot, version, newLat);
        }

        private static bool HasSsaDef(Opcode op) => SsaDefSlot(op) != null;

        // Lattice evaluation per opcode shape.
        private Lat Evaluate(Opcode op, uint instr, int pc)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                {
                    ushort idx = Encoding.Imm16(instr);
                    var c = Cfg.Function.Consts;
                    if (idx < c.Length && c[idx] != null) return Lat.OfConst(c[idx]!);
                    return Lat.Bottom;
                }
                case Opcode.LoadNull:    return Lat.OfConst(NullValue.Null);
                case Opcode.LoadTrue:    return Lat.OfConst(BooleanValue.Of(true));
                case Opcode.LoadFalse:   return Lat.OfConst(BooleanValue.Of(false));
                case Opcode.LoadIntS:
                    return Lat.OfConst(NumberValue.OfBigNumber(new BigNumber(new BigInteger(Encoding.SImm16(instr)), BigInteger.Zero)));
                // M76: LoadIntS64 mirrors LoadIntS in the abstract
                // lattice — both encode an immediate signed integer;
                // only the dispatch-time representation differs
                // (Int64-tagged vs boxed NumberValue). The IrRewriter
                // phase 1 sees this constant and excludes typed opcodes
                // from the LoadConst rewrite so the Int64 tag stays
                // intact at runtime (writing LoadConst would set
                // Tag=Ref and leave Bits stale, breaking downstream
                // typed readers such as the lazy-Range counter).
                case Opcode.LoadIntS64:
                    return Lat.OfConst(NumberValue.OfBigNumber(new BigNumber(new BigInteger(Encoding.SImm16(instr)), BigInteger.Zero)));
                // M76: tag-bridging opcodes are referentially transparent
                // — `BoxI x` produces the same abstract value as the
                // Int64-tagged slot it reads. SCCP treats them as
                // identity copies so a chain `LoadIntS64 1; BoxI; Add ...`
                // propagates the constant through the BoxI bridge.
                // The IrRewriter excludes them from the rewrite so the
                // boxed/unboxed tag-bridge stays intact at runtime.
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.UnboxF:
                case Opcode.BoxF:
                    return OperandLat(pc, Encoding.B(instr));
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.Eq: case Opcode.Ne: case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                // M76: typed II / FF / BB binary opcodes share the same
                // constant-folding lattice as their boxed siblings —
                // abstract value is the same RuntimeValue regardless
                // of whether dispatch goes through the Int64-tagged
                // fast path or the boxed slow path. `FoldBinary`
                // maps each typed opcode to its boxed-arithmetic
                // semantics. The IrRewriter phase 1 skips these
                // (typed rewrite would lose the typed tag).
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                case Opcode.AndBB: case Opcode.OrBB:
                {
                    var l = OperandLat(pc, Encoding.B(instr));
                    var r = OperandLat(pc, Encoding.C(instr));
                    if (l.Kind == LatKind.Bottom || r.Kind == LatKind.Bottom) return Lat.Bottom;
                    if (l.Kind == LatKind.Top || r.Kind == LatKind.Top) return Lat.Top;
                    var folded = FoldBinary(op, l.Value!, r.Value!);
                    return folded != null ? Lat.OfConst(folded) : Lat.Bottom;
                }
                case Opcode.Neg:
                case Opcode.Not:
                case Opcode.BNot:
                // M76: typed unary fold — NegI / NegF / NotB share the
                // boxed semantics. NegI on a NumberValue == Neg on
                // same; NotB on a BooleanValue == Not on same.
                case Opcode.NegI:
                case Opcode.NegF:
                case Opcode.NotB:
                {
                    var v = OperandLat(pc, Encoding.B(instr));
                    if (v.Kind != LatKind.Const) return v;
                    var folded = FoldUnary(op, v.Value!);
                    return folded != null ? Lat.OfConst(folded) : Lat.Bottom;
                }
                // ---------- M67 Memory-SSA transfer functions ---------
                //
                // SymbolEntry writers propagate the source `locals[]`
                // lattice into the SE slot's new SSA version. Readers
                // (`LoadLocalS` / `LoadGlobal`) materialise the SE
                // slot's lattice into the destination `locals[A]`.
                //
                // `StoreGlobal` is intentionally Bottom — the AST
                // node carries the compound operator (`+=`, `-=`,
                // `??=`, ...), which the IR doesn't model here. A
                // plain `x = expr` therefore loses constant
                // propagation, but no semantics break: SCCP simply
                // can't prove the new value constant.
                case Opcode.DeclareLocal:
                case Opcode.StoreLocalS:
                case Opcode.AssignBinding:
                    return OperandLat(pc, Encoding.A(instr));
                case Opcode.StoreGlobal:
                    return Lat.Bottom;
                case Opcode.LoadLocalS:
                    return OperandLat(pc, SsaForm.SymbolEntrySlotBase + Encoding.Imm16(instr));
                // LoadGlobal intentionally Bottom: see SsaForm.cs
                // — the runtime scope walk can fail after a
                // PushScope/PopScope sequence, so the SE slot's
                // last-stored value is not a sound source.
                case Opcode.AddIntoSlot:
                case Opcode.SubIntoSlot:
                {
                    var l = OperandLat(pc, SsaForm.SymbolEntrySlotBase + Encoding.Imm16(instr));
                    var r = OperandLat(pc, Encoding.A(instr));
                    if (l.Kind == LatKind.Bottom || r.Kind == LatKind.Bottom) return Lat.Bottom;
                    if (l.Kind == LatKind.Top || r.Kind == LatKind.Top) return Lat.Top;
                    var folded = FoldBinary(op == Opcode.AddIntoSlot ? Opcode.Add : Opcode.Sub, l.Value!, r.Value!);
                    return folded != null ? Lat.OfConst(folded) : Lat.Bottom;
                }
                case Opcode.AddIntoSlotImm:
                case Opcode.SubIntoSlotImm:
                {
                    var l = OperandLat(pc, SsaForm.SymbolEntrySlotBase + Encoding.A(instr));
                    if (l.Kind == LatKind.Bottom) return Lat.Bottom;
                    if (l.Kind == LatKind.Top) return Lat.Top;
                    short simm = Encoding.SImm16(instr);
                    var r = NumberValue.OfBigNumber(new BigNumber(new BigInteger(simm), BigInteger.Zero));
                    var folded = FoldBinary(op == Opcode.AddIntoSlotImm ? Opcode.Add : Opcode.Sub, l.Value!, r);
                    return folded != null ? Lat.OfConst(folded) : Lat.Bottom;
                }
                default:
                    // Conservative: any other opcode that writes a slot
                    // produces an unknown value.
                    return Lat.Bottom;
            }
        }

        // Mirror of `SsaForm.SymbolEntrySlotFromName` for SCCP's
        // LoadGlobal transfer. Returns the SE slot id when the name
        // resolves through `RaFunction.NameToSlot`.
        private int? LookupSymbolEntryReadFromName(uint instr)
        {
            var fn = Cfg.Function;
            if (fn.NameToSlot == null) return null;
            int nameIdx = Encoding.Imm16(instr);
            if ((uint)nameIdx >= (uint)fn.Names.Length) return null;
            var nm = fn.Names[nameIdx];
            if (string.IsNullOrEmpty(nm)) return null;
            if (!fn.NameToSlot.TryGetValue(nm, out int frame)) return null;
            return SsaForm.SymbolEntrySlotBase + frame;
        }

        private Lat OperandLat(int pc, int slot)
        {
            if (!Ssa.UseVersions.TryGetValue((pc, slot), out var version)) return Lat.Bottom;
            return Get(slot, version);
        }

        private static RuntimeValue? FoldBinary(Opcode op, RuntimeValue a, RuntimeValue b)
        {
            // Handle Number+Number arithmetic / comparison only; other
            // type combinations are dispatched through the boxed
            // operator at runtime and don't fold cleanly here.
            //
            // M76: typed II / FF opcodes are mapped to their boxed
            // semantics — the lattice value is the same regardless
            // of the dispatch-time tag representation. DivFF folds
            // here too (IEEE-754 division never throws). DivII and
            // ModII are intentionally absent — their deopt path
            // raises a RuntimeError on div-by-zero / signed-overflow
            // and SCCP must leave the error site at the original PC.
            if (a is NumberValue na && b is NumberValue nb)
            {
                try
                {
                    switch (op)
                    {
                        case Opcode.Add: case Opcode.AddNN:
                        case Opcode.AddII: case Opcode.AddFF:
                            return NumberValue.OfBigNumber(na.Value + nb.Value);
                        case Opcode.Sub: case Opcode.SubNN:
                        case Opcode.SubII: case Opcode.SubFF:
                            return NumberValue.OfBigNumber(na.Value - nb.Value);
                        case Opcode.Mul: case Opcode.MulNN:
                        case Opcode.MulII: case Opcode.MulFF:
                            return NumberValue.OfBigNumber(na.Value * nb.Value);
                        case Opcode.DivFF:
                            // IEEE-754 semantics — never throws.
                            // Guard against BigNumber div-by-zero
                            // raising (it would in the boxed slow
                            // path) by checking the divisor first.
                            if (nb.Value.IsZero()) return null;
                            return NumberValue.OfBigNumber(na.Value / nb.Value);
                        case Opcode.Lt: case Opcode.LtII: case Opcode.LtFF:
                            return BooleanValue.Of(na.Value < nb.Value);
                        case Opcode.Le: case Opcode.LeII: case Opcode.LeFF:
                            return BooleanValue.Of(na.Value <= nb.Value);
                        case Opcode.Gt: case Opcode.GtII: case Opcode.GtFF:
                            return BooleanValue.Of(na.Value > nb.Value);
                        case Opcode.Ge: case Opcode.GeII: case Opcode.GeFF:
                            return BooleanValue.Of(na.Value >= nb.Value);
                        case Opcode.Eq: case Opcode.SEq: case Opcode.EqII:
                            return BooleanValue.Of(na.Value == nb.Value);
                        case Opcode.Ne: case Opcode.SNe: case Opcode.NeII:
                            return BooleanValue.Of(na.Value != nb.Value);
                    }
                }
                catch { return null; }
            }
            // Boolean comparisons + BB family.
            if (a is BooleanValue ba && b is BooleanValue bb)
            {
                switch (op)
                {
                    case Opcode.Eq: case Opcode.SEq: case Opcode.EqII:
                        return BooleanValue.Of(ba.Value == bb.Value);
                    case Opcode.Ne: case Opcode.SNe: case Opcode.NeII:
                        return BooleanValue.Of(ba.Value != bb.Value);
                    case Opcode.AndBB:
                        return BooleanValue.Of(ba.Value && bb.Value);
                    case Opcode.OrBB:
                        return BooleanValue.Of(ba.Value || bb.Value);
                }
            }
            return null;
        }

        private static RuntimeValue? FoldUnary(Opcode op, RuntimeValue v)
        {
            switch (op)
            {
                case Opcode.Neg when v is NumberValue n:
                    return NumberValue.OfBigNumber(BigNumber.Zero - n.Value);
                // M76: NegI and NegF share boxed Neg semantics on a
                // NumberValue lattice operand. NumberValue carries
                // the full BigNumber payload so int / float roundtrip
                // through the same fold path.
                case Opcode.NegI when v is NumberValue ni:
                    return NumberValue.OfBigNumber(BigNumber.Zero - ni.Value);
                case Opcode.NegF when v is NumberValue nf:
                    return NumberValue.OfBigNumber(BigNumber.Zero - nf.Value);
                case Opcode.Not when v is BooleanValue b:
                    return BooleanValue.Of(!b.Value);
                case Opcode.Not:
                    return BooleanValue.Of(!v.IsTrue());
                // M76: NotB on a BooleanValue lattice operand.
                case Opcode.NotB when v is BooleanValue nb:
                    return BooleanValue.Of(!nb.Value);
                case Opcode.NotB:
                    return BooleanValue.Of(!v.IsTrue());
            }
            return null;
        }

        // Lattice meet — the "value at a merge point" of two abstract
        // values. Top is identity (Top ∧ X = X); Bottom is absorbing
        // (Bottom ∧ X = Bottom); two different constants meet to
        // Bottom; two equal constants stay constant.
        private static Lat Meet(Lat a, Lat b)
        {
            if (a.Kind == LatKind.Top) return b;
            if (b.Kind == LatKind.Top) return a;
            if (a.Kind == LatKind.Bottom || b.Kind == LatKind.Bottom) return Lat.Bottom;
            if (ReferenceEquals(a.Value, b.Value)) return a;
            // Structural equality on the constant payload — same
            // BigNumber/Bool/Null is a hit even if the interned
            // identity differs.
            if (a.Value is NumberValue na && b.Value is NumberValue nb && na.Value.Equals(nb.Value)) return a;
            if (a.Value is BooleanValue ba && b.Value is BooleanValue bb && ba.Value == bb.Value) return a;
            if (a.Value is NullValue && b.Value is NullValue) return a;
            return Lat.Bottom;
        }

        private Lat Get(int slot, int version)
        {
            return _values.TryGetValue((slot, version), out var l) ? l : Lat.Top;
        }

        private void Set(int slot, int version, Lat lat)
        {
            var prev = Get(slot, version);
            if (prev.Kind == lat.Kind &&
                (prev.Kind != LatKind.Const || Meet(prev, lat).Kind == LatKind.Const))
            {
                return;
            }
            _values[(slot, version)] = lat;
            _ssaWl.Enqueue((slot, version));
        }

        private static int? SsaDefSlot(Opcode op)
        {
            // Mirrors SsaForm.DefinedSlot exactly. Could be exposed as
            // public there if it's ever needed by a third pass.
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
                case Opcode.NewInstance:
                case Opcode.NativeDefine:
                case Opcode.DefineType:
                case Opcode.Await: case Opcode.Spawn:
                case Opcode.AsmInvoke: case Opcode.AsmInvokeI: // L9/L10 — write A (impure, never folded)
                case Opcode.EnumTagEq: case Opcode.EnumPayload: // L7 variant patterns
                case Opcode.EnumNameEq:
                case Opcode.TupleShape:
                case Opcode.StructShape: case Opcode.StructFieldGet:
                case Opcode.ListShape: case Opcode.ListElemBack: case Opcode.ListRestSlice:
                case Opcode.IsType:
                case Opcode.MapShape: case Opcode.MapHasKey: case Opcode.MapGetKey:
                case Opcode.TryUnwrap:
                // M66 II opcodes also write A (or the long shadow).
                case Opcode.LoadIntS64:
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                // M72 FF opcodes write A (Float64 shadow / boxed bool).
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                // M73 Bool opcodes write A.
                case Opcode.AndBB: case Opcode.OrBB:
                case Opcode.NotB:
                // M68 extended II/FF opcodes write A.
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI: case Opcode.NegF:
                // M80 typed Pow writes A.
                case Opcode.PowII: case Opcode.PowFF:
                // M67 Memory-SSA SymbolEntry writers — slot is
                // resolved by VisitPc via `SsaForm.DefinedSlotOf`.
                case Opcode.DeclareLocal:
                case Opcode.StoreLocalS:
                case Opcode.AssignBinding:
                case Opcode.StoreGlobal:
                case Opcode.AddIntoSlot:
                case Opcode.SubIntoSlot:
                case Opcode.AddIntoSlotImm:
                case Opcode.SubIntoSlotImm:
                    return 0; // Slot is resolved by VisitPc.
                default:
                    return null;
            }
        }

        // Bake the lattice into the public output dictionaries.
        private void Materialise()
        {
            foreach (var kv in _values)
            {
                if (kv.Value.Kind != LatKind.Const || kv.Value.Value == null) continue;
                // Find the PC that defined this (slot, version).
                foreach (var dkv in Ssa.DefVersions)
                {
                    if (dkv.Key.Slot == kv.Key.Slot && dkv.Value == kv.Key.Version)
                    {
                        ConstantValues[(dkv.Key.Pc, dkv.Key.Slot)] = kv.Value.Value;
                        break;
                    }
                }
            }
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# SCCP of {Cfg.Function.Name}");
            sb.AppendLine($"  reachable blocks: {string.Join(",", ReachableBlocks)}");
            sb.AppendLine($"  proven-constant defs: {ConstantValues.Count}");
            foreach (var kv in ConstantValues)
            {
                sb.AppendLine($"    pc={kv.Key.Pc} slot=s{kv.Key.Slot} = {kv.Value}");
            }
            sb.AppendLine($"  dead branches: {DeadBranches.Count}");
            foreach (var kv in DeadBranches)
            {
                sb.AppendLine($"    pc={kv.Key} statically taken={kv.Value}");
            }
            return sb.ToString();
        }
    }
}

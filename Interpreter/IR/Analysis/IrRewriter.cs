using System.Collections.Generic;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M64: in-place rewrite of `RaFunction.Code` driven by the
    // M54-M62 analysis pipeline. The runtime VM benefits at dispatch
    // time without paying the cost of a separate IR layer.
    //
    // All rewrites are **1:1 opcode substitutions** — each transformed
    // PC keeps the same array index in `Code`, so PC-relative branches,
    // `EhTable` entries, `PcSpansPc` source-mapping markers, and every
    // per-PC inline-cache table (`LoadGlobalIc`, `EnumAccessIc`,
    // `CastIc`, `MemberAccessIc`, `CallMethodIc`) stay valid without
    // any offset patching.
    //
    // Four transforms, applied in this order:
    //
    //   1. SCCP constant folding — replace any opcode whose def was
    //      proven a constant value with a single `LoadConst` of that
    //      value. Eliminates per-iter dispatch of the original
    //      arith/comparison; saves up to ~50 ns per replaced op.
    //
    //   2. Branch folding — `CondJump` whose condition SCCP folded
    //      becomes either an unconditional `Jmp` (always taken) or a
    //      no-op `Pass` (always falls through). Saves the per-iter
    //      condition fetch + compare.
    //
    //   3. GVN substitution — a pure op whose result GVN proved equal
    //      to a dominating canonical def becomes `Move dst, canonical_dst`.
    //      Saves the per-iter arith dispatch.
    //
    //   4. DCE — a pure op whose result no use consumes becomes
    //      `Pass`. Saves the per-iter dispatch entirely.
    //
    // Stats captured on `IrRewriter.Stats` for the `--dump-cfg`
    // diagnostic and for sanity-checking the pass in tests.
    public static class IrRewriter
    {
        public sealed class Stats
        {
            public int ConstFolded;
            public int BranchesEliminated;
            public int GvnSubstitutions;
            public int DeadOps;
            public int FusedBranches;
        }

        // Apply every rewrite phase. Mutates `fn.Code` and may append
        // to `fn.Consts`. Returns the count of each transform.
        public static Stats Apply(RaFunction fn, IrAnalysisBundle bundle)
        {
            var stats = new Stats();
            if (fn.Code.Length == 0) return stats;

            // Materialise Consts as a growable list because SCCP folding
            // synthesises new constants (intermediate `Add` results).
            // Wrap once, flush back to `fn.Consts` at the end.
            var consts = new List<RuntimeValue?>(fn.Consts);
            var constIndex = new Dictionary<RuntimeValue, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < fn.Consts.Length; i++)
            {
                var c = fn.Consts[i];
                if (c != null && !constIndex.ContainsKey(c)) constIndex[c] = i;
            }
            ushort InternConst(RuntimeValue v)
            {
                if (constIndex.TryGetValue(v, out var existing)) return (ushort)existing;
                if (consts.Count >= ushort.MaxValue) return ushort.MaxValue; // cannot intern; caller skips
                int idx = consts.Count;
                consts.Add(v);
                constIndex[v] = idx;
                return (ushort)idx;
            }

            // ---- Phase 1: SCCP constant folding ----------------------
            //
            // Per SCCP, a (pc, slot) may have a proven constant. The
            // opcode writes that slot — replace its body with
            // `LoadConst slot, idx`. Cannot replace opcodes that are
            // already constant-loads (would be a noop, but tracking
            // catches it anyway).
            foreach (var kv in bundle.Sccp.ConstantValues)
            {
                int pc = kv.Key.Pc;
                int slot = kv.Key.Slot;
                if (slot < 0 || slot > byte.MaxValue) continue;
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                if (op == Opcode.LoadConst || op == Opcode.LoadNull ||
                    op == Opcode.LoadTrue || op == Opcode.LoadFalse ||
                    op == Opcode.LoadIntS)
                {
                    // Already a load; no benefit replacing.
                    continue;
                }
                // M76: typed (II / FF / BB) opcodes write a slot whose
                // ValueSlot.Tag is Int64 / Float64 / Bool and whose
                // primitive payload lives in Bits. Rewriting to
                // `LoadConst` would set Tag=Ref and leave Bits stale,
                // breaking downstream typed readers (e.g. lazy-Range
                // ForInt* counters that index Slots[i].Bits directly,
                // TryReadAsLong / TryReadAsDouble / TryReadAsBool
                // typed-dispatch fast paths). Keep the typed opcode —
                // SCCP's lattice value still flows through to fold
                // downstream consumers; we just don't materialise the
                // const at this PC. A future tier could insert a
                // `LoadIntS64 slot, simm16` here when the const fits
                // in 16 bits and the downstream chain expects Int64,
                // but for now the typed opcode itself stays put.
                switch (op)
                {
                    case Opcode.LoadIntS64:
                    case Opcode.UnboxI: case Opcode.BoxI:
                    case Opcode.UnboxF: case Opcode.BoxF:
                    case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                    case Opcode.DivII: case Opcode.ModII:
                    case Opcode.ShlII: case Opcode.ShrII:
                    case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                    case Opcode.NegI:
                    case Opcode.PowII:
                    case Opcode.LtII: case Opcode.LeII:
                    case Opcode.GtII: case Opcode.GeII:
                    case Opcode.EqII: case Opcode.NeII:
                    case Opcode.AddFF: case Opcode.SubFF:
                    case Opcode.MulFF: case Opcode.DivFF:
                    case Opcode.NegF:
                    case Opcode.PowFF:
                    case Opcode.LtFF: case Opcode.LeFF:
                    case Opcode.GtFF: case Opcode.GeFF:
                    case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                        continue;
                }
                // Re-using LoadConst keeps the encoding shape and the
                // dispatch loop's existing fast-path. Synthesise the
                // const-pool entry (intern via reference equality;
                // NumberValue.OfBigNumber + small-int cache makes most
                // of these collide automatically).
                if (kv.Value is null) continue;
                ushort idx = InternConst(kv.Value);
                if (idx == ushort.MaxValue) continue; // pool full
                fn.Code[pc] = Encoding.Pack2(Opcode.LoadConst, (byte)slot, idx);
                stats.ConstFolded++;
            }

            // ---- Phase 2: branch folding -----------------------------
            //
            // SCCP DeadBranches[pc] = takenBool tells us the static
            // outcome. Replace the cond-jump opcode with either an
            // unconditional Jmp (same offset) or a Pass (no-op).
            foreach (var kv in bundle.Sccp.DeadBranches)
            {
                int pc = kv.Key;
                bool taken = kv.Value;
                uint instr = fn.Code[pc];
                ushort imm = Encoding.Imm16(instr);
                if (taken)
                {
                    // Unconditional jump preserves the original offset.
                    fn.Code[pc] = Encoding.Pack2(Opcode.Jmp, 0, imm);
                }
                else
                {
                    // Fall through — Pass is a 1-word no-op.
                    fn.Code[pc] = Encoding.Pack3(Opcode.Pass, 0, 0, 0);
                }
                stats.BranchesEliminated++;
            }

            // ---- Phase 3: GVN substitution ---------------------------
            //
            // For each redundant pure op, replace with
            // `Move dst, canonical_dst`. Saves the per-iter arith
            // dispatch + the operand fetches.
            // Re-enabled with canonical-slot live-range verification.
            // GVN proves the value at canonical_pc equals the value at
            // redundant_pc on every reachable path. Substituting
            // `Move dst, canonical_slot` only works if no opcode on any
            // path from canonical to redundant writes to canonical_slot
            // — the IR emitter recycles temp slots aggressively, so
            // the same slot id may carry an unrelated value at the
            // redundant pc unless we verify.
            foreach (var kv in bundle.Gvn.RedundantWithDominator)
            {
                int redundantPc = kv.Key;
                int canonicalPc = kv.Value;
                uint redundantInstr = fn.Code[redundantPc];
                var redundantOp = Encoding.DecodeOp(redundantInstr);
                if (redundantOp == Opcode.LoadConst || redundantOp == Opcode.Move ||
                    redundantOp == Opcode.Pass || redundantOp == Opcode.Jmp)
                {
                    continue; // already rewritten by an earlier phase
                }
                uint canonicalInstr = fn.Code[canonicalPc];
                var canonicalOp = Encoding.DecodeOp(canonicalInstr);
                if (canonicalOp == Opcode.Pass || canonicalOp == Opcode.Move)
                {
                    continue; // canonical may have been DCE'd / GVN'd already
                }
                byte dstSlot = Encoding.A(redundantInstr);
                byte srcSlot = Encoding.A(canonicalInstr);
                if (!IsCanonicalSlotClean(fn, bundle.Cfg, canonicalPc, redundantPc, srcSlot))
                {
                    continue; // canonical slot was overwritten somewhere on path
                }
                fn.Code[redundantPc] = Encoding.Pack3(Opcode.Move, dstSlot, srcSlot, 0);
                stats.GvnSubstitutions++;
            }

            // ---- Phase 4: DCE ----------------------------------------
            //
            // M65 re-enable. OperandReads now defaults to (B, C) reads
            // for any opcode not explicitly enumerated, so unknown
            // shapes never let DCE erase a live def. Earlier phases
            // may have re-shaped some opcodes (a SCCP-folded LoadConst
            // whose result is also dead would be DCE'd here); skip
            // anything that's not still pure.
            foreach (var pc in bundle.Opt.DeadDefPcs)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                if (op == Opcode.Pass) continue;
                if (!IsErasableForDce(op)) continue;
                fn.Code[pc] = Encoding.Pack3(Opcode.Pass, 0, 0, 0);
                stats.DeadOps++;
            }

            // ---- Phase 5: chain-aware II promotion (M66.4 + M66.5) ---
            //
            // Promote arith opcodes to their II variant only when the
            // entire def-use chain rooted at the result slot stays
            // inside the II family.
            //
            // M66.4 limitation: any phi consuming a candidate's
            // result was treated as ESCAPE, which left loop-carried
            // iter counters (slot rewritten on the back-edge by
            // AddNN, value flowing through a phi at the loop
            // header) demoted on every iter. The bench_hotloop slot
            // 7 chain was the canonical example.
            //
            // M66.5 algorithm:
            //   1. Build the candidate set: arith / cmp opcodes
            //      (M66.4 catalogue) PLUS
            //        - `LoadConst slot, const16` whose const is a
            //          NumberValue with `Scale.IsZero` and `Unscaled`
            //          fits int16. Promoted to `LoadIntS64 slot,
            //          simm16` so the const flows into `LongLocals`
            //          directly without paying the boxed
            //          `NumberValue.OfBigNumber` round-trip on every
            //          dispatch.
            //        - `LoadIntS slot, simm16`. Same shape as
            //          `LoadIntS64`; only the opcode tag differs.
            //   2. Initialise `phiPromotable` to every phi in the
            //      function. A phi is promotable iff every arg-def
            //      is a candidate (or another promotable phi) AND
            //      every use of the phi-version is a candidate (or
            //      feeds into another promotable phi).
            //   3. Symmetric fixpoint:
            //        - Demote a long-producing candidate if its
            //          result version escapes to a non-candidate PC
            //          or a non-promotable phi.
            //        - Demote a phi if any arg / use violates the
            //          coherence rule.
            //      Iterate until both sets are stable. Bounded by
            //      |cands| + |phis|; in practice converges in 2-4
            //      rounds.
            //   4. Apply: arith / cmp swaps are opcode-tag-only.
            //      `LoadConst → LoadIntS64` repacks A and replaces
            //      the const16 with the int16 immediate.
            //
            // Sets `fn.UsesUnboxedSlots = true` when at least one
            // swap survives, which gates `VmFrame.LongLocals` /
            // `LongValid` allocation.
            int iiSwaps = 0;
            {
                var ssa = bundle.Ssa;
                var cands = new Dictionary<int, Opcode>();
                var hints = fn.SlotTypeHints;

                // M72 local Float-hint inference, fused with the
                // candidate seed scan. We track a per-slot "live
                // float" bit as a forward sweep over `Code`: each
                // PC first DECIDES the FF/II promotion using the
                // CURRENT state of `localFloat`, then UPDATES the
                // state to reflect the PC's writer effect. The
                // fused order matters — a later non-float writer
                // (e.g. `LoadGlobal slot, "print"`) would otherwise
                // erase the bit before the earlier `Add` consults
                // it.
                bool[]? localFloat = (fn.LocalCount > 0) ? new bool[fn.LocalCount] : null;

                for (int pc = 0; pc < fn.Code.Length; pc++)
                {
                    uint instr = fn.Code[pc];
                    var op = Encoding.DecodeOp(instr);

                    // M72: Float64 chain preference. Arith / cmp
                    // opcodes whose B and C slots are float-hinted
                    // (either by M40's `SlotTypeHints` or by the
                    // local DoubleValue/FloatValue propagation)
                    // promote to the FF family in preference to II.
                    Opcode? promoted = null;
                    byte bSlot = Encoding.B(instr);
                    byte cSlot = Encoding.C(instr);
                    bool bFloat = (hints != null && IsFloatHinted(hints, bSlot))
                                || (localFloat != null && bSlot < localFloat.Length && localFloat[bSlot]);
                    bool cFloat = (hints != null && IsFloatHinted(hints, cSlot))
                                || (localFloat != null && cSlot < localFloat.Length && localFloat[cSlot]);
                    if (bFloat && cFloat)
                    {
                        promoted = op switch
                        {
                            Opcode.Add => Opcode.AddFF,
                            Opcode.Sub => Opcode.SubFF,
                            Opcode.Mul => Opcode.MulFF,
                            Opcode.Div => Opcode.DivFF,
                            // M80 — Float-chain Pow promotes to PowFF
                            // (Math.Pow IEEE-754 semantics).
                            Opcode.Pow => Opcode.PowFF,
                            Opcode.Lt  => Opcode.LtFF,
                            Opcode.Le  => Opcode.LeFF,
                            Opcode.Gt  => Opcode.GtFF,
                            Opcode.Ge  => Opcode.GeFF,
                            _          => null,
                        };
                    }
                    // M74: Not → NotB promotion. NotB is dual-rep
                    // (writes both `Slots[a].Tag=Bool` AND
                    // `locals[a]=BooleanValue.Of(...)`) so the
                    // promotion is unconditionally safe — every
                    // downstream consumer keeps working, while
                    // BB / Jmp{If,IfNot} / AndJz / OrJnz / NCJz
                    // readers can fast-path through `TryReadAsBool`.
                    if (promoted == null && op == Opcode.Not)
                    {
                        promoted = Opcode.NotB;
                    }
                    promoted ??= op switch
                    {
                        Opcode.Add   => Opcode.AddII,
                        Opcode.Sub   => Opcode.SubII,
                        Opcode.Mul   => Opcode.MulII,
                        Opcode.AddNN => Opcode.AddII,
                        Opcode.SubNN => Opcode.SubII,
                        Opcode.MulNN => Opcode.MulII,
                        Opcode.Lt    => Opcode.LtII,
                        Opcode.Le    => Opcode.LeII,
                        Opcode.Gt    => Opcode.GtII,
                        Opcode.Ge    => Opcode.GeII,
                        // M68 pervasive II promotion.
                        Opcode.Div   => Opcode.DivII,
                        Opcode.Mod   => Opcode.ModII,
                        Opcode.Shl   => Opcode.ShlII,
                        Opcode.Shr   => Opcode.ShrII,
                        Opcode.Ushr  => Opcode.UshrII,
                        Opcode.Rol   => Opcode.RolII,
                        Opcode.Ror   => Opcode.RorII,
                        Opcode.BAnd  => Opcode.BAndII,
                        Opcode.BOr   => Opcode.BOrII,
                        Opcode.BXor  => Opcode.BXorII,
                        Opcode.Neg   => Opcode.NegI,
                        // M80 — typed Pow. Pow→PowII fast path with
                        // deopt-on-overflow / negative-exponent. The
                        // boxed `Pow` virtual stays available via the
                        // `DeoptBinaryExtII` fallback.
                        Opcode.Pow   => Opcode.PowII,
                        // Eq / Ne intentionally EXCLUDED. The boxed
                        // `Binary` semantics for `==` / `!=` cover
                        // every RuntimeValue subtype (string/list/
                        // map/instance/null/...) via the virtual
                        // `GetComparisonEq` dispatch. The II
                        // promotion would deopt on every non-int
                        // operand pair, regressing tests that
                        // compare booleans / strings / null at
                        // an `Eq`-shaped opcode.
                        Opcode.LoadIntS => Opcode.LoadIntS64,
                        // M66.6: II opcodes the IR compiler emitted
                        // directly (lazy-Range loop scaffolding) are
                        // already in their II form. Self-promote so
                        // the chain analyzer accepts them as valid
                        // producers / consumers without rewriting.
                        Opcode.LoadIntS64 => Opcode.LoadIntS64,
                        Opcode.UnboxI     => Opcode.UnboxI,
                        Opcode.BoxI       => Opcode.BoxI,
                        Opcode.AddII      => Opcode.AddII,
                        Opcode.SubII      => Opcode.SubII,
                        Opcode.MulII      => Opcode.MulII,
                        Opcode.LtII       => Opcode.LtII,
                        Opcode.LeII       => Opcode.LeII,
                        Opcode.GtII       => Opcode.GtII,
                        Opcode.GeII       => Opcode.GeII,
                        Opcode.EqII       => Opcode.EqII,
                        Opcode.NeII       => Opcode.NeII,
                        // M72: FF self-promotion mirror.
                        Opcode.UnboxF     => Opcode.UnboxF,
                        Opcode.BoxF       => Opcode.BoxF,
                        Opcode.AddFF      => Opcode.AddFF,
                        Opcode.SubFF      => Opcode.SubFF,
                        Opcode.MulFF      => Opcode.MulFF,
                        Opcode.DivFF      => Opcode.DivFF,
                        Opcode.LtFF       => Opcode.LtFF,
                        Opcode.LeFF       => Opcode.LeFF,
                        Opcode.GtFF       => Opcode.GtFF,
                        Opcode.GeFF       => Opcode.GeFF,
                        // M74: BB self-promotion mirror.
                        Opcode.AndBB      => Opcode.AndBB,
                        Opcode.OrBB       => Opcode.OrBB,
                        Opcode.NotB       => Opcode.NotB,
                        // M68: extended II/FF self-promotion mirror.
                        Opcode.DivII      => Opcode.DivII,
                        Opcode.ModII      => Opcode.ModII,
                        Opcode.ShlII      => Opcode.ShlII,
                        Opcode.ShrII      => Opcode.ShrII,
                        Opcode.UshrII     => Opcode.UshrII,
                        Opcode.RolII      => Opcode.RolII,
                        Opcode.RorII      => Opcode.RorII,
                        Opcode.BAndII     => Opcode.BAndII,
                        Opcode.BOrII      => Opcode.BOrII,
                        Opcode.BXorII     => Opcode.BXorII,
                        Opcode.NegI       => Opcode.NegI,
                        Opcode.NegF       => Opcode.NegF,
                        // M80 — Pow self-promotion mirrors.
                        Opcode.PowII      => Opcode.PowII,
                        Opcode.PowFF      => Opcode.PowFF,
                        _            => null,
                    };
                    // M66.5: small-int LoadConst → LoadIntS64. The
                    // const-pool entry stays in place (other PCs may
                    // still reference it); only this PC switches to
                    // the immediate-encoded form.
                    if (promoted == null && op == Opcode.LoadConst)
                    {
                        int idx = Encoding.Imm16(instr);
                        if (idx < consts.Count && consts[idx] is NumberValue nv
                            && nv.Value.Scale.IsZero
                            && nv.Value.Unscaled >= short.MinValue
                            && nv.Value.Unscaled <= short.MaxValue)
                        {
                            promoted = Opcode.LoadIntS64;
                        }
                    }
                    // M72: update `localFloat` AFTER the promotion
                    // decision so the next PC sees this PC's
                    // writer effect.
                    if (localFloat != null)
                    {
                        byte aSlot = Encoding.A(instr);
                        if (aSlot < localFloat.Length)
                        {
                            switch (op)
                            {
                                case Opcode.LoadConst:
                                {
                                    int idx = Encoding.Imm16(instr);
                                    localFloat[aSlot] = idx < consts.Count
                                        && consts[idx] is RuntimeValue cv
                                        && (cv.Type == RuntimeValueType.Double
                                            || cv.Type == RuntimeValueType.Float);
                                    break;
                                }
                                case Opcode.Add: case Opcode.Sub: case Opcode.Mul: case Opcode.Div:
                                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                                    localFloat[aSlot] = bFloat || cFloat;
                                    break;
                                case Opcode.Move:
                                case Opcode.Alias:
                                case Opcode.MoveLet:
                                {
                                    byte bSrc = Encoding.B(instr);
                                    localFloat[aSlot] = bSrc < localFloat.Length && localFloat[bSrc];
                                    break;
                                }
                                case Opcode.UnboxF: localFloat[aSlot] = true; break;
                                case Opcode.BoxF:   localFloat[aSlot] = false; break;
                                default:
                                {
                                    int? defA = SsaForm.DefinedSlotOf(instr, fn);
                                    if (defA.HasValue && defA.Value >= 0 && defA.Value < localFloat.Length)
                                        localFloat[defA.Value] = false;
                                    break;
                                }
                            }
                        }
                    }

                    if (promoted == null) continue;
                    cands[pc] = promoted.Value;
                }

                // M66.5 phi-coherence: invert SSA indices so the
                // fixpoint runs in O((|cands|+|phis|) * iters) rather
                // than scanning the full UseVersions / PhiArgs dicts
                // each round.
                var versionDef = new Dictionary<(int Slot, int Version), int>();
                foreach (var kv in ssa.DefVersions)
                    versionDef[(kv.Key.Slot, kv.Value)] = kv.Key.Pc;
                var versionPhiDef = new Dictionary<(int Slot, int Version), (int Block, int Slot, int Version)>();
                foreach (var bbPhis in ssa.Phis)
                    foreach (var kv in bbPhis.Value)
                        versionPhiDef[(kv.Key, kv.Value)] = (bbPhis.Key, kv.Key, kv.Value);
                var versionUsesPc = new Dictionary<(int Slot, int Version), List<int>>();
                foreach (var kv in ssa.UseVersions)
                {
                    var key = (kv.Key.Slot, kv.Value);
                    if (!versionUsesPc.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        versionUsesPc[key] = list;
                    }
                    list.Add(kv.Key.Pc);
                }
                var versionFeedsPhis = new Dictionary<(int Slot, int Version), List<(int Block, int Slot, int Version)>>();
                foreach (var kv in ssa.PhiArgs)
                {
                    int slot = kv.Key.Slot;
                    var args = kv.Value;
                    for (int i = 0; i < args.Length; i++)
                    {
                        int argV = args[i];
                        if (argV == 0) continue;
                        var key = (slot, argV);
                        if (!versionFeedsPhis.TryGetValue(key, out var list))
                        {
                            list = new List<(int Block, int Slot, int Version)>();
                            versionFeedsPhis[key] = list;
                        }
                        list.Add((kv.Key.Block, kv.Key.Slot, kv.Key.Version));
                    }
                }

                // Seed: every phi starts promotable. Fixpoint demotes
                // those that fail coherence.
                var phiPromotable = new HashSet<(int Block, int Slot, int Version)>();
                foreach (var bbPhis in ssa.Phis)
                    foreach (var kv in bbPhis.Value)
                        phiPromotable.Add((bbPhis.Key, kv.Key, kv.Value));

                // M78 — phi family invariant. Each phi gets a tentative
                // family derived from its arg producers; mixed families
                // demote the phi up-front. The fixpoint then refines:
                // a candidate's family must match every consumer's
                // ConsumerFamilyOf, and a phi's family must match the
                // family of every use.
                var phiFamily = new Dictionary<(int Block, int Slot, int Version), ChainFamily>();
                foreach (var p in phiPromotable)
                {
                    phiFamily[p] = ChainFamily.None;
                }

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    // ---- Demote candidates whose result escapes ---
                    var candRemove = new List<int>();
                    foreach (var kv in cands)
                    {
                        int pc = kv.Key;
                        Opcode iiOp = kv.Value;
                        // Lt/Le/Gt/Ge/Eq/Ne write a boxed Boolean —
                        // their consumers read a normal RuntimeValue
                        // regardless of tag state. Terminal-safe.
                        if (IsBoxedResultIIOp(iiOp)) continue;

                        byte resultSlot = Encoding.A(fn.Code[pc]);
                        if (!ssa.DefVersions.TryGetValue((pc, resultSlot), out int version))
                        {
                            candRemove.Add(pc);
                            continue;
                        }
                        var verKey = ((int)resultSlot, version);
                        // M78: producer family — uses must agree on
                        // tag interpretation. `LoadIntS64` /
                        // arith-II produce Int64; FF arith produces
                        // Float64; BB ops produce Bool. A consumer
                        // expecting a different family would deopt at
                        // every dispatch.
                        ChainFamily prodFam = ProducerFamilyOf(iiOp);
                        bool anyUse = false;
                        bool allInChain = true;
                        if (versionUsesPc.TryGetValue(verKey, out var pcUses))
                        {
                            foreach (var usePc in pcUses)
                            {
                                anyUse = true;
                                if (!cands.TryGetValue(usePc, out var useOp)) { allInChain = false; break; }
                                uint useInstr = fn.Code[usePc];
                                byte ub = Encoding.B(useInstr);
                                byte uc = Encoding.C(useInstr);
                                if (resultSlot != ub && resultSlot != uc) { allInChain = false; break; }
                                // M78 family match — None family on
                                // either side is non-typed; treat as
                                // mismatch.
                                ChainFamily useFam = ConsumerFamilyOf(useOp);
                                if (prodFam == ChainFamily.None
                                    || useFam == ChainFamily.None
                                    || prodFam != useFam)
                                { allInChain = false; break; }
                            }
                        }
                        if (allInChain && versionFeedsPhis.TryGetValue(verKey, out var phiUses))
                        {
                            foreach (var p in phiUses)
                            {
                                anyUse = true;
                                if (!phiPromotable.Contains(p)) { allInChain = false; break; }
                                // M78 — phi family must match producer.
                                if (phiFamily.TryGetValue(p, out var pf)
                                    && pf != ChainFamily.None
                                    && pf != prodFam)
                                { allInChain = false; break; }
                            }
                        }
                        // No uses ≈ dead def — leave for DCE.
                        if (!anyUse) { candRemove.Add(pc); continue; }
                        if (!allInChain) { candRemove.Add(pc); continue; }
                    }
                    if (candRemove.Count > 0)
                    {
                        foreach (var pc in candRemove) cands.Remove(pc);
                        changed = true;
                    }

                    // ---- Demote phis whose arg/use coherence broke -
                    var phiRemove = new List<(int Block, int Slot, int Version)>();
                    foreach (var p in phiPromotable)
                    {
                        bool ok = true;
                        // M78 — derive expected family from first
                        // arg producer. All other args + uses must
                        // agree.
                        ChainFamily expected = ChainFamily.None;
                        // Every arg-def must be a long producer.
                        if (ssa.PhiArgs.TryGetValue(p, out var args))
                        {
                            for (int i = 0; i < args.Length; i++)
                            {
                                int argV = args[i];
                                if (argV == 0) { ok = false; break; } // uninitialised
                                var argKey = (p.Slot, argV);
                                ChainFamily argFam = ChainFamily.None;
                                if (versionDef.TryGetValue(argKey, out int prodPc))
                                {
                                    if (!cands.TryGetValue(prodPc, out var prodOp)
                                        || !IsLongProducerIIOp(prodOp))
                                    { ok = false; break; }
                                    argFam = ProducerFamilyOf(prodOp);
                                }
                                else if (versionPhiDef.TryGetValue(argKey, out var phiSrc))
                                {
                                    if (!phiPromotable.Contains(phiSrc)) { ok = false; break; }
                                    if (!phiFamily.TryGetValue(phiSrc, out argFam))
                                        argFam = ChainFamily.None;
                                }
                                else { ok = false; break; } // unknown def

                                if (argFam == ChainFamily.None)
                                {
                                    // Predecessor phi hasn't yet
                                    // settled its family — defer
                                    // (next fixpoint round will
                                    // re-evaluate).
                                    continue;
                                }
                                if (expected == ChainFamily.None) expected = argFam;
                                else if (expected != argFam) { ok = false; break; }
                            }
                        }
                        else
                        {
                            // No PhiArgs entry — happens when a phi
                            // has zero predecessor versions recorded
                            // (degenerate; treat as unpromotable).
                            ok = false;
                        }
                        if (!ok) { phiRemove.Add(p); continue; }

                        // Every use of phi-version must stay in chain.
                        var useKey = (p.Slot, p.Version);
                        if (versionUsesPc.TryGetValue(useKey, out var pcUses))
                        {
                            foreach (var usePc in pcUses)
                            {
                                if (!cands.TryGetValue(usePc, out var useOp)) { ok = false; break; }
                                uint useInstr = fn.Code[usePc];
                                byte ub = Encoding.B(useInstr);
                                byte uc = Encoding.C(useInstr);
                                if (p.Slot != ub && p.Slot != uc) { ok = false; break; }
                                // M78 — consumer family must match
                                // the phi's expected family.
                                if (expected != ChainFamily.None)
                                {
                                    var useFam = ConsumerFamilyOf(useOp);
                                    if (useFam == ChainFamily.None
                                        || useFam != expected)
                                    { ok = false; break; }
                                }
                            }
                        }
                        if (ok && versionFeedsPhis.TryGetValue(useKey, out var phiUses))
                        {
                            foreach (var q in phiUses)
                            {
                                if (!phiPromotable.Contains(q)) { ok = false; break; }
                                if (expected != ChainFamily.None
                                    && phiFamily.TryGetValue(q, out var qf)
                                    && qf != ChainFamily.None
                                    && qf != expected)
                                { ok = false; break; }
                            }
                        }
                        if (!ok) phiRemove.Add(p);
                        else
                        {
                            // Record latched family for downstream
                            // checks in subsequent rounds.
                            if (expected != ChainFamily.None)
                            {
                                if (!phiFamily.TryGetValue(p, out var prev) || prev != expected)
                                {
                                    phiFamily[p] = expected;
                                    changed = true; // family transition
                                }
                            }
                        }
                    }
                    if (phiRemove.Count > 0)
                    {
                        foreach (var p in phiRemove) phiPromotable.Remove(p);
                        changed = true;
                    }
                }

                // ---- Apply the surviving promotions -----------------
                foreach (var kv in cands)
                {
                    int pc = kv.Key;
                    Opcode iiOp = kv.Value;
                    uint instr = fn.Code[pc];
                    var origOp = Encoding.DecodeOp(instr);
                    if (origOp == Opcode.LoadConst && iiOp == Opcode.LoadIntS64)
                    {
                        // Repack: keep A, replace const16 with the
                        // int16 immediate read from the pool entry.
                        byte slot = Encoding.A(instr);
                        int idx = Encoding.Imm16(instr);
                        var nv = (NumberValue)consts[idx]!;
                        short smallInt = (short)(long)nv.Value.Unscaled;
                        fn.Code[pc] = Encoding.Pack2(Opcode.LoadIntS64, slot, unchecked((ushort)smallInt));
                    }
                    else if (origOp != iiOp)
                    {
                        // Same A/B/C (or A/imm16 for LoadIntS) shape
                        // — opcode-tag substitution preserves the
                        // operand layout byte-for-byte.
                        fn.Code[pc] = (instr & 0xFFFFFF00u) | (uint)iiOp;
                    }
                    iiSwaps++;
                }
            }
            if (iiSwaps > 0) fn.UsesUnboxedSlots = true;

            // NOTE: fused compare-and-branch (M90) is NOT done here. It must
            // run as the LAST code transform — after LICM, which physically
            // reshuffles Code[] and patches only the standard imm16 branch
            // opcodes (Jmp/JmpIf/JmpIfNot), not the fused ops' sbyte-encoded
            // offset. Running it here (pre-LICM) would let LICM invalidate the
            // baked offsets. See `FuseCompareBranches`, called from
            // IrCompiler.FinalizeFn after LICM with a fresh bundle.

            // Flush mutated const pool back to the RaFunction.
            if (consts.Count != fn.Consts.Length)
            {
                fn.Consts = consts.ToArray();
            }
            return stats;
        }

        // M90 fused compare-and-branch. Runs as the FINAL code transform
        // (from IrCompiler.FinalizeFn, after LICM) so the offsets it bakes
        // into the fused ops match the layout that actually executes —
        // LICM moves instructions and patches only imm16-encoded branches,
        // never the fused ops' sbyte offset, so this MUST come after it.
        //
        // Fuses `cmpII@n ; JmpIfNot@n+1` (the loop-test pattern; LtII +
        // JmpIfNot alone are ~37% of dispatched opcodes) into a single
        // `JmpNot{Cmp}II@n`, turning n+1 into Pass. One dispatch replaces
        // two, and the cmp's dual-rep slot write + the JmpIfNot's slot read
        // both vanish.
        //
        // Builds its own fresh CFG+SSA bundle on the final layout. Safety
        // (all required):
        //   * opcode[n] is a typed II comparison; opcode[n+1] is a JmpIfNot
        //     reading the exact slot cmpII wrote;
        //   * n and n+1 sit in the SAME basic block — so n+1 is reachable
        //     only by fall-through from n (no foreign predecessor loses its
        //     branch when n+1 becomes Pass);
        //   * the cmp slot's SSA def at n has its ONLY use at n+1 and feeds
        //     no phi — so dropping the slot write is safe;
        //   * the fused offset (origOffset + 1, since the fused op sits one
        //     PC before the JmpIfNot it absorbs) fits signed-8.
        // Returns the number of fused pairs.
        public static int FuseCompareBranches(RaFunction fn)
        {
            if (fn == null || fn.Code == null || fn.Code.Length < 2) return 0;

            // Cheap pre-scan: skip the bundle build entirely when no
            // candidate cmpII-then-JmpIfNot adjacency exists (most
            // non-loop functions).
            bool anyCandidate = false;
            for (int n = 0; n + 1 < fn.Code.Length; n++)
            {
                if (IsFusibleCmpII(Encoding.DecodeOp(fn.Code[n]))
                    && Encoding.DecodeOp(fn.Code[n + 1]) == Opcode.JmpIfNot)
                { anyCandidate = true; break; }
            }
            if (!anyCandidate) return 0;

            IrAnalysisBundle bundle;
            try { bundle = IrAnalysisBundle.Build(fn); }
            catch { return 0; }
            if (bundle.Ssa == null || bundle.Cfg == null) return 0;
            var ssa = bundle.Ssa;
            var cfg = bundle.Cfg;

            var usesByVer = new Dictionary<(int Slot, int Ver), List<int>>();
            foreach (var u in ssa.UseVersions)
            {
                var key = (u.Key.Slot, u.Value);
                if (!usesByVer.TryGetValue(key, out var l)) { l = new List<int>(); usesByVer[key] = l; }
                l.Add(u.Key.Pc);
            }
            var feedsPhi = new HashSet<(int Slot, int Ver)>();
            foreach (var kv in ssa.PhiArgs)
            {
                int slot = kv.Key.Slot;
                foreach (int argV in kv.Value)
                    if (argV != 0) feedsPhi.Add((slot, argV));
            }

            int fusedCount = 0;
            for (int n = 0; n + 1 < fn.Code.Length; n++)
            {
                Opcode fusedOp;
                switch (Encoding.DecodeOp(fn.Code[n]))
                {
                    case Opcode.LtII: fusedOp = Opcode.JmpNotLtII; break;
                    case Opcode.LeII: fusedOp = Opcode.JmpNotLeII; break;
                    case Opcode.GtII: fusedOp = Opcode.JmpNotGtII; break;
                    case Opcode.GeII: fusedOp = Opcode.JmpNotGeII; break;
                    case Opcode.EqII: fusedOp = Opcode.JmpNotEqII; break;
                    case Opcode.NeII: fusedOp = Opcode.JmpNotNeII; break;
                    default: continue;
                }
                uint jmpInstr = fn.Code[n + 1];
                if (Encoding.DecodeOp(jmpInstr) != Opcode.JmpIfNot) continue;
                int cmpSlot = Encoding.A(fn.Code[n]);
                if (Encoding.A(jmpInstr) != cmpSlot) continue;
                if (n >= cfg.PcToBlock.Length || (n + 1) >= cfg.PcToBlock.Length) continue;
                if (cfg.PcToBlock[n] != cfg.PcToBlock[n + 1]) continue;
                if (!ssa.DefVersions.TryGetValue((n, cmpSlot), out int ver)) continue;
                if (feedsPhi.Contains((cmpSlot, ver))) continue;
                if (!usesByVer.TryGetValue((cmpSlot, ver), out var uses)) continue;
                if (uses.Count != 1 || uses[0] != n + 1) continue;
                int newOff = Encoding.SImm16(jmpInstr) + 1;
                if (newOff < sbyte.MinValue || newOff > sbyte.MaxValue) continue;
                byte lhs = Encoding.B(fn.Code[n]);
                byte rhs = Encoding.C(fn.Code[n]);
                fn.Code[n] = Encoding.Pack3(fusedOp, lhs, rhs, (byte)(sbyte)newOff);
                fn.Code[n + 1] = Encoding.Pack3(Opcode.Pass, 0, 0, 0);
                fusedCount++;
                n++; // skip the consumed Pass
            }
            return fusedCount;
        }

        private static bool IsFusibleCmpII(Opcode op) =>
            op == Opcode.LtII || op == Opcode.LeII || op == Opcode.GtII
            || op == Opcode.GeII || op == Opcode.EqII || op == Opcode.NeII;

        // M66.2: SlotTypeHints query that treats only the inferred
        // `Number` type as a green light for II promotion. Other types
        // — Integer, Long, Float, Double, etc. — keep their existing
        // boxed paths (the typed primitives have their own widening /
        // overflow semantics that the int64 path does not honour).
        private static bool IsHintedNumber(Values.RuntimeValueType[] hints, byte slot)
        {
            if (slot >= hints.Length) return false;
            return hints[slot] == Values.RuntimeValueType.Number;
        }

        // M72: SlotTypeHints query for Float64 chain promotion. Both
        // `Float` and `Double` typed primitives ride the FF path —
        // `FloatValue.Value` is widened to double inside
        // `TryReadAsDouble`, so the runtime semantics stay
        // identical to the boxed virtual-dispatch path.
        private static bool IsFloatHinted(Values.RuntimeValueType[] hints, byte slot)
        {
            if (slot >= hints.Length) return false;
            var t = hints[slot];
            return t == Values.RuntimeValueType.Double || t == Values.RuntimeValueType.Float;
        }

        // M66.5 / M66.6 helpers: classify II opcodes by what they
        // leave in `LongLocals[a]` / `locals[a]`.
        //
        //   IsBoxedResultIIOp: comparisons (Lt/Le/Gt/Ge/Eq/Ne) + the
        //   `BoxI` long→boxed bridge. They read operands via
        //   `TryReadAsLong` but write a boxed `RuntimeValue` to
        //   `locals[a]` and clear `LongValid[a]`. Their consumers
        //   read a normal RuntimeValue regardless of upstream tag
        //   state — terminal for the long chain.
        //
        //   IsLongProducerIIOp: arith (Add/Sub/Mul) + LoadIntS64 +
        //   the `UnboxI` boxed→long bridge. They populate
        //   `LongLocals[a]` with `LongValid[a] = true`. A phi merging
        //   two long producers stays coherent only when EVERY arg-def
        //   is a long producer, so phi-promotion checks against this
        //   predicate.
        private static bool IsBoxedResultIIOp(Opcode op) =>
            op == Opcode.LtII || op == Opcode.LeII
            || op == Opcode.GtII || op == Opcode.GeII
            || op == Opcode.EqII || op == Opcode.NeII
            || op == Opcode.BoxI
            // M72 FF terminals.
            || op == Opcode.LtFF || op == Opcode.LeFF
            || op == Opcode.GtFF || op == Opcode.GeFF
            || op == Opcode.BoxF
            // M74 BB terminals — dual-rep (Bool tag + boxed
            // BooleanValue mirror in locals[a]), so downstream
            // boxed readers see a real RuntimeValue and the
            // chain analyzer does NOT need to verify
            // bool-aware consumers.
            || op == Opcode.AndBB || op == Opcode.OrBB
            || op == Opcode.NotB;
        // M73 Bool-producing operations. Distinct from
        // IsBoxedResultIIOp because BB writers leave the slot
        // tagged `Bool` (Bits & 1), not `Ref`. Listed here so
        // chain analysis can treat them as their own family —
        // promoting `AndBB(b, NotB(c))` etc. without going
        // through the boxed BooleanValue path.
        private static bool IsBoolProducerOp(Opcode op) =>
            op == Opcode.AndBB || op == Opcode.OrBB
            || op == Opcode.NotB;

        private static bool IsLongProducerIIOp(Opcode op) =>
            op == Opcode.AddII || op == Opcode.SubII || op == Opcode.MulII
            || op == Opcode.LoadIntS64 || op == Opcode.UnboxI
            // M72 FF long-like producers (Float64 payload via Bits).
            || op == Opcode.AddFF || op == Opcode.SubFF
            || op == Opcode.MulFF || op == Opcode.DivFF
            || op == Opcode.UnboxF
            // M68 extended II/FF long-like producers.
            || op == Opcode.DivII || op == Opcode.ModII
            || op == Opcode.ShlII || op == Opcode.ShrII
            || op == Opcode.UshrII || op == Opcode.RolII || op == Opcode.RorII
            || op == Opcode.BAndII || op == Opcode.BOrII || op == Opcode.BXorII
            || op == Opcode.NegI || op == Opcode.NegF
            // M80 — typed Pow.
            || op == Opcode.PowII || op == Opcode.PowFF;

        // M78 — chain-family classification. Splits the M66.5 "long
        // producer" pool into the actual typed-tag families so the
        // chain analyzer can reject mixed-family merges (e.g. a phi
        // taking `AddII` on one edge and `AddFF` on the other).
        //
        // Without this split, the analyzer treats Int64 and Float64
        // producers as interchangeable — runtime stays correct only
        // because `TryReadAsLong` / `TryReadAsDouble` deopt to boxed
        // dispatch on a tag mismatch, but every miss pays a
        // BigNumber / DoubleValue round-trip. The family invariant
        // prevents the analyzer from speculatively typing a phi
        // whose paths cannot agree on the slot's tag at runtime.
        //
        // BoxI / BoxF / comparisons are deliberately classified by
        // their OPERAND family (the tag they read) — their result
        // is boxed and exits the long chain (`IsBoxedResultIIOp`
        // short-circuits the consumer walk at line 532). This lets
        // chain promotion stay coherent at terminals: an `LtII`
        // sitting on the consumer side of an `AddFF`-fed phi is
        // demoted because the phi's family disagrees with `LtII`'s
        // II consumer requirement.
        internal enum ChainFamily { None, II, FF, BB }

        internal static ChainFamily ProducerFamilyOf(Opcode op)
        {
            switch (op)
            {
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI:
                case Opcode.PowII:
                case Opcode.LoadIntS64: case Opcode.UnboxI:
                    return ChainFamily.II;
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.NegF: case Opcode.UnboxF:
                case Opcode.PowFF:
                    return ChainFamily.FF;
                case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                    return ChainFamily.BB;
                default:
                    return ChainFamily.None;
            }
        }

        // Consumer family — the typed tag that the opcode READS from
        // its B / C operand slots. Comparisons + BoxI / BoxF land
        // here because their result is terminal (boxed) but their
        // operands MUST come from a matching-family producer.
        internal static ChainFamily ConsumerFamilyOf(Opcode op)
        {
            switch (op)
            {
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI:
                case Opcode.PowII:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                case Opcode.BoxI:
                    return ChainFamily.II;
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.NegF:
                case Opcode.PowFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                case Opcode.BoxF:
                    return ChainFamily.FF;
                case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                    return ChainFamily.BB;
                default:
                    return ChainFamily.None;
            }
        }

        // DCE-erasable opcodes: pure, no observable side effect, no
        // exception path. Identical lists to SsaOptimizer.IsPureForDce
        // — duplicated here so the rewriter doesn't depend on
        // SsaOptimizer's private surface.
        private static bool IsErasableForDce(Opcode op)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.Neg: case Opcode.Not: case Opcode.BNot:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                case Opcode.Move:
                case Opcode.Alias:
                    return true;
                default:
                    return false;
            }
        }

        // M65: canonical-slot live-range check. Walk every reachable
        // path from `canonicalPc + 1` to `redundantPc` through the
        // CFG; if any opcode on any of those paths writes to
        // `canonicalSlot`, the slot is dirty and we must NOT
        // substitute. Bounded BFS over CFG blocks — each block at most
        // once. Conservative: any write to slot (regardless of whether
        // the writer's SSA def kills the canonical's SSA version)
        // means the locals[canonicalSlot] cell holds a different
        // value at redundantPc than at canonicalPc.
        private static bool IsCanonicalSlotClean(
            RaFunction fn,
            ControlFlowGraph cfg,
            int canonicalPc,
            int redundantPc,
            byte canonicalSlot)
        {
            int canonicalBlock = cfg.PcToBlock[canonicalPc];
            int redundantBlock = cfg.PcToBlock[redundantPc];
            if (canonicalBlock < 0 || redundantBlock < 0) return false;

            // Within-block fast path.
            if (canonicalBlock == redundantBlock)
            {
                for (int pc = canonicalPc + 1; pc < redundantPc; pc++)
                {
                    if (WritesToSlot(fn.Code[pc], canonicalSlot)) return false;
                }
                return true;
            }

            // Multi-block path: BFS from canonical block's tail
            // through CFG successors, stopping at redundant block's
            // head. Visit each block at most once. Within each block,
            // scan the relevant PC range (full body except for the
            // canonical block, which starts from canonicalPc+1, and
            // the redundant block, which ends at redundantPc).
            var visited = new HashSet<int> { canonicalBlock };
            // Scan tail of canonical block.
            for (int pc = canonicalPc + 1; pc < cfg.Blocks[canonicalBlock].EndPcExclusive; pc++)
            {
                if (WritesToSlot(fn.Code[pc], canonicalSlot)) return false;
            }
            var queue = new Queue<int>();
            foreach (var s in cfg.Blocks[canonicalBlock].Successors)
            {
                if (visited.Add(s)) queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                int b = queue.Dequeue();
                var bb = cfg.Blocks[b];
                int endPc = (b == redundantBlock) ? redundantPc : bb.EndPcExclusive;
                for (int pc = bb.StartPc; pc < endPc; pc++)
                {
                    if (WritesToSlot(fn.Code[pc], canonicalSlot)) return false;
                }
                if (b == redundantBlock) continue; // don't descend past redundant
                foreach (var s in bb.Successors)
                {
                    if (visited.Add(s)) queue.Enqueue(s);
                }
            }
            return true;
        }

        // Does the opcode write to `locals[slot]`? Mirrors
        // `SsaForm.DefinedSlot` — opcodes whose A operand is a write
        // target return true when A == slot.
        private static bool WritesToSlot(uint instr, byte slot)
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
                case Opcode.NullCoal:
                case Opcode.StrConcat: case Opcode.Interp: case Opcode.Fmt:
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
                case Opcode.With:
                case Opcode.NativeDefine:
                case Opcode.DefineType:
                case Opcode.Await: case Opcode.Spawn:
                case Opcode.AsmInvoke: case Opcode.AsmInvokeI: // L9/L10 — write A (impure; not DCE-erasable)
                case Opcode.AnnotationApply:
                case Opcode.EnumTagEq: case Opcode.EnumPayload: // L7 variant patterns
                case Opcode.EnumNameEq:
                case Opcode.TupleShape:
                case Opcode.StructShape: case Opcode.StructFieldGet:
                case Opcode.ListShape: case Opcode.ListElemBack: case Opcode.ListRestSlice:
                case Opcode.IsType:
                case Opcode.MapShape: case Opcode.MapHasKey: case Opcode.MapGetKey:
                case Opcode.TryUnwrap:
                // M87 — extend WritesToSlot to include the entire typed
                // tagged-union family. Without these, GVN's
                // `IsCanonicalSlotClean` walked through a typed write
                // to the canonical slot without flagging it, and
                // emitted a stale `Move` from a slot whose value had
                // since been overwritten. The bug is invisible at
                // function scope (typed ops live inside a loop's
                // scope, distinct slot ranges) but surfaces at top
                // level when the M87 generalised typed-Int64 redirect
                // emits typed ops in sibling statements that share
                // temp slot indices.
                case Opcode.LoadIntS64:
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.UnboxF:
                case Opcode.BoxF:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.LtII:  case Opcode.LeII:
                case Opcode.GtII:  case Opcode.GeII:
                case Opcode.EqII:  case Opcode.NeII:
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF:  case Opcode.LeFF:
                case Opcode.GtFF:  case Opcode.GeFF:
                case Opcode.AndBB: case Opcode.OrBB:
                case Opcode.NotB:
                case Opcode.NegI:  case Opcode.NegF:
                case Opcode.PowII: case Opcode.PowFF:
                    return Encoding.A(instr) == slot;
                default:
                    return false;
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<RuntimeValue>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public bool Equals(RuntimeValue? x, RuntimeValue? y) => ReferenceEquals(x, y);
            public int GetHashCode(RuntimeValue obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

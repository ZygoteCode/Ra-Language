using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M61: natural-loop detection + loop-invariant code motion analysis.
    //
    // A "natural loop" is the connected set of blocks dominated by a
    // loop header `H` and reachable from a back-edge `(latch → H)` —
    // where `latch`'s successor includes `H` and `H` dominates `latch`.
    // The detection algorithm (Aho-Sethi-Ullman, Dragon Book §10.4):
    //
    //   1. Iterate every CFG edge `(b → s)`. If `s` dominates `b`,
    //      `(b → s)` is a back-edge; `s` is a loop header; `b` is the
    //      latch.
    //   2. Loop body = `{s} ∪ {x | x reaches b without leaving the set
    //      of blocks dominated by s}`. Computed via reverse BFS from
    //      `b` over the predecessor edges, stopping at `s`.
    //
    // Once loops are known, LICM identifies invariant computations:
    // a pure opcode is loop-invariant when every operand it reads is
    // defined OUTSIDE the loop body (including by a phi at the loop
    // header — phi defs ARE the loop-carried path so they break
    // invariance). Hoistable PCs land in `HoistableOps`; a future
    // codegen / IR-rewrite pass moves them into the loop's pre-header
    // block (the unique predecessor of the header that's NOT the
    // latch).
    public sealed class LoopAnalysis
    {
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;
        public readonly SsaForm Ssa;

        // header block id → loop info.
        public readonly Dictionary<int, NaturalLoop> Loops = new();
        // pc → header block id; only entries that are flagged hoistable.
        public readonly Dictionary<int, int> HoistableOps = new();

        public sealed class NaturalLoop
        {
            public int HeaderId;
            // All blocks belonging to this loop body, including the header.
            public readonly HashSet<int> Body = new();
            // Back-edges (latch → header). Multiple back-edges to the
            // same header form a single natural loop union.
            public readonly List<int> Latches = new();
        }

        private LoopAnalysis(SsaForm ssa) { Cfg = ssa.Cfg; Dom = ssa.Dom; Ssa = ssa; }

        public static LoopAnalysis Run(SsaForm ssa)
        {
            var la = new LoopAnalysis(ssa);
            la.FindLoops();
            la.FindInvariants();
            return la;
        }

        // Edge-iteration based back-edge detection. `s dominates b` iff
        // `s` appears on the IDom-chain of `b` (or equals `b` itself —
        // covers single-block self-loops).
        private void FindLoops()
        {
            int n = Cfg.Blocks.Count;
            for (int b = 0; b < n; b++)
            {
                foreach (var s in Cfg.Blocks[b].Successors)
                {
                    if (!DominatesOrEquals(s, b)) continue;
                    if (!Loops.TryGetValue(s, out var loop))
                    {
                        loop = new NaturalLoop { HeaderId = s };
                        loop.Body.Add(s);
                        Loops[s] = loop;
                    }
                    loop.Latches.Add(b);
                    // Body discovery: reverse BFS from `b` over preds,
                    // stopping at `s`. Anything reachable that way is
                    // inside the loop.
                    var stack = new Stack<int>();
                    if (b != s)
                    {
                        stack.Push(b);
                        loop.Body.Add(b);
                    }
                    while (stack.Count > 0)
                    {
                        int x = stack.Pop();
                        foreach (var p in Cfg.Blocks[x].Predecessors)
                        {
                            if (loop.Body.Add(p) && p != s) stack.Push(p);
                        }
                    }
                }
            }
        }

        private bool DominatesOrEquals(int a, int b)
        {
            // a dominates b iff a == b OR a is on b's IDom chain.
            int cur = b;
            while (cur != -1)
            {
                if (cur == a) return true;
                cur = Dom.IDom[cur];
            }
            return false;
        }

        // For each loop, scan its body's opcodes. A pure opcode is
        // invariant iff every operand SSA def lives in a block that
        // either:
        //   * Is OUTSIDE the loop body, OR
        //   * Is a constant immediate (LoadConst with no SSA operands).
        //
        // Phi defs at the loop header are explicitly NOT invariant
        // (they are the loop-carried state).
        private void FindInvariants()
        {
            foreach (var loop in Loops.Values)
            {
                foreach (var b in loop.Body)
                {
                    var bb = Cfg.Blocks[b];
                    for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                    {
                        uint instr = Cfg.Function.Code[pc];
                        var op = Encoding.DecodeOp(instr);
                        if (!IsPureForLicm(op)) continue;
                        if (!AreOperandsLoopInvariant(pc, instr, op, loop)) continue;
                        HoistableOps[pc] = loop.HeaderId;
                    }
                }
            }
        }

        private bool AreOperandsLoopInvariant(int pc, uint instr, Opcode op, NaturalLoop loop)
        {
            // Walk every (slot, version) the opcode reads. If the
            // defining PC of that version is inside the loop AND the
            // def is not a constant-shaped opcode that we've already
            // certified hoistable, the current op is not invariant.
            foreach (var (slot, isUse) in SsaForm_OperandReads(op, instr))
            {
                if (!isUse) continue;
                if (!Ssa.UseVersions.TryGetValue((pc, slot), out var version)) continue;
                // Locate the def. Scan DefVersions for matching (slot, version).
                // For low cardinality of defs in typical functions
                // this linear scan is acceptable; could be a Dict-of-Dict
                // index if it shows in profiles.
                bool defOutsideLoop = false;
                foreach (var dkv in Ssa.DefVersions)
                {
                    if (dkv.Key.Slot != slot || dkv.Value != version) continue;
                    int defPc = dkv.Key.Pc;
                    int defBlock = Cfg.PcToBlock[defPc];
                    if (!loop.Body.Contains(defBlock)) defOutsideLoop = true;
                    break;
                }
                // Phi def at the header is loop-carried — treat as
                // inside the loop.
                if (!defOutsideLoop)
                {
                    // Check whether the version comes from a phi at the
                    // header. If so, not invariant. Otherwise: defined
                    // inside the loop body → not invariant.
                    return false;
                }
            }
            return true;
        }

        // Bridge into SsaForm.OperandReads. Delegates directly so II/FF/BB
        // (and any future typed opcodes added to SSA tracking) get the
        // correct operand classification without a parallel switch
        // statement that can fall out of sync.
        private static IEnumerable<(int Slot, bool IsUse)> SsaForm_OperandReads(Opcode op, uint instr)
            => SsaForm.OperandReads(op, instr);

        // M-tier1 (post-M75): II / FF / BB typed-opcode families joined
        // the pure set so loop-invariant typed chains can hoist out
        // of headers. `UnboxI x` in a loop whose `x` slot is loop-
        // invariant was previously stuck in the body even though its
        // result never changed across iterations — adding it here
        // matches the boxed `Add` / `Sub` / `Mul` pure classification.
        //
        // DivII / ModII excluded for the same reason boxed Div / Mod
        // are: deopt raises a RuntimeError on division-by-zero and
        // signed-overflow, so the error site must stay observable at
        // the original PC. DivFF is included — IEEE-754 division
        // never throws.
        private static bool IsPureForLicm(Opcode op)
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
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.Neg: case Opcode.Not: case Opcode.BNot:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                // II family — Int64-typed (deopt-on-overflow boxes
                // internally, no externally observable error).
                case Opcode.LoadIntS64:
                case Opcode.UnboxI: case Opcode.BoxI:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                // FF family — Float64-typed (IEEE-754, deterministic).
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.NegF:
                case Opcode.PowFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                // BB family — Bool-typed.
                case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                    return true;
                default:
                    return false;
            }
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Loop analysis of {Cfg.Function.Name}");
            sb.AppendLine($"  loops: {Loops.Count}");
            foreach (var kv in Loops)
            {
                var loop = kv.Value;
                sb.AppendLine($"    header=BB{loop.HeaderId} body={{{string.Join(",", loop.Body)}}} latches=[{string.Join(",", loop.Latches)}]");
            }
            sb.AppendLine($"  hoistable ops: {HoistableOps.Count}");
            foreach (var kv in HoistableOps)
            {
                var op = Encoding.DecodeOp(Cfg.Function.Code[kv.Key]);
                sb.AppendLine($"    pc={kv.Key} ({op}) → out of loop header BB{kv.Value}");
            }
            return sb.ToString();
        }
    }
}

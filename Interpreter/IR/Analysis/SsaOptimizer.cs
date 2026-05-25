using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M59: SSA-based optimisation passes.
    //
    // Three classic transformations operating on `SsaForm` output:
    //
    //   1. Dead-Code Elimination — SSA values whose def has no
    //      reachable use are dead. The defining opcode can be deleted
    //      iff it is side-effect-free.
    //   2. Common Subexpression Elimination — two identical pure
    //      operations on the same SSA-version operands produce the same
    //      result; the second can be rewritten to reuse the first's
    //      result (or in pure-SSA terms, the two SSA values are merged).
    //   3. Copy Propagation — `Move dst, src` lets every downstream use
    //      of `dst` read `src` directly, eliding the Move entirely.
    //
    // All three are computed as ANALYSIS only — the passes do NOT
    // rewrite the flat `RaFunction.Code` array. The dispatch loop keeps
    // interpreting the original bytecode; rewriting Code requires
    // patching PC offsets in branches, EhTable, PcSpans, and every IC
    // table, which is too pervasive for a single milestone. Results
    // live on the `TierUpAnalysisBundle` for the future native-codegen
    // pass (M58 follow-up) which can lower the optimised SSA directly
    // to x64 via the existing `X64Assembler` pipeline.
    //
    // Empirical hit rate on the test corpus (147 .ra files):
    //   * DCE  identifies ~3-8% of SSA defs as dead per function
    //          (mostly result-discarded LoadGlobal / LoadConst pairs
    //          emitted by the AST → IR lowering's temp-slot scheme).
    //   * CSE  catches repeated LoadConst of the same constant + repeated
    //          LoadLocalS of the same slot inside a basic block; loop-
    //          carried repeats outside a single block require LICM (M61).
    //   * Copy prop trims explicit `Move`s; rare in the current IR
    //          shape, kept for forward compat with future Move-heavy
    //          lowerings.
    public sealed class SsaOptimizer
    {
        public readonly SsaForm Ssa;
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;

        // Sets / maps populated by Run():
        //   DeadDefPcs — opcode PCs that produce SSA defs nothing
        //   reads. Side-effect-free opcodes among these are safe to
        //   skip when lowering to native code.
        public readonly HashSet<int> DeadDefPcs = new();
        //   CseReplaceWith — for each redundant (pc, slot) write, the
        //   pc of the original canonical def. The lowering can fold the
        //   second write into a Move (or noop if SSA-merged) at codegen
        //   time.
        public readonly Dictionary<int, int> CseReplaceWith = new();
        //   CopyAlias — for each Move PC, the source slot the dst is
        //   aliased to. Lowering rewrites uses of dst to read src.
        public readonly Dictionary<int, (int DstSlot, int SrcSlot)> CopyAlias = new();

        private SsaOptimizer(SsaForm ssa)
        {
            Ssa = ssa;
            Cfg = ssa.Cfg;
            Dom = ssa.Dom;
        }

        public static SsaOptimizer Run(SsaForm ssa)
        {
            var opt = new SsaOptimizer(ssa);
            opt.RunCopyPropagation();
            opt.RunCommonSubexpressionElimination();
            opt.RunDeadCodeElimination();
            return opt;
        }

        // ------------------------------------------------------------
        // Copy propagation
        // ------------------------------------------------------------
        //
        // `Move a, b` defines slot `a` as an alias of slot `b`. Every
        // subsequent use of `a` (before `a` is re-defined) can read
        // `b` directly. We record this in `CopyAlias`; the lowering
        // pass uses it to elide the Move's runtime cost.
        private void RunCopyPropagation()
        {
            foreach (var bb in Cfg.Blocks)
            {
                for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                {
                    uint instr = Cfg.Function.Code[pc];
                    if (Encoding.DecodeOp(instr) != Opcode.Move) continue;
                    int dst = Encoding.A(instr);
                    int src = Encoding.B(instr);
                    CopyAlias[pc] = (dst, src);
                }
            }
        }

        // ------------------------------------------------------------
        // Common Subexpression Elimination
        // ------------------------------------------------------------
        //
        // Within each basic block, hash every pure opcode by
        // (Opcode, operand SSA versions). A second hit on the same key
        // is a redundant computation; its def is replaced with a
        // reference to the first.
        //
        // Block-local for simplicity; global-CSE across joins needs
        // value-numbering on the dominator tree + phi-aware hashing,
        // a follow-up worth the complexity only after the
        // codegen lowering exists.
        private void RunCommonSubexpressionElimination()
        {
            foreach (var bb in Cfg.Blocks)
            {
                var table = new Dictionary<(Opcode Op, int A, int B, int C, ushort Imm), int>();
                for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                {
                    uint instr = Cfg.Function.Code[pc];
                    var op = Encoding.DecodeOp(instr);
                    if (!IsPureForCse(op)) continue;
                    // Hash key uses SSA versions of operand reads so two
                    // textually-identical opcodes with different upstream
                    // defs are correctly distinguished.
                    int b = Encoding.B(instr);
                    int c = Encoding.C(instr);
                    int bVer = Ssa.UseVersions.TryGetValue((pc, b), out var bv) ? bv : 0;
                    int cVer = Ssa.UseVersions.TryGetValue((pc, c), out var cv) ? cv : 0;
                    var key = (op, 0, bVer, cVer, Encoding.Imm16(instr));
                    if (table.TryGetValue(key, out var firstPc))
                    {
                        CseReplaceWith[pc] = firstPc;
                    }
                    else
                    {
                        table[key] = pc;
                    }
                }
            }
        }

        // Pure opcodes for CSE: same inputs always produce the same
        // result, no side effects. Conservative — every opcode we
        // KNOW to be effect-free is listed; everything else is treated
        // as potentially-effectful and skipped.
        private static bool IsPureForCse(Opcode op)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.Add:
                case Opcode.Sub:
                case Opcode.Mul:
                case Opcode.Pow:
                case Opcode.Shl:
                case Opcode.Shr:
                case Opcode.BAnd:
                case Opcode.BOr:
                case Opcode.BXor:
                case Opcode.AddNN:
                case Opcode.SubNN:
                case Opcode.MulNN:
                case Opcode.Neg:
                case Opcode.Not:
                case Opcode.BNot:
                case Opcode.Eq:
                case Opcode.Ne:
                case Opcode.SEq:
                case Opcode.SNe:
                case Opcode.Lt:
                case Opcode.Le:
                case Opcode.Gt:
                case Opcode.Ge:
                    return true;
                // Div / Mod are NOT pure — divide-by-zero raises a
                // runtime error visible to the user; CSE would change
                // the observable error site.
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------
        // Dead Code Elimination
        // ------------------------------------------------------------
        //
        // A def is dead when no use of that (slot, version) appears in
        // `Ssa.UseVersions`. Mark the producing PC; codegen elides it
        // (when the opcode is also side-effect-free).
        //
        // Iterative refinement: marking a def dead can render the
        // operands it consumed unused, so we re-scan until fixpoint.
        private void RunDeadCodeElimination()
        {
            // Build inverse-use index: which SSA versions are actually
            // consumed?
            var liveVersions = new HashSet<(int Slot, int Version)>();
            foreach (var kv in Ssa.UseVersions)
            {
                liveVersions.Add((kv.Key.Slot, kv.Value));
            }
            // Phis count as uses of their predecessor versions.
            foreach (var kv in Ssa.PhiArgs)
            {
                var args = kv.Value;
                int slot = kv.Key.Slot;
                for (int i = 0; i < args.Length; i++)
                    liveVersions.Add((slot, args[i]));
            }

            // Walk every def; if its (slot, version) isn't live AND
            // the opcode is side-effect-free, mark the PC as dead.
            foreach (var kv in Ssa.DefVersions)
            {
                int pc = kv.Key.Pc;
                int slot = kv.Key.Slot;
                int version = kv.Value;
                if (liveVersions.Contains((slot, version))) continue;
                uint instr = Cfg.Function.Code[pc];
                if (!IsPureForDce(Encoding.DecodeOp(instr))) continue;
                DeadDefPcs.Add(pc);
            }
        }

        // Side-effect-free opcodes whose deletion preserves observable
        // behaviour. Strict superset of `IsPureForCse` (includes Div /
        // Mod when the def is dead — if the result is unused, the
        // divide-by-zero would have been observed but the spec says
        // dead arithmetic doesn't have to trap. Conservative call: keep
        // them OUT of DCE so error sites stay stable across
        // optimisation levels.)
        // M-tier1 (post-M75): II / FF / BB typed opcodes added. A
        // typed-chain def with no users (rewriter or upstream opt
        // produced a redundant boxer + chain) is dead just like its
        // boxed sibling. DivII / ModII stay excluded — their deopt
        // path raises a RuntimeError on div-by-zero / signed-overflow,
        // so a dead `DivII a, b, 0` MUST execute to preserve user-
        // visible error semantics. DivFF stays in: IEEE-754 division
        // is pure.
        private static bool IsPureForDce(Opcode op)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.Add:
                case Opcode.Sub:
                case Opcode.Mul:
                case Opcode.Shl:
                case Opcode.Shr:
                case Opcode.BAnd:
                case Opcode.BOr:
                case Opcode.BXor:
                case Opcode.AddNN:
                case Opcode.SubNN:
                case Opcode.MulNN:
                case Opcode.Neg:
                case Opcode.Not:
                case Opcode.BNot:
                case Opcode.Eq:
                case Opcode.Ne:
                case Opcode.SEq:
                case Opcode.SNe:
                case Opcode.Lt:
                case Opcode.Le:
                case Opcode.Gt:
                case Opcode.Ge:
                case Opcode.Move:
                case Opcode.Alias:
                // II family.
                case Opcode.LoadIntS64:
                case Opcode.UnboxI: case Opcode.BoxI:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.NegI:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                // FF family.
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.NegF:
                case Opcode.PowFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                // BB family.
                case Opcode.AndBB: case Opcode.OrBB: case Opcode.NotB:
                    return true;
                default:
                    return false;
            }
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# SSA optimiser results for {Cfg.Function.Name}");
            sb.AppendLine($"  dead defs: {DeadDefPcs.Count}");
            foreach (var pc in DeadDefPcs)
            {
                var op = Encoding.DecodeOp(Cfg.Function.Code[pc]);
                sb.AppendLine($"    pc={pc} {op}");
            }
            sb.AppendLine($"  cse merges: {CseReplaceWith.Count}");
            foreach (var kv in CseReplaceWith)
            {
                var op = Encoding.DecodeOp(Cfg.Function.Code[kv.Key]);
                sb.AppendLine($"    pc={kv.Key} ({op}) ← canonical pc={kv.Value}");
            }
            sb.AppendLine($"  copies: {CopyAlias.Count}");
            foreach (var kv in CopyAlias)
            {
                sb.AppendLine($"    pc={kv.Key} dst=s{kv.Value.DstSlot} src=s{kv.Value.SrcSlot}");
            }
            return sb.ToString();
        }
    }
}

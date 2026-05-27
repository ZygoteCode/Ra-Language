using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M60: Global Value Numbering on SSA + dominator tree.
    //
    // Extends M59's block-local CSE to a cross-block pass. Walks the
    // dominator tree DFS-style maintaining a scoped hash table
    // (Click-Cooper "Hash-based Value Numbering on SSA Form", 1995):
    //
    //   * On block entry, push a new scope onto the table.
    //   * Hash every pure opcode by
    //       (Opcode, SSA-version of operand B, SSA-version of operand C,
    //        Imm16).
    //     Two opcodes with the same key compute the same value at every
    //     point dominated by the first; the second is a redundancy.
    //   * On block exit, pop its scope so non-dominated successors don't
    //     reuse defs they can't reach.
    //
    // Dominance is the correctness guard: a redundancy found inside a
    // dominator scope is provably reachable from the canonical def, so
    // codegen can fold the second def into a reference to the first
    // without changing semantics.
    //
    // Why GVN beats plain CSE here: loop-invariant constants and
    // arithmetic computed once outside the loop are recognised as
    // identical inside the loop body, because the dominator tree
    // places the loop header (and the body it dominates) under the
    // pre-header scope where the canonical def lives. M61 LICM will
    // consume `RedundantWithDominator` to hoist those defs out of the
    // loop entirely.
    public sealed class GlobalValueNumbering
    {
        public readonly SsaForm Ssa;
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;

        // pc → canonical_pc. Records every redundant pure-opcode site
        // and the PC of the dominating canonical def. Forward-flow
        // consumable by codegen (the JIT emits a Mov from the
        // canonical reg) and by LICM (any redundancy whose canonical
        // pc lives outside the current loop is hoistable).
        public readonly Dictionary<int, int> RedundantWithDominator = new();

        private GlobalValueNumbering(SsaForm ssa)
        {
            Ssa = ssa;
            Cfg = ssa.Cfg;
            Dom = ssa.Dom;
        }

        public static GlobalValueNumbering Run(SsaForm ssa)
        {
            var gvn = new GlobalValueNumbering(ssa);
            gvn.Walk();
            return gvn;
        }

        // Hash key for a pure opcode. `Opcode` distinguishes shape;
        // operand SSA versions capture "what concrete value the inputs
        // hold"; Imm16 catches the const-pool index for LoadConst and
        // the immediate operand of LoadIntS. The def-slot A is
        // intentionally excluded — two defs writing to different slots
        // still compute the same value.
        private readonly struct VnKey : System.IEquatable<VnKey>
        {
            public readonly Opcode Op;
            public readonly int VerB;
            public readonly int VerC;
            public readonly int Imm;
            public VnKey(Opcode op, int b, int c, int imm) { Op = op; VerB = b; VerC = c; Imm = imm; }
            public bool Equals(VnKey o) => Op == o.Op && VerB == o.VerB && VerC == o.VerC && Imm == o.Imm;
            public override bool Equals(object? obj) => obj is VnKey k && Equals(k);
            public override int GetHashCode() => System.HashCode.Combine((int)Op, VerB, VerC, Imm);
        }

        private void Walk()
        {
            var tree = Dom.BuildDominatorTree();
            // Scope stack: each entry is a dictionary of value → canonical_pc.
            // Push on block entry, pop on exit so non-dominated branches
            // can't see each other's defs.
            var scopeStack = new Stack<Dictionary<VnKey, int>>();
            Visit(0, tree, scopeStack);
        }

        private void Visit(int bId, List<int>[] tree, Stack<Dictionary<VnKey, int>> scopes)
        {
            var localScope = new Dictionary<VnKey, int>();
            scopes.Push(localScope);
            var bb = Cfg.Blocks[bId];
            for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
            {
                uint instr = Cfg.Function.Code[pc];
                var op = Encoding.DecodeOp(instr);
                if (!IsPureForGvn(op)) continue;
                int b = Encoding.B(instr);
                int c = Encoding.C(instr);
                int bVer = Ssa.UseVersions.TryGetValue((pc, b), out var bv) ? bv : 0;
                int cVer = Ssa.UseVersions.TryGetValue((pc, c), out var cv) ? cv : 0;
                var key = new VnKey(op, bVer, cVer, Encoding.Imm16(instr));
                // Look up across the scope stack (innermost first).
                bool hit = false;
                foreach (var scope in scopes)
                {
                    if (scope.TryGetValue(key, out var canonicalPc))
                    {
                        RedundantWithDominator[pc] = canonicalPc;
                        hit = true;
                        break;
                    }
                }
                if (!hit) localScope[key] = pc;
            }
            // Recurse into dominator-tree children.
            foreach (var child in tree[bId]) Visit(child, tree, scopes);
            scopes.Pop();
        }

        // Pure opcodes safe to value-number.
        //
        // LoadLocalS / LoadGlobal / LoadBuiltin are EXCLUDED: even
        // though two reads of the same SymbolEntry produce two SSA
        // versions on different locals[] slots, the SymbolEntry behind
        // them mutates independently of the locals[]-SSA we model
        // (AssignBinding / StoreLocalS / opcode side effects through
        // FunctionCallExecutor can all change SymbolEntry.Value
        // without touching any locals[] slot SsaForm tracks). Substituting
        // a Move from the canonical's slot would read a stale value
        // post-mutation; correctness requires either modelling
        // SymbolEntry as memory SSA (M64 follow-up) or leaving these
        // reads alone — we choose the latter.
        //
        // Div / Mod / Pow stay excluded — error sites must stay
        // observable at the original PC. DivII / ModII excluded for
        // the same reason (deopt path raises RuntimeError on
        // div-by-zero / signed-overflow). DivFF stays included —
        // IEEE-754 division never throws (NaN / +-Inf are valid
        // results), so the result is referentially transparent.
        //
        // M-tier1 (post-M75): II / FF / BB typed-opcode families added
        // to the pure set so their def-use chains participate in GVN
        // CSE. Without this, every chain produced by the M66.4 / M68 /
        // M72 / M73 rewriter is treated as opaque — redundant
        // `LoadIntS64 1` / `UnboxI` / `AddII a, b, c` repeated across
        // a dominator tree never merge.
        private static bool IsPureForGvn(Opcode op)
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
                case Opcode.Ushr:
                case Opcode.Rol:
                case Opcode.Ror:
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
                // II family — Int64-typed (no throw on overflow; boxed-
                // BigNumber fallback is internally captured and produces
                // a deterministic value).
                case Opcode.LoadIntS64:
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.AddII:
                case Opcode.SubII:
                case Opcode.MulII:
                case Opcode.ShlII:
                case Opcode.ShrII:
                case Opcode.UshrII:
                case Opcode.RolII:
                case Opcode.RorII:
                case Opcode.BAndII:
                case Opcode.BOrII:
                case Opcode.BXorII:
                case Opcode.NegI:
                case Opcode.LtII:
                case Opcode.LeII:
                case Opcode.GtII:
                case Opcode.GeII:
                case Opcode.EqII:
                case Opcode.NeII:
                // FF family — Float64-typed (IEEE-754, deterministic).
                case Opcode.UnboxF:
                case Opcode.BoxF:
                case Opcode.AddFF:
                case Opcode.SubFF:
                case Opcode.MulFF:
                case Opcode.DivFF:
                case Opcode.NegF:
                // M80 — PowFF is IEEE-754 deterministic.
                case Opcode.PowFF:
                case Opcode.LtFF:
                case Opcode.LeFF:
                case Opcode.GtFF:
                case Opcode.GeFF:
                // BB family — Bool-typed (deterministic).
                case Opcode.AndBB:
                case Opcode.OrBB:
                case Opcode.NotB:
                    return true;
                default:
                    return false;
            }
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# GVN of {Cfg.Function.Name}");
            sb.AppendLine($"  redundant defs: {RedundantWithDominator.Count}");
            foreach (var kv in RedundantWithDominator)
            {
                var op = Encoding.DecodeOp(Cfg.Function.Code[kv.Key]);
                sb.AppendLine($"    pc={kv.Key} ({op}) ← dominator pc={kv.Value}");
            }
            return sb.ToString();
        }
    }
}

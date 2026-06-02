using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M54: turn `RaFunction.Code` (a flat uint32[] of opcodes) into a
    // basic-block CFG. Two-pass algorithm:
    //
    //   Pass 1 — collect *leaders*. A leader is:
    //     * PC 0 (entry).
    //     * Any PC that is a jump target (branch resolves to it).
    //     * Any PC immediately after a branch / Ret / Throw / Halt /
    //       TailCall — control would otherwise fall into a new block
    //       without an explicit jump.
    //
    //   Pass 2 — slice. Walk PCs in order; each leader opens a new
    //     block whose end is the PC just before the next leader.
    //     Classify the terminator (last opcode), wire successors.
    //
    // Exception edges (EnterTry/LeaveTry/handler entries) are NOT modelled
    // as CFG edges in this initial implementation — they form an
    // exceptional sub-CFG handled by the existing dispatch loop. Marking
    // them is a documented follow-up; the explicit-handler info already
    // lives on `RaFunction.EhTable` if a future pass wants to wire them
    // in.
    public static class CfgBuilder
    {
        public static ControlFlowGraph Build(RaFunction fn)
        {
            var cfg = new ControlFlowGraph(fn);
            int n = fn.Code.Length;
            if (n == 0)
            {
                cfg.Blocks.Add(new BasicBlock { Id = 0, StartPc = 0, EndPcExclusive = 0, Kind = TerminatorKind.Halt });
                return cfg;
            }

            // -------------------------------------------------------
            // Pass 1: leaders.
            // -------------------------------------------------------
            var leaders = new SortedSet<int> { 0 };
            for (int pc = 0; pc < n; pc++)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                var clz = ClassifyTerminator(op);
                if (clz == TerminatorKind.FallThrough) continue;

                // Successor branch target (if any).
                int? target = ResolveBranchTarget(op, instr, pc);
                if (target.HasValue && target.Value >= 0 && target.Value < n)
                {
                    leaders.Add(target.Value);
                }
                // Instruction after a terminator is a new leader (unless
                // the function ends here).
                int after = pc + InstructionWidth(op);
                if (after < n) leaders.Add(after);
            }

            // -------------------------------------------------------
            // Pass 2: slice + classify.
            // -------------------------------------------------------
            var leaderList = new List<int>(leaders);
            leaderList.Sort();
            for (int i = 0; i < leaderList.Count; i++)
            {
                int start = leaderList[i];
                int end = (i + 1 < leaderList.Count) ? leaderList[i + 1] : n;
                var bb = new BasicBlock
                {
                    Id = i,
                    StartPc = start,
                    EndPcExclusive = end,
                };
                cfg.Blocks.Add(bb);
                for (int p = start; p < end; p++) cfg.PcToBlock[p] = i;
            }

            // -------------------------------------------------------
            // Pass 3: wire successors based on each block's last
            // instruction.
            // -------------------------------------------------------
            for (int i = 0; i < cfg.Blocks.Count; i++)
            {
                var bb = cfg.Blocks[i];
                if (bb.Length == 0)
                {
                    bb.Kind = TerminatorKind.Halt;
                    continue;
                }
                int lastPc = bb.EndPcExclusive - 1;
                // Walk back if the slice ends mid-extension-word
                // (TailCall uses 1 word; no current opcode uses
                // multi-word encoding so this is a no-op for now, but
                // kept for future Wide-prefix safety).
                uint lastInstr = fn.Code[lastPc];
                var op = Encoding.DecodeOp(lastInstr);
                bb.Kind = ClassifyTerminator(op);
                int? target = ResolveBranchTarget(op, lastInstr, lastPc);
                int fallthrough = bb.EndPcExclusive;
                switch (bb.Kind)
                {
                    case TerminatorKind.FallThrough:
                        if (fallthrough < fn.Code.Length)
                            bb.Successors.Add(cfg.PcToBlock[fallthrough]);
                        break;
                    case TerminatorKind.Jump:
                        if (target.HasValue && (uint)target.Value < (uint)fn.Code.Length)
                            bb.Successors.Add(cfg.PcToBlock[target.Value]);
                        break;
                    case TerminatorKind.CondJump:
                        // Order: fallthrough first, then branch target.
                        // SSA / dominator algorithms don't care about
                        // ordering but downstream JIT codegen finds the
                        // convention useful for emitting fall-through-
                        // friendly branch instructions.
                        if (fallthrough < fn.Code.Length)
                            bb.Successors.Add(cfg.PcToBlock[fallthrough]);
                        if (target.HasValue && (uint)target.Value < (uint)fn.Code.Length)
                            bb.Successors.Add(cfg.PcToBlock[target.Value]);
                        break;
                    case TerminatorKind.Return:
                    case TerminatorKind.ReturnNull:
                    case TerminatorKind.Throw:
                    case TerminatorKind.Halt:
                    case TerminatorKind.TailCall:
                        // No in-function successor.
                        break;
                }
            }

            // -------------------------------------------------------
            // Pass 4: inverse-edge population (predecessors).
            // -------------------------------------------------------
            foreach (var bb in cfg.Blocks)
            {
                foreach (var s in bb.Successors)
                {
                    cfg.Blocks[s].Predecessors.Add(bb.Id);
                }
            }

            return cfg;
        }

        // Classification of an opcode by control-flow role. Every opcode
        // not listed is implicitly FallThrough — its execution leaves
        // control on the next PC.
        private static TerminatorKind ClassifyTerminator(Opcode op)
        {
            switch (op)
            {
                case Opcode.Jmp: return TerminatorKind.Jump;
                case Opcode.JmpIf:
                case Opcode.JmpIfNot:
                case Opcode.AndJz:
                case Opcode.OrJnz:
                case Opcode.NCJz:
                case Opcode.ForTest:
                case Opcode.ForEachNext:
                case Opcode.JmpIfStream:
                    return TerminatorKind.CondJump;
                case Opcode.Ret: return TerminatorKind.Return;
                case Opcode.RetNull: return TerminatorKind.ReturnNull;
                case Opcode.Throw: return TerminatorKind.Throw;
                case Opcode.MatchFail: return TerminatorKind.Throw; // L7: always throws (no-match)
                case Opcode.Halt: return TerminatorKind.Halt;
                case Opcode.TailCall: return TerminatorKind.TailCall;
                default: return TerminatorKind.FallThrough;
            }
        }

        // Returns the absolute PC of the branch target for branch opcodes,
        // or null for opcodes without a static target. PC-relative offsets
        // are added to the *next* PC: dispatch reads `instr = code[pc++]`
        // before applying the offset, so the runtime sees
        // `pc_after_read + offset = (insn_pc + 1) + offset`. Matches
        // `InstructionBuilder.PatchJumpToHere` which stores
        // `offset = target - (jumpPc + 1)`.
        private static int? ResolveBranchTarget(Opcode op, uint instr, int pc)
        {
            switch (op)
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
                    return pc + 1 + Encoding.SImm16(instr);
                default: return null;
            }
        }

        // Number of u32 words consumed by `op`. All current opcodes are
        // 1 word; reserved for future Wide / Far jump encodings.
        private static int InstructionWidth(Opcode op)
        {
            return 1;
        }
    }
}

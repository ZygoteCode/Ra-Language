using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M54: a contiguous run of opcodes with a single entry and a single
    // terminator. The flat `RaFunction.Code` array is the source of truth
    // at runtime; this struct is an *analysis-only* view built on demand
    // by `CfgBuilder` for optimisation / JIT codegen passes.
    //
    //   * `Id` is a dense small-int handle used as the array index inside
    //     `ControlFlowGraph.Blocks` — predecessors/successors reference
    //     blocks by this Id rather than by PC, so dominator and SSA
    //     algorithms stay in array-index space.
    //   * `StartPc` and `EndPcExclusive` delimit the slice of `Code` this
    //     block owns. `EndPcExclusive - 1` is the terminator instruction
    //     PC (or `EndPcExclusive - 1` is the last fall-through opcode
    //     when the block ends by joining the next block's leader).
    //   * `Successors` are the PC-resolved block ids the terminator can
    //     hand off to. Length 0 = exit block (Ret / RetNull / Throw /
    //     Halt / TailCall); length 1 = unconditional branch or
    //     fall-through; length 2 = conditional branch (fall-through then
    //     branch target).
    //   * `Predecessors` is the inverse; populated in a second pass after
    //     all successor edges are wired.
    //   * `Kind` records the terminator opcode class so consumers can
    //     pattern-match without re-decoding the last instruction.
    public sealed class BasicBlock
    {
        public int Id;
        public int StartPc;
        public int EndPcExclusive;
        public TerminatorKind Kind = TerminatorKind.FallThrough;
        public readonly List<int> Successors = new();
        public readonly List<int> Predecessors = new();

        public int Length => EndPcExclusive - StartPc;

        public override string ToString()
            => $"BB{Id}[{StartPc}..{EndPcExclusive}) {Kind} -> [{string.Join(",", Successors)}]";
    }

    public enum TerminatorKind : byte
    {
        // Block ends with an instruction that hands off to the immediately
        // following PC. Used when control reaches a leader of the next
        // block (split point) without an explicit branch.
        FallThrough = 0,
        // Unconditional jump (`OP_JMP`).
        Jump = 1,
        // Conditional jump (`OP_JMP_IF`, `OP_JMP_IF_NOT`, `OP_AND_JZ`,
        // `OP_OR_JNZ`, `OP_NC_JZ`, `OP_FOR_TEST`, `OP_FOR_EACH_NEXT`).
        // Successors[0] = fallthrough, Successors[1] = branch target.
        CondJump = 2,
        // Exit terminators with no in-function successor.
        Return = 3,
        ReturnNull = 4,
        Throw = 5,
        Halt = 6,
        // Tail call hands off to a different function; modelled as an
        // exit edge here (the trampoline / interpreter resumes the
        // caller's caller, not this function).
        TailCall = 7,
    }
}

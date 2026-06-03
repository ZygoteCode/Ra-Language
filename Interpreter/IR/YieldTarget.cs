using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR
{
    // L8: the nearest enclosing `match` / `switch` arm context that a `yield`
    // statement resolves into. In Ra, `yield X` inside a match/switch arm body
    // sets that arm's RESULT value to X and escapes the arm (the visitor models
    // it as a Yield control-flow signal the MatchNodeVisitor / SwitchNodeVisitor
    // intercept). The IR lowers it the same way `break` lowers: write X into the
    // construct's destination slot, pop any scopes opened since the arm started,
    // then forward-jump to the construct's end.
    //
    // One instance is pushed per match/switch being compiled and popped when its
    // end Pc is known. A `yield` with no enclosing target on the stack is a
    // function-level yield (propagates like `ret`) and stays on OP_NATIVE_DEFINE.
    internal sealed class YieldTarget
    {
        // The slot the construct reads its result value from (switch/match
        // `destSlot`). A firing `yield X` compiles X into this slot.
        public readonly byte DestSlot;

        // Forward-jump Pcs patched to the construct's end — SHARED with the
        // construct's arm-success `endJumps` list so a yield lands exactly where
        // a normal arm completion lands.
        public readonly List<int> EndJumps;

        // IrCompiler.State.ScopeDepth at the moment the construct started
        // compiling its arms. A `yield` nested inside arm-local scopes (an inner
        // `if` push, etc.) emits OP_POP_SCOPE down to this depth before its jump,
        // exactly like `break` unwinds to LoopContext.BaselineScopeDepth.
        public readonly int BaselineScopeDepth;

        public YieldTarget(byte destSlot, List<int> endJumps, int baselineScopeDepth)
        {
            DestSlot = destSlot;
            EndJumps = endJumps;
            BaselineScopeDepth = baselineScopeDepth;
        }
    }
}

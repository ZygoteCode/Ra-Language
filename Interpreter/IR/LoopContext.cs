using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR
{
    // Tracks pending forward-jump fixups for `break` and `continue` inside a
    // compiled loop body, plus the back-edge Pc for `retry`. One instance is
    // pushed onto IrCompiler's loop stack per active loop being compiled;
    // popped when the loop's exit Pc is known so all pending breaks can be
    // patched in a single pass.
    internal sealed class LoopContext
    {
        // Pcs of forward jumps that should be patched to the loop's exit Pc
        // once the body is fully emitted.
        public readonly List<int> BreakFixups = new();

        // Pcs of forward jumps that should be patched to the loop's
        // continue-target Pc (top-of-loop test for while/for, body-end for
        // do-while).
        public readonly List<int> ContinueFixups = new();

        // Backward-jump target for `retry` — points at the very first
        // instruction of the loop body (before condition test).
        public readonly int RetryTargetPc;

        // IrCompiler.State.ScopeDepth at the moment this loop's body started
        // compiling. Used to compute how many OP_POP_SCOPE opcodes must be
        // emitted in front of a `break` / `continue` that sits inside nested
        // scopes (an `if` body push, an inner `for` push, etc.).
        public readonly int BaselineScopeDepth;

        public LoopContext(int retryTargetPc, int baselineScopeDepth)
        {
            RetryTargetPc = retryTargetPc;
            BaselineScopeDepth = baselineScopeDepth;
        }
    }
}

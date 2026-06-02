using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR
{
    // L10 control-flow-escape try/finally: tracks the finally body a `return` /
    // `yield` inside the enclosing try (or catch) body must route THROUGH. One
    // instance is pushed per lowered try/finally while its try + catch bodies are
    // compiled, and popped before the finally body itself is compiled (so the
    // finally's own return/yield escape the fn normally, overriding the stash).
    internal sealed class FinallyContext
    {
        // Forward-jump Pcs (the jump after OP_SET_PENDING_FLOW) patched to the
        // finally body's entry Pc once it is known.
        public readonly List<int> ToFinally = new();

        // IrCompiler.State.ScopeDepth at the point the finally runs (the outer
        // scope, before the try's PushScope). A return/yield escape pops scopes
        // down to here before jumping to the finally.
        public readonly int ScopeDepth;

        public FinallyContext(int scopeDepth)
        {
            ScopeDepth = scopeDepth;
        }
    }
}

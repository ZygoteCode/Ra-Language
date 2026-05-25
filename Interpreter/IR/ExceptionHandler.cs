namespace RaLanguage.Interpreter.IR
{
    // Static per-function exception region. See RA_VM_MIGRATION.md §3.7.
    // The VM's OP_THROW handler walks RaFunction.EhTable from innermost to
    // outermost, picks the first region whose [StartPc, EndPc) covers the
    // faulting PC, and transfers control to CatchPc / FinallyPc.
    public readonly struct ExceptionHandler
    {
        // Protected region in instruction units: PC such that StartPc <= pc < EndPc.
        public readonly int StartPc;
        public readonly int EndPc;

        // -1 means "no catch in this region" (try/finally without catch).
        public readonly int CatchPc;

        // -1 means "no finally in this region" (try/catch without finally).
        public readonly int FinallyPc;

        // Local slot to receive the caught error message string. Unused if
        // CatchPc == -1. The IR compiler pre-allocates this slot so the
        // dispatch loop has a stable place to land the StringValue before
        // jumping into the catch body.
        public readonly byte CatchSlot;

        // Static scope depth at try-entry. On exception raise, the dispatch
        // loop pops the current Context back through `.Parent` until the
        // runtime ctx depth matches this — which restores the binding scope
        // to what it was at try-entry so the catch body sees the correct
        // outer scope.
        public readonly int ScopeDepth;

        public ExceptionHandler(int start, int end, int catchPc, int finallyPc, byte catchSlot, int scopeDepth)
        {
            StartPc = start;
            EndPc = end;
            CatchPc = catchPc;
            FinallyPc = finallyPc;
            CatchSlot = catchSlot;
            ScopeDepth = scopeDepth;
        }
    }
}

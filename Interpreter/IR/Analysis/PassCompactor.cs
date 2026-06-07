using RaLanguage.Errors;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M91 — Pass compaction. The final code transform: physically removes
    // every `Opcode.Pass` from `RaFunction.Code` and repatches every
    // PC-keyed structure so the shorter stream dispatches fewer opcodes.
    //
    // Passes accumulate from several earlier transforms:
    //   * SCCP branch folding (a statically-false cond-jump → Pass);
    //   * DCE (a dead pure def → Pass);
    //   * M90 compare-and-branch fusion (the absorbed JmpIfNot → Pass);
    //   * a handful the IR compiler emits directly as scope no-ops.
    // In the bench suite the fusion Passes alone are ~16% of dispatched
    // opcodes — each is a wasted fetch+decode+switch+break per loop
    // iteration. Removing them shrinks the hot loop body proportionally.
    //
    // Why this is safe to do as a *physical* shrink (unlike the 1:1
    // in-place rewrites in IrRewriter):
    //   * Every Ra opcode is exactly one u32 word (CfgBuilder.InstructionWidth
    //     ≡ 1; no Far/Wide multi-word encoding is emitted), so removing a
    //     word never splits an instruction.
    //   * The only opcodes that embed a PC are the relative jumps below; we
    //     recompute each offset against the post-compaction layout via the
    //     old→new PC map. The Eh table's absolute PCs and the PcSpans source
    //     map are remapped the same way.
    //   * Per-PC inline-cache arrays are empty at compile time (primed lazily
    //     at runtime), so they are simply reallocated to the new length.
    //
    // Resilience: if the function contains any opcode whose PC encoding this
    // pass does not handle (JmpFar's absolute extension, ForAwait's exit_pc,
    // the reserved Match* arm indices), it BAILS — leaving the function
    // byte-for-byte unchanged and fully correct in its un-compacted form.
    // It also bails if any jump target or rewritten offset fails validation,
    // so a malformed stream can never be silently corrupted.
    //
    // Runs LAST in IrCompiler.FinalizeFn, after FuseCompareBranches. Because
    // compaction only ever *removes* instructions between a jump and its
    // target, every recomputed offset has magnitude ≤ the original, so the
    // standard imm16 jumps and the fused s8 jumps both stay in range.
    public static class PassCompactor
    {
        // Returns the number of Pass instructions removed (0 if compaction
        // was skipped for any reason).
        public static int Compact(RaFunction fn)
        {
            if (fn == null || fn.Code == null) return 0;
            uint[] code = fn.Code;
            int oldLen = code.Length;
            if (oldLen == 0) return 0;

            // ---- Phase A: scan. Count Passes; bail on unsupported PC ops.
            int passCount = 0;
            for (int pc = 0; pc < oldLen; pc++)
            {
                switch (Encoding.DecodeOp(code[pc]))
                {
                    case Opcode.Pass:
                        passCount++;
                        break;
                    case Opcode.JmpFar:
                    case Opcode.ForAwait:
                    case Opcode.MatchArm:
                    case Opcode.MatchEnd:
                        return 0; // PC encoding not handled — leave function as-is
                }
            }
            if (passCount == 0) return 0;
            int newLen = oldLen - passCount;
            if (newLen <= 0) return 0; // degenerate (all Pass) — never happens, defensive

            // ---- Phase B: old→new PC map. A kept instruction maps to its
            // sequential new index; a Pass maps to the new index of the NEXT
            // kept instruction (so a jump that targeted the Pass now lands on
            // the instruction that followed it — identical control flow).
            // Length oldLen+1: index [oldLen] = newLen, used to remap an EH
            // EndPc that points one past the last instruction (exclusive).
            var oldToNew = new int[oldLen + 1];
            {
                int ni = 0;
                for (int pc = 0; pc < oldLen; pc++)
                {
                    oldToNew[pc] = ni;
                    if (Encoding.DecodeOp(code[pc]) != Opcode.Pass) ni++;
                }
                oldToNew[oldLen] = ni; // == newLen
            }

            // ---- Phase C: validate every jump target + recomputed offset
            // BEFORE mutating anything. On any failure, bail with no changes.
            for (int pc = 0; pc < oldLen; pc++)
            {
                uint instr = code[pc];
                var op = Encoding.DecodeOp(instr);
                if (op == Opcode.Pass) continue;
                int newPc = oldToNew[pc];
                if (IsRelImm16Jump(op))
                {
                    int oldTarget = pc + 1 + Encoding.SImm16(instr);
                    if (oldTarget < 0 || oldTarget > oldLen) return 0;
                    int newOff = oldToNew[oldTarget] - (newPc + 1);
                    if (newOff < short.MinValue || newOff > short.MaxValue) return 0;
                }
                else if (IsFusedCmpBranch(op))
                {
                    int oldTarget = pc + 1 + (sbyte)Encoding.C(instr);
                    if (oldTarget < 0 || oldTarget > oldLen) return 0;
                    int newOff = oldToNew[oldTarget] - (newPc + 1);
                    if (newOff < sbyte.MinValue || newOff > sbyte.MaxValue) return 0;
                }
            }

            // ---- Phase D: build the compacted Code[] with rewritten offsets.
            var newCode = new uint[newLen];
            int w = 0;
            for (int pc = 0; pc < oldLen; pc++)
            {
                uint instr = code[pc];
                var op = Encoding.DecodeOp(instr);
                if (op == Opcode.Pass) continue;
                int newPc = w;
                uint outInstr = instr;
                if (IsRelImm16Jump(op))
                {
                    int oldTarget = pc + 1 + Encoding.SImm16(instr);
                    int newOff = oldToNew[oldTarget] - (newPc + 1);
                    outInstr = Encoding.Pack2(op, Encoding.A(instr), unchecked((ushort)(short)newOff));
                }
                else if (IsFusedCmpBranch(op))
                {
                    int oldTarget = pc + 1 + (sbyte)Encoding.C(instr);
                    int newOff = oldToNew[oldTarget] - (newPc + 1);
                    outInstr = Encoding.Pack3(op, Encoding.A(instr), Encoding.B(instr), (byte)(sbyte)newOff);
                }
                newCode[w++] = outInstr;
            }
            fn.Code = newCode;

            // ---- Phase E: remap the exception table (absolute PCs).
            if (fn.EhTable.Length > 0)
            {
                var newEh = new ExceptionHandler[fn.EhTable.Length];
                for (int i = 0; i < fn.EhTable.Length; i++)
                {
                    var e = fn.EhTable[i];
                    newEh[i] = new ExceptionHandler(
                        oldToNew[ClampPc(e.StartPc, oldLen)],
                        oldToNew[ClampPc(e.EndPc, oldLen)],
                        e.CatchPc < 0 ? -1 : oldToNew[ClampPc(e.CatchPc, oldLen)],
                        e.FinallyPc < 0 ? -1 : oldToNew[ClampPc(e.FinallyPc, oldLen)],
                        e.CatchSlot,
                        e.ScopeDepth);
                }
                fn.EhTable = newEh;
            }

            // ---- Phase F: reallocate the (empty, lazily-primed) per-PC IC
            // arrays to the new length. Contents are zero at compile time, so
            // no remap is needed — they re-prime at runtime against new PCs.
            if (fn.LoadGlobalIc != null) fn.LoadGlobalIc = new LoadGlobalIcSlot[newLen];
            if (fn.EnumAccessIc != null) fn.EnumAccessIc = new EnumAccessIcSlot[newLen];
            if (fn.CastIc != null) fn.CastIc = new CastIcSlot[newLen];
            if (fn.MemberAccessIc != null) fn.MemberAccessIc = new MemberAccessIcSlot[newLen];
            if (fn.CallMethodIc != null) fn.CallMethodIc = new CallMethodIcSlot[newLen];

            // ---- Phase G: remap the source-span map (traceback positions).
            // oldToNew is monotonic non-decreasing, so the array stays sorted
            // for the error-time binary search.
            if (fn.PcSpansPc != null)
            {
                var src = fn.PcSpansPc;
                for (int i = 0; i < src.Length; i++)
                {
                    int p = src[i];
                    if (p >= 0 && p <= oldLen) src[i] = oldToNew[p];
                }
            }

            // ---- Phase H: drop stale analysis. The CFG/SSA/etc. bundle and
            // the Memory-SSA eligibility bitmap reference pre-compaction PCs.
            // Nothing runs after this point, so simply invalidate them.
            fn.Analysis = null;
            fn._memSsaEligible = null;
            fn._memSsaBarrierCached = 0;

            return passCount;
        }

        private static int ClampPc(int pc, int oldLen)
            => pc < 0 ? 0 : (pc > oldLen ? oldLen : pc);

        private static bool IsRelImm16Jump(Opcode op) =>
            op == Opcode.Jmp || op == Opcode.JmpIf || op == Opcode.JmpIfNot
            || op == Opcode.AndJz || op == Opcode.OrJnz || op == Opcode.NCJz
            || op == Opcode.ForTest || op == Opcode.ForEachNext
            || op == Opcode.JmpIfStream;

        private static bool IsFusedCmpBranch(Opcode op) =>
            op == Opcode.JmpNotLtII || op == Opcode.JmpNotLeII
            || op == Opcode.JmpNotGtII || op == Opcode.JmpNotGeII
            || op == Opcode.JmpNotEqII || op == Opcode.JmpNotNeII;
    }
}

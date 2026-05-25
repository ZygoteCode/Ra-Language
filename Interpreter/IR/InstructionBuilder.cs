using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR
{
    // Append-only u32 instruction buffer with snapshot/rollback and
    // forward-jump patching. Statements try-compile into the buffer; on
    // IrCompileException the caller calls Truncate(savedLen) to discard the
    // partial bytecode. Forward branches (if-skip, break, short-circuit)
    // emit a placeholder and patch the 16-bit imm slot when the target PC is
    // known.
    internal sealed class InstructionBuilder
    {
        private readonly List<uint> _code = new();

        public int Pc => _code.Count;

        public void Emit(uint instr) => _code.Add(instr);

        public void Emit3(Opcode op, byte a, byte b, byte c)
            => _code.Add(Encoding.Pack3(op, a, b, c));

        public void Emit2(Opcode op, byte a, ushort imm)
            => _code.Add(Encoding.Pack2(op, a, imm));

        // M82 — emit `op a b c` where `c` is a 16-bit refIdx that
        // may exceed 255. When over 255, a Wide prefix is emitted
        // first carrying the high byte of `c` in its own C position;
        // the following `op` instruction holds the low byte. The
        // dispatch loop's `wideHiC` state combines them. Caller
        // guarantees `refIdx <= 65535` via the IrCompiler pool-
        // overflow throws.
        public void Emit3WideC(Opcode op, byte a, byte b, int refIdx)
        {
            if ((uint)refIdx > 255)
            {
                _code.Add(Encoding.Pack3(Opcode.Wide, 0, 0, (byte)(refIdx >> 8)));
            }
            _code.Add(Encoding.Pack3(op, a, b, (byte)(refIdx & 0xFF)));
        }

        // Reserves a u32 slot for a jump and returns its PC index. The high
        // 16 bits (imm16) are zeroed and later filled by Patch16. The signed
        // offset interpretation is `target_pc - (jump_pc + 1)` — same as the
        // VM's `f.Pc += (short)imm16` after the jump increments Pc.
        public int EmitForwardJump(Opcode op, byte cond)
        {
            int pc = _code.Count;
            _code.Add(Encoding.Pack2(op, cond, 0));
            return pc;
        }

        // Unconditional forward-jump: no condition slot.
        public int EmitForwardJump(Opcode op)
        {
            int pc = _code.Count;
            _code.Add(Encoding.Pack2(op, 0, 0));
            return pc;
        }

        // Patches the imm16 of a previously-emitted forward jump so it
        // targets the *current* Pc. Throws IrCompileException if the offset
        // exceeds the signed-16-bit range (-32768..32767) — the caller falls
        // back to OP_VISIT_AST for the entire enclosing statement.
        public void PatchJumpToHere(int jumpPc)
        {
            int target = _code.Count;
            int offset = target - (jumpPc + 1);
            if (offset < short.MinValue || offset > short.MaxValue)
                throw new IrCompileException($"forward jump out of 16-bit range ({offset})");

            uint instr = _code[jumpPc];
            // Replace the high 16 bits (imm16) with the patched offset. Keep
            // the low 16 (opcode + slot) intact.
            uint patched = (instr & 0x0000FFFFu) | ((uint)(ushort)(short)offset << 16);
            _code[jumpPc] = patched;
        }

        // Emit a backward unconditional jump to a known earlier Pc.
        public void EmitBackwardJump(Opcode op, byte cond, int targetPc)
        {
            int here = _code.Count;
            int offset = targetPc - (here + 1);
            if (offset < short.MinValue || offset > short.MaxValue)
                throw new IrCompileException($"backward jump out of 16-bit range ({offset})");
            _code.Add(Encoding.Pack2(op, cond, (ushort)(short)offset));
        }

        public void Truncate(int pc)
        {
            if (_code.Count > pc) _code.RemoveRange(pc, _code.Count - pc);
        }

        public uint[] ToArray() => _code.ToArray();
    }
}

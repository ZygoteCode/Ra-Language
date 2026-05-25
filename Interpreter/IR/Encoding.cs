using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter.IR
{
    // 32-bit instruction encoding helpers. See RA_VM_MIGRATION.md §3.3.
    //
    //   layout1 (3-address):    [op:u8][a:u8][b:u8][c:u8]
    //   layout2 (16-bit imm):   [op:u8][a:u8][imm16:u16]
    //   layout3 (far / wide):   [op:u8][_:24] + extension u32
    //
    // The Wide prefix opcode (0xFF) signals the *following* instruction reads
    // b/c as one u16; not implemented in M1 (no current frame exceeds 256
    // slots in the test corpus), but the prefix slot is reserved.
    public static class Encoding
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Pack3(Opcode op, byte a, byte b, byte c)
            => (uint)op | ((uint)a << 8) | ((uint)b << 16) | ((uint)c << 24);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Pack2(Opcode op, byte a, ushort imm16)
            => (uint)op | ((uint)a << 8) | ((uint)imm16 << 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Opcode DecodeOp(uint instr) => (Opcode)(instr & 0xFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte A(uint instr) => (byte)((instr >> 8) & 0xFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte B(uint instr) => (byte)((instr >> 16) & 0xFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte C(uint instr) => (byte)((instr >> 24) & 0xFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Imm16(uint instr) => (ushort)((instr >> 16) & 0xFFFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short SImm16(uint instr) => unchecked((short)((instr >> 16) & 0xFFFF));
    }
}

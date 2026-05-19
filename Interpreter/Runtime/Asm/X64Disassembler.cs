using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// A pragmatic x64 disassembler.
    ///
    /// Scope: decodes the working subset emitted by <see cref="X64Mnemonics"/>
    /// (legacy/REX-prefixed integer ops, common SSE2, prefixes, branches, data
    /// directives, etc.). Coverage is intentionally narrower than the
    /// assembler — when we see a byte sequence we don't fully recognise we
    /// fall back to a hex dump so the output is always producible, never
    /// silent.
    ///
    /// This is a single-byte-at-a-time decoder, not a high-throughput one;
    /// the intent is debug / inspection rather than million-instructions-per-
    /// second JIT teardown.
    /// </summary>
    public static class X64Disassembler
    {
        public static List<string> Disassemble(byte[] bytes)
        {
            var output = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                int start = i;
                string mnem = DecodeOne(bytes, ref i);
                var sb = new StringBuilder();
                sb.Append("0x").Append(start.ToString("X4")).Append(": ");
                int hexLen = i - start;
                for (int k = 0; k < hexLen; k++) sb.Append(bytes[start + k].ToString("X2")).Append(' ');
                while (sb.Length < 32) sb.Append(' ');
                sb.Append(mnem);
                output.Add(sb.ToString());
            }
            return output;
        }

        private static string DecodeOne(byte[] bytes, ref int i)
        {
            int start = i;
            bool sizePrefix = false;
            bool repPrefix = false; bool repnePrefix = false;
            bool lockPrefix = false;
            bool addr32Prefix = false;
            byte? sse2Prefix = null;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b == 0x66) { sizePrefix = true; sse2Prefix = 0x66; i++; continue; }
                if (b == 0xF3) { repPrefix = true; sse2Prefix = 0xF3; i++; continue; }
                if (b == 0xF2) { repnePrefix = true; sse2Prefix = 0xF2; i++; continue; }
                if (b == 0xF0) { lockPrefix = true; i++; continue; }
                if (b == 0x67) { addr32Prefix = true; i++; continue; }
                if (b == 0x26 || b == 0x2E || b == 0x36 || b == 0x3E || b == 0x64 || b == 0x65) { i++; continue; }
                break;
            }

            bool rexW = false, rexR = false, rexX = false, rexB = false;
            if (i < bytes.Length && (bytes[i] & 0xF0) == 0x40)
            {
                byte rex = bytes[i++];
                rexW = (rex & 0x08) != 0;
                rexR = (rex & 0x04) != 0;
                rexX = (rex & 0x02) != 0;
                rexB = (rex & 0x01) != 0;
            }

            if (i >= bytes.Length) return "(truncated)";

            byte op = bytes[i++];

            var prefix = (lockPrefix ? "lock " : "") + (repPrefix ? "rep " : "") + (repnePrefix ? "repne " : "");

            string sz = rexW ? "q" : (sizePrefix ? "w" : "d");

            if (op == 0xC3) return prefix + "ret";
            if (op == 0xC9) return prefix + "leave";
            if (op == 0x90) return prefix + (repPrefix ? "pause" : "nop");
            if (op == 0xCC) return prefix + "int3";
            if (op == 0x99) return prefix + (rexW ? "cqo" : "cdq");
            if (op == 0xF8) return "clc";
            if (op == 0xF9) return "stc";
            if (op == 0xFC) return "cld";
            if (op == 0xFD) return "std";

            if (op == 0x0F)
            {
                if (i >= bytes.Length) return "(truncated 0F)";
                byte op2 = bytes[i++];
                if (op2 == 0x05) return "syscall";
                if (op2 == 0x31) return "rdtsc";
                if (op2 == 0xA2) return "cpuid";
                if ((op2 & 0xF0) == 0x80)
                {
                    int rel = ReadI32(bytes, ref i);
                    string mnem = JccName((byte)(op2 & 0x0F));
                    return $"{mnem} rel32:{rel:+#;-#;0}";
                }
                if ((op2 & 0xF0) == 0x90)
                {
                    byte modrm = bytes[i++];
                    var rm = DecodeModRM(bytes, ref i, modrm, rexB, rexX, RegSize.B8);
                    return $"set{ JccName((byte)(op2 & 0x0F)).Substring(1) } {rm}";
                }
                if (op2 == 0xAF)
                {
                    byte modrm = bytes[i++];
                    var dst = GprName(((modrm >> 3) & 7) | (rexR ? 8 : 0), rexW ? RegSize.B64 : RegSize.B32);
                    var src = DecodeModRM(bytes, ref i, modrm, rexB, rexX, rexW ? RegSize.B64 : RegSize.B32);
                    return $"imul {dst}, {src}";
                }
                if (op2 == 0xB6 || op2 == 0xB7 || op2 == 0xBE || op2 == 0xBF)
                {
                    byte modrm = bytes[i++];
                    string name = op2 == 0xB6 || op2 == 0xBE ? (op2 == 0xBE ? "movsx" : "movzx") : (op2 == 0xBF ? "movsx" : "movzx");
                    var dst = GprName(((modrm >> 3) & 7) | (rexR ? 8 : 0), rexW ? RegSize.B64 : RegSize.B32);
                    var src = DecodeModRM(bytes, ref i, modrm, rexB, rexX, op2 == 0xB6 || op2 == 0xBE ? RegSize.B8 : RegSize.B16);
                    return $"{name} {dst}, {src}";
                }
                if (op2 == 0xC8 || op2 == 0xC9 || op2 == 0xCA || op2 == 0xCB || op2 == 0xCC || op2 == 0xCD || op2 == 0xCE || op2 == 0xCF)
                {
                    int idx = (op2 - 0xC8) | (rexB ? 8 : 0);
                    return $"bswap {GprName(idx, rexW ? RegSize.B64 : RegSize.B32)}";
                }
                return $"(0F {op2:X2} …)";
            }

            if (op == 0xE8) { int rel = ReadI32(bytes, ref i); return $"call rel32:{rel:+#;-#;0}"; }
            if (op == 0xE9) { int rel = ReadI32(bytes, ref i); return $"jmp rel32:{rel:+#;-#;0}"; }
            if (op == 0xEB) { sbyte rel = (sbyte)bytes[i++]; return $"jmp short {rel:+#;-#;0}"; }

            if (op >= 0x50 && op <= 0x57) return $"push {GprName((op - 0x50) | (rexB ? 8 : 0), RegSize.B64)}";
            if (op >= 0x58 && op <= 0x5F) return $"pop {GprName((op - 0x58) | (rexB ? 8 : 0), RegSize.B64)}";

            if ((op & 0xF8) == 0xB8 && (op & 0x07) <= 7)
            {
                int reg = (op & 7) | (rexB ? 8 : 0);
                if (rexW)
                {
                    ulong imm64 = ReadU64(bytes, ref i);
                    return $"mov {GprName(reg, RegSize.B64)}, 0x{imm64:X}";
                }
                uint imm32 = (uint)ReadI32(bytes, ref i);
                return $"mov {GprName(reg, RegSize.B32)}, 0x{imm32:X}";
            }
            if ((op & 0xF8) == 0xB0)
            {
                int reg = (op & 7) | (rexB ? 8 : 0);
                byte imm = bytes[i++];
                return $"mov {GprName(reg, RegSize.B8)}, 0x{imm:X}";
            }

            int arithBase = op & 0xF8;
            if (op <= 0x3D && (op & 0x06) <= 0x06 && (arithBase == 0x00 || arithBase == 0x08 || arithBase == 0x10 || arithBase == 0x18 || arithBase == 0x20 || arithBase == 0x28 || arithBase == 0x30 || arithBase == 0x38))
            {
                string[] names = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
                int variant = op - arithBase;
                string name = names[arithBase >> 3];
                if (variant == 1)
                {
                    byte modrm = bytes[i++];
                    var rm = DecodeModRM(bytes, ref i, modrm, rexB, rexX, rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32));
                    var reg = GprName(((modrm >> 3) & 7) | (rexR ? 8 : 0), rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32));
                    return $"{prefix}{name} {rm}, {reg}";
                }
                if (variant == 3)
                {
                    byte modrm = bytes[i++];
                    var reg = GprName(((modrm >> 3) & 7) | (rexR ? 8 : 0), rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32));
                    var rm = DecodeModRM(bytes, ref i, modrm, rexB, rexX, rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32));
                    return $"{prefix}{name} {reg}, {rm}";
                }
            }

            if (op == 0x89 || op == 0x8B)
            {
                byte modrm = bytes[i++];
                var size = rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32);
                var reg = GprName(((modrm >> 3) & 7) | (rexR ? 8 : 0), size);
                var rm = DecodeModRM(bytes, ref i, modrm, rexB, rexX, size);
                return op == 0x89 ? $"mov {rm}, {reg}" : $"mov {reg}, {rm}";
            }

            if (op == 0xFF)
            {
                byte modrm = bytes[i++];
                int sub = (modrm >> 3) & 7;
                string[] subs = { "inc", "dec", "call", "callf", "jmp", "jmpf", "push", "—" };
                var rm = DecodeModRM(bytes, ref i, modrm, rexB, rexX, rexW ? RegSize.B64 : (sizePrefix ? RegSize.B16 : RegSize.B32));
                return $"{subs[sub]} {rm}";
            }

            return $"(0x{op:X2} unrecognised, prefixes:{(sizePrefix ? " 66" : "")}{(repPrefix ? " F3" : "")}{(repnePrefix ? " F2" : "")}{(lockPrefix ? " F0" : "")} rex.W={rexW})";
        }

        private static int ReadI32(byte[] b, ref int i)
        {
            int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
            i += 4;
            return v;
        }

        private static ulong ReadU64(byte[] b, ref int i)
        {
            ulong v = 0;
            for (int k = 0; k < 8; k++) v |= ((ulong)b[i + k]) << (k * 8);
            i += 8;
            return v;
        }

        private static string DecodeModRM(byte[] bytes, ref int i, byte modrm, bool rexB, bool rexX, RegSize size)
        {
            int mod = (modrm >> 6) & 3;
            int rm = modrm & 7;

            if (mod == 3)
                return GprName(rm | (rexB ? 8 : 0), size);

            if (rm == 4)
            {
                byte sib = bytes[i++];
                int scale = 1 << ((sib >> 6) & 3);
                int idx = (sib >> 3) & 7;
                int baseReg = sib & 7;
                bool isRipBase = false;

                string baseStr = "";
                if (mod == 0 && baseReg == 5)
                {
                    int disp = ReadI32(bytes, ref i);
                    return $"[disp32 0x{disp:X8}]";
                }
                else
                {
                    baseStr = GprName(baseReg | (rexB ? 8 : 0), RegSize.B64);
                }

                string idxStr = idx == 4 && !rexX ? "" : "+" + GprName(idx | (rexX ? 8 : 0), RegSize.B64) + (scale > 1 ? "*" + scale : "");

                long disp2 = 0;
                if (mod == 1) disp2 = (sbyte)bytes[i++];
                else if (mod == 2) disp2 = ReadI32(bytes, ref i);

                return $"[{baseStr}{idxStr}{(disp2 == 0 ? "" : (disp2 > 0 ? "+0x" + disp2.ToString("X") : "-0x" + (-disp2).ToString("X")))}]";
            }

            if (mod == 0 && rm == 5)
            {
                int disp = ReadI32(bytes, ref i);
                return $"[rip+0x{disp:X8}]";
            }

            string baseR = GprName(rm | (rexB ? 8 : 0), RegSize.B64);
            long d = 0;
            if (mod == 1) d = (sbyte)bytes[i++];
            else if (mod == 2) d = ReadI32(bytes, ref i);
            return $"[{baseR}{(d == 0 ? "" : (d > 0 ? "+0x" + d.ToString("X") : "-0x" + (-d).ToString("X")))}]";
        }

        private static string GprName(int idx, RegSize size)
        {
            string[] r64 = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
            string[] r32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d" };
            string[] r16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w" };
            string[] r8  = { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b" };
            idx &= 15;
            return size switch
            {
                RegSize.B64 => r64[idx],
                RegSize.B32 => r32[idx],
                RegSize.B16 => r16[idx],
                _ => r8[idx],
            };
        }

        private static string JccName(byte tttn)
        {
            string[] names = { "jo","jno","jb","jae","je","jne","jbe","ja","js","jns","jp","jnp","jl","jge","jle","jg" };
            return names[tttn & 0xF];
        }
    }
}

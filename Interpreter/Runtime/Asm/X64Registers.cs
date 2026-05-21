using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    internal enum RegClass : byte { Gpr, Xmm }
    internal enum RegSize : byte { B8 = 1, B16 = 2, B32 = 4, B64 = 8, X128 = 16 }

    internal readonly struct RegRef
    {
        public readonly RegClass Class;
        public readonly RegSize Size;
        public readonly byte Index;
        public bool IsExtended => Index >= 8;
        public bool NeedsRex => IsExtended || (Class == RegClass.Gpr && Size == RegSize.B8 && (Index == 4 || Index == 5 || Index == 6 || Index == 7) && IsHighByte == false && IsNewLowByte);
        public bool IsHighByte { get; }
        public bool IsNewLowByte { get; }

        public RegRef(RegClass cls, RegSize size, byte index, bool isHighByte = false, bool isNewLowByte = false)
        {
            Class = cls;
            Size = size;
            Index = index;
            IsHighByte = isHighByte;
            IsNewLowByte = isNewLowByte;
        }
    }

    internal static class X64Registers
    {
        private static readonly Dictionary<string, RegRef> _table = Build();

        public static bool TryParse(string name, out RegRef reg) => _table.TryGetValue(name, out reg);

        public static IEnumerable<string> AllNames => _table.Keys;

        private static Dictionary<string, RegRef> Build()
        {
            var d = new Dictionary<string, RegRef>(StringComparer.OrdinalIgnoreCase);

            string[] r64 = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
            string[] r32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d" };
            string[] r16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w" };
            string[] r8L = { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b" };
            string[] r8H = { "ah", "ch", "dh", "bh" };

            for (byte i = 0; i < 16; i++)
            {
                d[r64[i]] = new RegRef(RegClass.Gpr, RegSize.B64, i);
                d[r32[i]] = new RegRef(RegClass.Gpr, RegSize.B32, i);
                d[r16[i]] = new RegRef(RegClass.Gpr, RegSize.B16, i);
                d[r8L[i]] = new RegRef(RegClass.Gpr, RegSize.B8, i, false, i >= 4 && i <= 7);
            }
            for (byte i = 0; i < 4; i++)
            {
                d[r8H[i]] = new RegRef(RegClass.Gpr, RegSize.B8, (byte)(i + 4), true, false);
            }
            for (byte i = 0; i < 16; i++)
            {
                d["xmm" + i] = new RegRef(RegClass.Xmm, RegSize.X128, i);
            }

            return d;
        }
    }
}

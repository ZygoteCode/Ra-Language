using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Plain-language explanations for individual x64 mnemonics.
    /// Used by the `asm_explain` builtin.
    /// </summary>
    public static class AsmExplain
    {
        private static readonly Dictionary<string, string> _explain = new(StringComparer.OrdinalIgnoreCase)
        {
            { "mov", "Copy a value from source to destination (no flag changes)." },
            { "movabs", "Move 64-bit absolute immediate into a 64-bit register." },
            { "add", "Integer addition; updates OF/SF/ZF/AF/CF/PF." },
            { "sub", "Integer subtraction; updates OF/SF/ZF/AF/CF/PF." },
            { "imul", "Signed integer multiplication. 1-/2-/3-operand forms." },
            { "mul", "Unsigned integer multiplication; product in RDX:RAX." },
            { "idiv", "Signed division; RDX:RAX / src -> quotient RAX, remainder RDX." },
            { "div", "Unsigned division; RDX:RAX / src -> quotient RAX, remainder RDX." },
            { "and", "Bitwise AND; sets SF/ZF/PF, clears OF/CF." },
            { "or", "Bitwise OR; sets SF/ZF/PF, clears OF/CF." },
            { "xor", "Bitwise XOR; common idiom 'xor reg, reg' = zero with no immediate." },
            { "not", "One's-complement of destination (no flag changes)." },
            { "neg", "Two's-complement negation; sets CF=1 if src != 0." },
            { "cmp", "Compare (sub without writing destination); sets flags only." },
            { "test", "Bitwise AND without writing destination; sets SF/ZF/PF." },
            { "shl", "Logical/arithmetic left shift; CF gets last shifted-out bit." },
            { "sal", "Same as SHL." },
            { "shr", "Logical right shift (zero-fill); CF gets last shifted-out bit." },
            { "sar", "Arithmetic right shift (sign-fill)." },
            { "rol", "Rotate left through unchanged sign; CF gets last bit rotated out." },
            { "ror", "Rotate right." },
            { "lea", "Load effective address; computes the address expression without dereferencing." },
            { "push", "Decrement RSP by 8 (for r64) and store operand at [RSP]." },
            { "pop", "Load [RSP] into operand and increment RSP." },
            { "ret", "Return: pop RIP from stack. Optional imm16 pops that many bytes after." },
            { "leave", "Equivalent to 'mov rsp, rbp; pop rbp' — standard epilogue." },
            { "call", "Push return address (next RIP) and jump to target." },
            { "jmp", "Unconditional branch." },
            { "je",  "Jump if equal (ZF=1)." }, { "jz",  "Jump if zero (ZF=1)." },
            { "jne", "Jump if not equal (ZF=0)." }, { "jnz", "Jump if not zero (ZF=0)." },
            { "jl",  "Jump if signed less (SF<>OF)." }, { "jg",  "Jump if signed greater (ZF=0 and SF=OF)." },
            { "jle", "Jump if signed less-or-equal." }, { "jge", "Jump if signed greater-or-equal." },
            { "jb",  "Jump if unsigned below (CF=1)." }, { "ja",  "Jump if unsigned above." },
            { "jbe", "Jump if unsigned below-or-equal." }, { "jae", "Jump if unsigned above-or-equal." },
            { "jc", "Jump if carry." }, { "jnc", "Jump if no carry." },
            { "js", "Jump if sign." }, { "jns", "Jump if not sign." },
            { "jo", "Jump if overflow." }, { "jno", "Jump if not overflow." },
            { "jp", "Jump if parity (even number of 1-bits)." }, { "jnp", "Jump if no parity." },
            { "cmove", "Move if equal (ZF=1)." },
            { "cmovne", "Move if not equal." },
            { "setne", "Set destination byte to 1 if ZF=0, else 0." },
            { "sete", "Set destination byte to 1 if ZF=1." },
            { "movzx", "Move with zero extension." },
            { "movsx", "Move with sign extension." },
            { "movsxd", "Move dword to qword with sign extension." },
            { "xchg", "Atomic exchange (implicit LOCK when operand is memory)." },
            { "xadd", "Atomic exchange-and-add." },
            { "cmpxchg", "Atomic compare-and-exchange against RAX (or its sub-reg)." },
            { "lock", "Prefix that asserts the bus-lock (atomic) for the next RMW instruction." },
            { "rep", "Prefix repeating string op while RCX > 0." },
            { "rdtsc", "Read time-stamp counter into EDX:EAX." },
            { "rdtscp", "Read TSC + CPU ID into EDX:EAX/ECX (serializing)." },
            { "cpuid", "Identify processor features; input EAX[:ECX], output EAX/EBX/ECX/EDX." },
            { "syscall", "Linux/Win64 fast syscall; uses RAX as syscall number." },
            { "int3", "Software breakpoint (single-byte 0xCC)." },
            { "nop", "Do nothing (1 byte)." },
            { "endbr64", "Indirect branch target marker for Intel CET." },
            { "ud2", "Undefined Instruction (intentional trap)." },
            { "mfence", "Full memory fence (loads + stores)." },
            { "lfence", "Load fence." },
            { "sfence", "Store fence." },
            { "pause", "Spin-wait hint for the pipeline." },
            { "clflush", "Flush cache line containing the operand." },
            { "prefetcht0", "Prefetch into L1 cache (T0 hint)." },
            { "popcnt", "Population count (number of 1 bits)." },
            { "tzcnt", "Trailing zero count (BMI1)." },
            { "lzcnt", "Leading zero count." },
            { "bsf", "Bit scan forward (index of lowest set bit)." },
            { "bsr", "Bit scan reverse." },
            { "bswap", "Byte-swap a 32-bit or 64-bit register (endian flip)." },
            { "movsd", "Move scalar double (XMM)." },
            { "movss", "Move scalar single (XMM)." },
            { "addsd", "Add scalar double-precision." },
            { "subsd", "Subtract scalar double-precision." },
            { "mulsd", "Multiply scalar double-precision." },
            { "divsd", "Divide scalar double-precision." },
            { "sqrtsd", "Square root scalar double-precision." },
            { "ucomisd", "Unordered compare scalar doubles; sets EFLAGS." },
            { "cvtsi2sd", "Convert signed int (32/64) to scalar double." },
            { "cvtsd2si", "Convert scalar double to signed int (round)." },
            { "cvttsd2si", "Convert with truncation toward zero." },
            { "vaddpd", "AVX packed double add (VEX 3-op)." },
            { "vfmadd213sd", "AVX fused multiply-add (a*b + c) scalar double." },
        };

        public static string Explain(string mnemonic)
        {
            if (_explain.TryGetValue(mnemonic, out var s)) return s;
            return "(no description available — see Intel SDM Vol. 2)";
        }

        public static IEnumerable<string> AllExplained => _explain.Keys;
    }
}

using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Optional policy that rejects unsafe instructions before they reach the
    /// encoder. Used by sandboxed builds where untrusted users may submit
    /// asm sources.
    ///
    /// Default <see cref="Sandbox"/> blocks: privileged instructions, raw
    /// syscalls, debug traps, I/O ports, and any LOCK/REP-prefixed memory ops
    /// (mitigates micro-architectural side channels).
    /// </summary>
    public sealed class AsmSecurityPolicy
    {
        public HashSet<string> DisallowedMnemonics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ForbidIndirectCalls { get; set; }
        public bool ForbidExternalCalls { get; set; }
        public bool ForbidPrefixes { get; set; }
        public int MaxInstructions { get; set; } = int.MaxValue;
        public int MaxBytes { get; set; } = int.MaxValue;

        private int _seenInstrs;

        public static AsmSecurityPolicy Sandbox()
        {
            var p = new AsmSecurityPolicy();
            foreach (var m in new[]
            {
                "syscall", "sysret", "sysenter", "sysexit",
                "int3", "int", "into",
                "hlt", "cli", "sti",
                "in", "out", "ins", "outs",
                "rdmsr", "wrmsr", "rdpmc",
                "invd", "wbinvd", "invlpg",
                "lgdt", "sgdt", "lidt", "sidt", "lldt", "sldt", "ltr", "str",
                "swapgs", "lmsw", "smsw", "clts"
            }) p.DisallowedMnemonics.Add(m);
            p.ForbidPrefixes = true;
            return p;
        }

        public static AsmSecurityPolicy Permissive() => new AsmSecurityPolicy();

        internal void ValidateInstruction(ParsedInstr ins)
        {
            if (ins.Kind != InstrKind.Instruction) return;
            _seenInstrs++;
            if (_seenInstrs > MaxInstructions)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"sandbox: too many instructions (>{MaxInstructions})");
            if (DisallowedMnemonics.Contains(ins.Mnemonic))
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"sandbox: mnemonic '{ins.Mnemonic}' is forbidden");
            if (ForbidIndirectCalls && (ins.Mnemonic == "call" || ins.Mnemonic == "jmp"))
            {
                foreach (var op in ins.Operands)
                    if (op.Kind == OperandKind.Register || op.Kind == OperandKind.Memory)
                        throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"sandbox: indirect {ins.Mnemonic} is forbidden");
            }
            if (ForbidPrefixes && (ins.Mnemonic == "lock" || ins.Mnemonic.StartsWith("rep", StringComparison.OrdinalIgnoreCase)))
            {
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"sandbox: prefix '{ins.Mnemonic}' is forbidden");
            }
        }
    }
}

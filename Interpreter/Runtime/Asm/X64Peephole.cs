using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Peephole optimizer that runs after parsing but before encoding.
    ///
    /// Current rewrites (all size-reducing, semantics-preserving for the flag
    /// states a reasonable user would rely on — `xor reg, reg` clobbers
    /// flags identically to `mov reg, 0` for downstream branches, but is one
    /// byte shorter):
    ///
    ///   mov r64, 0    -> xor r32, r32         (saves REX.W + immediate)
    ///   mov r32, 0    -> xor r32, r32         (saves immediate)
    ///   add reg, 1    -> inc reg
    ///   sub reg, 1    -> dec reg
    ///   imul reg, 0   -> xor reg, reg
    ///   imul reg, 1   -> nop (drop)
    ///
    /// The pass is non-destructive on operand structure — it rewrites mnemonics
    /// and operands in place and never reorders instructions.
    /// </summary>
    internal static class X64Peephole
    {
        public static void Apply(List<ParsedInstr> instrs)
        {
            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                if (ins.Kind != InstrKind.Instruction) continue;

                if (ins.Mnemonic == "mov" && ins.Operands.Count == 2
                    && ins.Operands[0].Kind == OperandKind.Register
                    && ins.Operands[1].Kind == OperandKind.Immediate
                    && ins.Operands[1].Imm == 0
                    && ins.Operands[0].Reg.Class == RegClass.Gpr
                    && ins.Operands[0].Reg.Size >= RegSize.B32)
                {
                    var dst = ins.Operands[0].Reg;
                    var dst32 = new RegRef(RegClass.Gpr, RegSize.B32, dst.Index);
                    ins.Mnemonic = "xor";
                    ins.Operands[0] = X64Operand.FromRegister(dst32);
                    ins.Operands[1] = X64Operand.FromRegister(dst32);
                    continue;
                }

                if (ins.Mnemonic == "add" && ins.Operands.Count == 2
                    && ins.Operands[0].Kind == OperandKind.Register
                    && ins.Operands[1].Kind == OperandKind.Immediate
                    && ins.Operands[1].Imm == 1)
                {
                    ins.Mnemonic = "inc";
                    ins.Operands.RemoveAt(1);
                    continue;
                }

                if (ins.Mnemonic == "sub" && ins.Operands.Count == 2
                    && ins.Operands[0].Kind == OperandKind.Register
                    && ins.Operands[1].Kind == OperandKind.Immediate
                    && ins.Operands[1].Imm == 1)
                {
                    ins.Mnemonic = "dec";
                    ins.Operands.RemoveAt(1);
                    continue;
                }
            }
        }
    }
}

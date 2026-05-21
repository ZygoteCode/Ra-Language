using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// AVX / AVX2 / BMI1 / BMI2 mnemonics encoded through the VEX prefix.
    ///
    /// Encodes a representative working set (move / arithmetic / compare /
    /// shuffle / bitwise) sufficient for SIMD code without pulling in a full
    /// AVX-512 (EVEX) implementation.
    /// </summary>
    internal static class X64Avx
    {
        public static bool TryEmit(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            switch (ins.Mnemonic)
            {
                // ===== 3-operand vex binary ops (xmm/ymm dst, vvvv src, rm) =====
                case "vaddps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x58, false); return true;
                case "vaddpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x58, false); return true;
                case "vsubps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x5C, false); return true;
                case "vsubpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x5C, false); return true;
                case "vmulps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x59, false); return true;
                case "vmulpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x59, false); return true;
                case "vdivps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x5E, false); return true;
                case "vdivpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x5E, false); return true;
                case "vminps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x5D, false); return true;
                case "vminpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x5D, false); return true;
                case "vmaxps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x5F, false); return true;
                case "vmaxpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x5F, false); return true;
                case "vandps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x54, false); return true;
                case "vandpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x54, false); return true;
                case "vorps":   EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x56, false); return true;
                case "vorpd":   EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x56, false); return true;
                case "vxorps":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F, 0x57, false); return true;
                case "vxorpd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66,  X64Vex.MapMm.M0F, 0x57, false); return true;

                case "vpaddb":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xFC, false); return true;
                case "vpaddw":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xFD, false); return true;
                case "vpaddd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xFE, false); return true;
                case "vpaddq":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xD4, false); return true;
                case "vpsubb":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xF8, false); return true;
                case "vpsubw":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xF9, false); return true;
                case "vpsubd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xFA, false); return true;
                case "vpsubq":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xFB, false); return true;
                case "vpand":   EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xDB, false); return true;
                case "vpandn":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xDF, false); return true;
                case "vpor":    EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xEB, false); return true;
                case "vpxor":   EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F, 0xEF, false); return true;

                case "vmovups": EmitVexMov(ins, e, fixups, X64Vex.MapPp.None, 0x10, 0x11); return true;
                case "vmovupd": EmitVexMov(ins, e, fixups, X64Vex.MapPp.P66,  0x10, 0x11); return true;
                case "vmovaps": EmitVexMov(ins, e, fixups, X64Vex.MapPp.None, 0x28, 0x29); return true;
                case "vmovapd": EmitVexMov(ins, e, fixups, X64Vex.MapPp.P66,  0x28, 0x29); return true;
                case "vmovdqa": EmitVexMov(ins, e, fixups, X64Vex.MapPp.P66,  0x6F, 0x7F); return true;
                case "vmovdqu": EmitVexMov(ins, e, fixups, X64Vex.MapPp.PF3,  0x6F, 0x7F); return true;

                case "vaddsd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F, 0x58, false); return true;
                case "vaddss":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF3, X64Vex.MapMm.M0F, 0x58, false); return true;
                case "vsubsd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F, 0x5C, false); return true;
                case "vmulsd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F, 0x59, false); return true;
                case "vdivsd":  EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F, 0x5E, false); return true;
                case "vsqrtsd": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F, 0x51, false); return true;

                case "vfmadd213sd": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0xA9, true); return true;
                case "vfmadd213ss": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0xA9, false); return true;
                case "vfmadd231sd": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0xB9, true); return true;
                case "vfmadd231ss": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0xB9, false); return true;
                case "vfmadd132sd": EmitVexRvm(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0x99, true); return true;

                case "vbroadcastss": EmitVexBroadcast(ins, e, fixups, X64Vex.MapMm.M0F38, 0x18); return true;
                case "vbroadcastsd": EmitVexBroadcast(ins, e, fixups, X64Vex.MapMm.M0F38, 0x19); return true;

                case "bzhi": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.None, X64Vex.MapMm.M0F38, 0xF5); return true;
                case "pdep": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F38, 0xF5); return true;
                case "pext": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.PF3, X64Vex.MapMm.M0F38, 0xF5); return true;
                case "mulx": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F38, 0xF6); return true;
                case "shlx": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.P66, X64Vex.MapMm.M0F38, 0xF7); return true;
                case "sarx": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.PF3, X64Vex.MapMm.M0F38, 0xF7); return true;
                case "shrx": EmitBmiVexRvm_Bmi(ins, e, fixups, X64Vex.MapPp.PF2, X64Vex.MapMm.M0F38, 0xF7); return true;
            }
            return false;
        }

        private static bool IsYmm(X64Operand op)
        {
            return op.Kind == OperandKind.Register && op.Reg.Class == RegClass.Xmm && (op.Reg.Size == RegSize.X128 || op.Reg.Size == RegSize.B64);
        }

        private static void EmitVexRvm(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, X64Vex.MapPp pp, X64Vex.MapMm mm, byte opcode, bool w)
        {
            if (ins.Operands.Count != 3)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires 3 operands");
            var dst = ins.Operands[0];
            var src1 = ins.Operands[1];
            var src2 = ins.Operands[2];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be xmm");
            if (src1.Kind != OperandKind.Register || src1.Reg.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} 2nd op must be xmm");

            bool L = false;

            if (src2.Kind == OperandKind.Register)
            {
                X64Vex.EmitVex(e, L, src1.Reg.Index, dst.Reg.Index, 0, src2.Reg.Index, pp, mm, w);
                e.Byte(opcode);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src2.Reg.Index & 7));
                return;
            }
            if (src2.Kind == OperandKind.Memory)
            {
                byte rmBaseIdx = src2.HasMemBase ? src2.MemBase.Index : (byte)0;
                byte rmIdxIdx = src2.HasMemIndex ? src2.MemIndex.Index : (byte)0;
                X64Vex.EmitVex(e, L, src1.Reg.Index, dst.Reg.Index, rmIdxIdx, rmBaseIdx, pp, mm, w);
                e.Byte(opcode);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src2, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} 3rd op must be xmm or memory");
        }

        private static void EmitVexMov(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, X64Vex.MapPp pp, byte opRm, byte opMr)
        {
            if (ins.Operands.Count != 2)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires 2 operands");
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            bool L = false;

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                X64Vex.EmitVex(e, L, 0, dst.Reg.Index, 0, src.Reg.Index, pp, X64Vex.MapMm.M0F, false);
                e.Byte(opRm);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Memory)
            {
                byte rmBaseIdx = src.HasMemBase ? src.MemBase.Index : (byte)0;
                byte rmIdxIdx = src.HasMemIndex ? src.MemIndex.Index : (byte)0;
                X64Vex.EmitVex(e, L, 0, dst.Reg.Index, rmIdxIdx, rmBaseIdx, pp, X64Vex.MapMm.M0F, false);
                e.Byte(opRm);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Register)
            {
                byte rmBaseIdx = dst.HasMemBase ? dst.MemBase.Index : (byte)0;
                byte rmIdxIdx = dst.HasMemIndex ? dst.MemIndex.Index : (byte)0;
                X64Vex.EmitVex(e, L, 0, src.Reg.Index, rmIdxIdx, rmBaseIdx, pp, X64Vex.MapMm.M0F, false);
                e.Byte(opMr);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitVexBroadcast(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, X64Vex.MapMm mm, byte opcode)
        {
            if (ins.Operands.Count != 2)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires 2 operands");
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be xmm");
            if (src.Kind != OperandKind.Memory)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} src must be memory");
            byte rmBaseIdx = src.HasMemBase ? src.MemBase.Index : (byte)0;
            byte rmIdxIdx = src.HasMemIndex ? src.MemIndex.Index : (byte)0;
            X64Vex.EmitVex(e, false, 0, dst.Reg.Index, rmIdxIdx, rmBaseIdx, X64Vex.MapPp.P66, mm, false);
            e.Byte(opcode);
            X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
        }

        private static void EmitBmiVexRvm_Bmi(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, X64Vex.MapPp pp, X64Vex.MapMm mm, byte opcode)
        {
            if (ins.Operands.Count != 3)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires 3 operands");
            var dst = ins.Operands[0];
            var src1 = ins.Operands[1];
            var src2 = ins.Operands[2];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Gpr)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be GPR");
            if (src1.Kind != OperandKind.Register || src1.Reg.Class != RegClass.Gpr)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} 2nd op must be GPR");

            bool w = dst.Reg.Size == RegSize.B64;
            if (src2.Kind == OperandKind.Register)
            {
                X64Vex.EmitVex(e, false, src1.Reg.Index, dst.Reg.Index, 0, src2.Reg.Index, pp, mm, w);
                e.Byte(opcode);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src2.Reg.Index & 7));
                return;
            }
            if (src2.Kind == OperandKind.Memory)
            {
                byte rmBaseIdx = src2.HasMemBase ? src2.MemBase.Index : (byte)0;
                byte rmIdxIdx = src2.HasMemIndex ? src2.MemIndex.Index : (byte)0;
                X64Vex.EmitVex(e, false, src1.Reg.Index, dst.Reg.Index, rmIdxIdx, rmBaseIdx, pp, mm, w);
                e.Byte(opcode);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src2, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }
    }
}

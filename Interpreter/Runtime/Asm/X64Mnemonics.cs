using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    internal static class X64Mnemonics
    {
        public static void Emit(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            switch (ins.Mnemonic)
            {
                case "mov":   EmitArith(ins, e, fixups, ArithOp.Mov); return;
                case "add":   EmitArith(ins, e, fixups, ArithOp.Add); return;
                case "or":    EmitArith(ins, e, fixups, ArithOp.Or); return;
                case "adc":   EmitArith(ins, e, fixups, ArithOp.Adc); return;
                case "sbb":   EmitArith(ins, e, fixups, ArithOp.Sbb); return;
                case "and":   EmitArith(ins, e, fixups, ArithOp.And); return;
                case "sub":   EmitArith(ins, e, fixups, ArithOp.Sub); return;
                case "xor":   EmitArith(ins, e, fixups, ArithOp.Xor); return;
                case "cmp":   EmitArith(ins, e, fixups, ArithOp.Cmp); return;
                case "test":  EmitTest(ins, e, fixups); return;

                case "inc":   EmitUnary(ins, e, fixups, 0); return;
                case "dec":   EmitUnary(ins, e, fixups, 1); return;
                case "neg":   EmitUnaryGroup3(ins, e, fixups, 3); return;
                case "not":   EmitUnaryGroup3(ins, e, fixups, 2); return;
                case "mul":   EmitUnaryGroup3(ins, e, fixups, 4); return;
                case "imul":  EmitImul(ins, e, fixups); return;
                case "div":   EmitUnaryGroup3(ins, e, fixups, 6); return;
                case "idiv":  EmitUnaryGroup3(ins, e, fixups, 7); return;

                case "push":  EmitPush(ins, e, fixups); return;
                case "pop":   EmitPop(ins, e, fixups); return;

                case "shl": case "sal": EmitShift(ins, e, fixups, 4); return;
                case "shr":             EmitShift(ins, e, fixups, 5); return;
                case "sar":             EmitShift(ins, e, fixups, 7); return;
                case "rol":             EmitShift(ins, e, fixups, 0); return;
                case "ror":             EmitShift(ins, e, fixups, 1); return;
                case "rcl":             EmitShift(ins, e, fixups, 2); return;
                case "rcr":             EmitShift(ins, e, fixups, 3); return;

                case "lea":   EmitLea(ins, e, fixups); return;

                case "ret":   EmitRet(ins, e); return;
                case "retn":  EmitRet(ins, e); return;
                case "leave": e.Byte(0xC9); return;
                case "nop":   e.Byte(0x90); return;
                case "int3":  e.Byte(0xCC); return;
                case "syscall": e.Bytes(0x0F, 0x05); return;
                case "cdq":   e.Byte(0x99); return;
                case "cqo":   e.Bytes(0x48, 0x99); return;
                case "cwd":   e.Bytes(0x66, 0x99); return;

                case "jmp":   EmitJmp(ins, e, fixups); return;
                case "call":  EmitCall(ins, e, fixups); return;

                case "movzx": EmitMovzxMovsx(ins, e, fixups, false, false); return;
                case "movsx": EmitMovzxMovsx(ins, e, fixups, true, false); return;
                case "movsxd":EmitMovzxMovsx(ins, e, fixups, true, true); return;

                case "movabs":EmitMovabs(ins, e); return;

                case "movsd": EmitSse2(ins, e, fixups, 0xF2, 0x10, 0x11, true); return;
                case "movss": EmitSse2(ins, e, fixups, 0xF3, 0x10, 0x11, true); return;
                case "addsd": EmitSse2Binary(ins, e, fixups, 0xF2, 0x58); return;
                case "subsd": EmitSse2Binary(ins, e, fixups, 0xF2, 0x5C); return;
                case "mulsd": EmitSse2Binary(ins, e, fixups, 0xF2, 0x59); return;
                case "divsd": EmitSse2Binary(ins, e, fixups, 0xF2, 0x5E); return;
                case "addss": EmitSse2Binary(ins, e, fixups, 0xF3, 0x58); return;
                case "subss": EmitSse2Binary(ins, e, fixups, 0xF3, 0x5C); return;
                case "mulss": EmitSse2Binary(ins, e, fixups, 0xF3, 0x59); return;
                case "divss": EmitSse2Binary(ins, e, fixups, 0xF3, 0x5E); return;
                case "sqrtsd":EmitSse2Binary(ins, e, fixups, 0xF2, 0x51); return;
                case "sqrtss":EmitSse2Binary(ins, e, fixups, 0xF3, 0x51); return;
                case "ucomisd":EmitSse2Binary(ins, e, fixups, 0x66, 0x2E); return;
                case "comisd": EmitSse2Binary(ins, e, fixups, 0x66, 0x2F); return;
                case "ucomiss":EmitSse2Binary(ins, e, fixups, 0x00, 0x2E); return;
                case "comiss": EmitSse2Binary(ins, e, fixups, 0x00, 0x2F); return;
                case "cvtsi2sd": EmitCvtSi2(ins, e, fixups, 0xF2); return;
                case "cvtsi2ss": EmitCvtSi2(ins, e, fixups, 0xF3); return;
                case "cvtsd2si": EmitCvt2Si(ins, e, fixups, 0xF2, 0x2D); return;
                case "cvttsd2si":EmitCvt2Si(ins, e, fixups, 0xF2, 0x2C); return;
                case "cvtss2si": EmitCvt2Si(ins, e, fixups, 0xF3, 0x2D); return;
                case "cvttss2si":EmitCvt2Si(ins, e, fixups, 0xF3, 0x2C); return;
                case "cvtsd2ss": EmitSse2Binary(ins, e, fixups, 0xF2, 0x5A); return;
                case "cvtss2sd": EmitSse2Binary(ins, e, fixups, 0xF3, 0x5A); return;
                case "xorps":    EmitSse2Binary(ins, e, fixups, 0x00, 0x57); return;
                case "xorpd":    EmitSse2Binary(ins, e, fixups, 0x66, 0x57); return;
                case "andps":    EmitSse2Binary(ins, e, fixups, 0x00, 0x54); return;
                case "andpd":    EmitSse2Binary(ins, e, fixups, 0x66, 0x54); return;
                case "orps":     EmitSse2Binary(ins, e, fixups, 0x00, 0x56); return;
                case "orpd":     EmitSse2Binary(ins, e, fixups, 0x66, 0x56); return;
                case "movd":     EmitMovd(ins, e, fixups, false); return;
                case "movq":     EmitMovd(ins, e, fixups, true); return;

                case "db": EmitData(ins, e, 1); return;
                case "dw": EmitData(ins, e, 2); return;
                case "dd": EmitData(ins, e, 4); return;
                case "dq": EmitData(ins, e, 8); return;
                case "resb": EmitReserve(ins, e, 1); return;
                case "resw": EmitReserve(ins, e, 2); return;
                case "resd": EmitReserve(ins, e, 4); return;
                case "resq": EmitReserve(ins, e, 8); return;
                case "align": EmitAlign(ins, e); return;
                case "times": EmitTimes(ins, e, fixups); return;

                case "bswap": EmitBswap(ins, e); return;

                case "xchg": EmitXchg(ins, e, fixups); return;
                case "xadd": EmitXadd(ins, e, fixups); return;
                case "cmpxchg": EmitCmpxchg(ins, e, fixups); return;
                case "cmpxchg8b": EmitCmpxchgN(ins, e, fixups, false); return;
                case "cmpxchg16b": EmitCmpxchgN(ins, e, fixups, true); return;

                case "bt":  EmitBitTest(ins, e, fixups, 4); return;
                case "bts": EmitBitTest(ins, e, fixups, 5); return;
                case "btr": EmitBitTest(ins, e, fixups, 6); return;
                case "btc": EmitBitTest(ins, e, fixups, 7); return;
                case "bsf": EmitBsfBsr(ins, e, fixups, 0xBC); return;
                case "bsr": EmitBsfBsr(ins, e, fixups, 0xBD); return;
                case "popcnt": EmitPopcnt(ins, e, fixups, 0xF3, 0xB8); return;
                case "lzcnt":  EmitPopcnt(ins, e, fixups, 0xF3, 0xBD); return;
                case "tzcnt":  EmitPopcnt(ins, e, fixups, 0xF3, 0xBC); return;

                case "mfence": e.Bytes(0x0F, 0xAE, 0xF0); return;
                case "lfence": e.Bytes(0x0F, 0xAE, 0xE8); return;
                case "sfence": e.Bytes(0x0F, 0xAE, 0xF8); return;
                case "pause":  e.Bytes(0xF3, 0x90); return;
                case "clflush":EmitClflush(ins, e, fixups); return;
                case "prefetcht0": EmitPrefetch(ins, e, fixups, 1); return;
                case "prefetcht1": EmitPrefetch(ins, e, fixups, 2); return;
                case "prefetcht2": EmitPrefetch(ins, e, fixups, 3); return;
                case "prefetchnta":EmitPrefetch(ins, e, fixups, 0); return;

                case "rdtsc":  e.Bytes(0x0F, 0x31); return;
                case "rdtscp": e.Bytes(0x0F, 0x01, 0xF9); return;
                case "cpuid":  e.Bytes(0x0F, 0xA2); return;
                case "rdrand": EmitGroup15(ins, e, fixups, 6); return;
                case "rdseed": EmitGroup15(ins, e, fixups, 7); return;

                case "movs": case "movsb": e.Byte(0xA4); return;
                case "movsw": e.Bytes(0x66, 0xA5); return;
                case "movsd_str": e.Byte(0xA5); return;
                case "movsq": e.Bytes(0x48, 0xA5); return;
                case "stosb": e.Byte(0xAA); return;
                case "stosw": e.Bytes(0x66, 0xAB); return;
                case "stosd": e.Byte(0xAB); return;
                case "stosq": e.Bytes(0x48, 0xAB); return;
                case "lodsb": e.Byte(0xAC); return;
                case "lodsw": e.Bytes(0x66, 0xAD); return;
                case "lodsd": e.Byte(0xAD); return;
                case "lodsq": e.Bytes(0x48, 0xAD); return;
                case "cmpsb": e.Byte(0xA6); return;
                case "cmpsw": e.Bytes(0x66, 0xA7); return;
                case "cmpsd_str": e.Byte(0xA7); return;
                case "cmpsq": e.Bytes(0x48, 0xA7); return;
                case "scasb": e.Byte(0xAE); return;
                case "scasw": e.Bytes(0x66, 0xAF); return;
                case "scasd": e.Byte(0xAF); return;
                case "scasq": e.Bytes(0x48, 0xAF); return;

                case "__prefix": e.Byte((byte)(ins.Operands[0].Imm & 0xff)); return;

                case "movups": EmitSse2(ins, e, fixups, 0x00, 0x10, 0x11, true); return;
                case "movupd": EmitSse2(ins, e, fixups, 0x66, 0x10, 0x11, true); return;
                case "movaps": EmitSse2(ins, e, fixups, 0x00, 0x28, 0x29, true); return;
                case "movapd": EmitSse2(ins, e, fixups, 0x66, 0x28, 0x29, true); return;
                case "movdqa": EmitSse2(ins, e, fixups, 0x66, 0x6F, 0x7F, true); return;
                case "movdqu": EmitSse2(ins, e, fixups, 0xF3, 0x6F, 0x7F, true); return;

                case "addps": EmitSse2Binary(ins, e, fixups, 0x00, 0x58); return;
                case "subps": EmitSse2Binary(ins, e, fixups, 0x00, 0x5C); return;
                case "mulps": EmitSse2Binary(ins, e, fixups, 0x00, 0x59); return;
                case "divps": EmitSse2Binary(ins, e, fixups, 0x00, 0x5E); return;
                case "addpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x58); return;
                case "subpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x5C); return;
                case "mulpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x59); return;
                case "divpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x5E); return;
                case "minps": EmitSse2Binary(ins, e, fixups, 0x00, 0x5D); return;
                case "maxps": EmitSse2Binary(ins, e, fixups, 0x00, 0x5F); return;
                case "minpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x5D); return;
                case "maxpd": EmitSse2Binary(ins, e, fixups, 0x66, 0x5F); return;

                case "paddb": EmitSse2Binary(ins, e, fixups, 0x66, 0xFC); return;
                case "paddw": EmitSse2Binary(ins, e, fixups, 0x66, 0xFD); return;
                case "paddd": EmitSse2Binary(ins, e, fixups, 0x66, 0xFE); return;
                case "paddq": EmitSse2Binary(ins, e, fixups, 0x66, 0xD4); return;
                case "psubb": EmitSse2Binary(ins, e, fixups, 0x66, 0xF8); return;
                case "psubw": EmitSse2Binary(ins, e, fixups, 0x66, 0xF9); return;
                case "psubd": EmitSse2Binary(ins, e, fixups, 0x66, 0xFA); return;
                case "psubq": EmitSse2Binary(ins, e, fixups, 0x66, 0xFB); return;
                case "pand":  EmitSse2Binary(ins, e, fixups, 0x66, 0xDB); return;
                case "pandn": EmitSse2Binary(ins, e, fixups, 0x66, 0xDF); return;
                case "por":   EmitSse2Binary(ins, e, fixups, 0x66, 0xEB); return;
                case "pxor":  EmitSse2Binary(ins, e, fixups, 0x66, 0xEF); return;
                case "pcmpeqb": EmitSse2Binary(ins, e, fixups, 0x66, 0x74); return;
                case "pcmpeqw": EmitSse2Binary(ins, e, fixups, 0x66, 0x75); return;
                case "pcmpeqd": EmitSse2Binary(ins, e, fixups, 0x66, 0x76); return;
                case "pcmpgtb": EmitSse2Binary(ins, e, fixups, 0x66, 0x64); return;
                case "pcmpgtw": EmitSse2Binary(ins, e, fixups, 0x66, 0x65); return;
                case "pcmpgtd": EmitSse2Binary(ins, e, fixups, 0x66, 0x66); return;

                case "pmovmskb": EmitPmovmskb(ins, e, fixups); return;
                case "movmskps": EmitMovmsk(ins, e, fixups, 0x00); return;
                case "movmskpd": EmitMovmsk(ins, e, fixups, 0x66); return;

                case "cmovne": case "cmovnz": EmitCmov(ins, e, fixups, 0x45); return;
                case "cmove":  case "cmovz":  EmitCmov(ins, e, fixups, 0x44); return;
                case "cmovl":  EmitCmov(ins, e, fixups, 0x4C); return;
                case "cmovle": EmitCmov(ins, e, fixups, 0x4E); return;
                case "cmovg":  EmitCmov(ins, e, fixups, 0x4F); return;
                case "cmovge": EmitCmov(ins, e, fixups, 0x4D); return;
                case "cmovb":  case "cmovc": case "cmovnae": EmitCmov(ins, e, fixups, 0x42); return;
                case "cmovae": case "cmovnb": case "cmovnc": EmitCmov(ins, e, fixups, 0x43); return;
                case "cmovbe": case "cmovna": EmitCmov(ins, e, fixups, 0x46); return;
                case "cmova":  case "cmovnbe":EmitCmov(ins, e, fixups, 0x47); return;
                case "cmovs":  EmitCmov(ins, e, fixups, 0x48); return;
                case "cmovns": EmitCmov(ins, e, fixups, 0x49); return;
                case "cmovo":  EmitCmov(ins, e, fixups, 0x40); return;
                case "cmovno": EmitCmov(ins, e, fixups, 0x41); return;
                case "cmovp":  case "cmovpe": EmitCmov(ins, e, fixups, 0x4A); return;
                case "cmovnp": case "cmovpo": EmitCmov(ins, e, fixups, 0x4B); return;

                case "shld": EmitShldShrd(ins, e, fixups, 0xA4, 0xA5); return;
                case "shrd": EmitShldShrd(ins, e, fixups, 0xAC, 0xAD); return;

                case "clc": e.Byte(0xF8); return;
                case "stc": e.Byte(0xF9); return;
                case "cmc": e.Byte(0xF5); return;
                case "cld": e.Byte(0xFC); return;
                case "std": e.Byte(0xFD); return;
                case "pushfq": e.Byte(0x9C); return;
                case "popfq":  e.Byte(0x9D); return;
                case "lahf": e.Byte(0x9F); return;
                case "sahf": e.Byte(0x9E); return;
                case "endbr64": e.Bytes(0xF3, 0x0F, 0x1E, 0xFA); return;
                case "endbr32": e.Bytes(0xF3, 0x0F, 0x1E, 0xFB); return;
                case "ud2": e.Bytes(0x0F, 0x0B); return;
                case "ud1": e.Bytes(0x0F, 0xB9, 0xC0); return;
                case "wait": case "fwait": e.Byte(0x9B); return;

                case "andn": EmitBmiAndn(ins, e, fixups); return;

                default:
                    if (ins.Mnemonic.StartsWith("j") && IsJccMnemonic(ins.Mnemonic))
                    {
                        EmitJcc(ins, e, fixups);
                        return;
                    }
                    if (ins.Mnemonic.StartsWith("set") && IsSetccMnemonic(ins.Mnemonic))
                    {
                        EmitSetcc(ins, e, fixups);
                        return;
                    }
                    if (ins.Mnemonic.StartsWith("v") && X64Avx.TryEmit(ins, e, fixups)) return;
                    if (X64Avx.TryEmit(ins, e, fixups)) return;
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"unsupported mnemonic '{ins.Mnemonic}'. {DidYouMean(ins.Mnemonic)}");
            }
        }

        private enum ArithOp { Add, Or, Adc, Sbb, And, Sub, Xor, Cmp, Mov }

        private static void EmitArith(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, ArithOp op)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            if (op == ArithOp.Mov)
            {
                EmitMov(ins, e, fixups, dst, src);
                return;
            }

            int groupDigit = op switch
            {
                ArithOp.Add => 0, ArithOp.Or => 1, ArithOp.Adc => 2, ArithOp.Sbb => 3,
                ArithOp.And => 4, ArithOp.Sub => 5, ArithOp.Xor => 6, ArithOp.Cmp => 7,
                _ => throw new InvalidOperationException()
            };

            int regMemBase = op switch
            {
                ArithOp.Add => 0x00, ArithOp.Or => 0x08, ArithOp.Adc => 0x10, ArithOp.Sbb => 0x18,
                ArithOp.And => 0x20, ArithOp.Sub => 0x28, ArithOp.Xor => 0x30, ArithOp.Cmp => 0x38,
                _ => throw new InvalidOperationException()
            };

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                int opc = regMemBase + (dst.Reg.Size == RegSize.B8 ? 0 : 1);
                EmitOperandSizePrefix(e, dst.Reg.Size);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                e.Byte((byte)opc);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                return;
            }

            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Register)
            {
                int opc = regMemBase + (src.Reg.Size == RegSize.B8 ? 0 : 1);
                EmitOperandSizePrefix(e, src.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                e.Byte((byte)opc);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Memory)
            {
                int opc = regMemBase + 2 + (dst.Reg.Size == RegSize.B8 ? 0 : 1);
                EmitOperandSizePrefix(e, dst.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Byte((byte)opc);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Immediate)
            {
                EmitArithImm(e, dst, src.Imm, groupDigit, regMemBase, ins);
                return;
            }

            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Immediate)
            {
                EmitArithImmMem(e, dst, src.Imm, groupDigit, fixups, ins);
                return;
            }

            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitArithImm(X64Emit e, X64Operand dst, long imm, int digit, int regMemBase, ParsedInstr ins)
        {
            var reg = dst.Reg;
            RegSize size = reg.Size;
            EmitOperandSizePrefix(e, size);

            if (size == RegSize.B8)
            {
                if (reg.Index == 0 && !reg.IsExtended)
                {
                    X64Encoder.WriteRex(e, false, false, false, false);
                    e.Byte((byte)(regMemBase + 4));
                    e.Byte((byte)(sbyte)imm);
                    return;
                }
                X64Encoder.WriteRex(e, false, false, false, reg.IsExtended);
                e.Byte(0x80);
                X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(reg.Index & 7));
                e.Byte((byte)(sbyte)imm);
                return;
            }

            bool fitsI8 = imm >= -128 && imm <= 127;

            if (reg.Index == 0 && !reg.IsExtended && !fitsI8)
            {
                X64Encoder.WriteRex(e, size == RegSize.B64, false, false, false);
                e.Byte((byte)(regMemBase + 5));
                EmitImm(e, imm, size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
                return;
            }

            if (fitsI8 && size != RegSize.B8)
            {
                X64Encoder.WriteRex(e, size == RegSize.B64, false, false, reg.IsExtended);
                e.Byte(0x83);
                X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(reg.Index & 7));
                e.Byte((byte)(sbyte)imm);
                return;
            }

            X64Encoder.WriteRex(e, size == RegSize.B64, false, false, reg.IsExtended);
            e.Byte(0x81);
            X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(reg.Index & 7));
            EmitImm(e, imm, size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
        }

        private static void EmitArithImmMem(X64Emit e, X64Operand mem, long imm, int digit, List<LabelFixup> fixups, ParsedInstr ins)
        {
            RegSize size = MemSizeHintToSize(mem.MemSize, ins);
            EmitOperandSizePrefix(e, size);
            var (rexX, rexB) = X64Encoder.MemRexBits(mem);

            if (size == RegSize.B8)
            {
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Byte(0x80);
                X64Encoder.EmitMemOperand(e, (byte)digit, mem, fixups, 0, 0);
                e.Byte((byte)(sbyte)imm);
                return;
            }

            bool fitsI8 = imm >= -128 && imm <= 127;
            X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);

            if (fitsI8)
            {
                e.Byte(0x83);
                X64Encoder.EmitMemOperand(e, (byte)digit, mem, fixups, 0, 0);
                e.Byte((byte)(sbyte)imm);
                return;
            }

            e.Byte(0x81);
            X64Encoder.EmitMemOperand(e, (byte)digit, mem, fixups, 0, 0);
            EmitImm(e, imm, size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
        }

        private static void EmitMov(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, X64Operand dst, X64Operand src)
        {
            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                int opc = dst.Reg.Size == RegSize.B8 ? 0x88 : 0x89;
                EmitOperandSizePrefix(e, dst.Reg.Size);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                e.Byte((byte)opc);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                return;
            }

            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Register)
            {
                int opc = src.Reg.Size == RegSize.B8 ? 0x88 : 0x89;
                EmitOperandSizePrefix(e, src.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                e.Byte((byte)opc);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Memory)
            {
                int opc = dst.Reg.Size == RegSize.B8 ? 0x8A : 0x8B;
                EmitOperandSizePrefix(e, dst.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Byte((byte)opc);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Immediate)
            {
                var reg = dst.Reg;
                EmitOperandSizePrefix(e, reg.Size);

                if (reg.Size == RegSize.B8)
                {
                    X64Encoder.WriteRex(e, false, false, false, reg.IsExtended);
                    e.Byte((byte)(0xB0 + (reg.Index & 7)));
                    e.Byte((byte)(sbyte)src.Imm);
                    return;
                }

                if (reg.Size == RegSize.B64 && (src.Imm < int.MinValue || src.Imm > uint.MaxValue))
                {
                    X64Encoder.WriteRex(e, true, false, false, reg.IsExtended);
                    e.Byte((byte)(0xB8 + (reg.Index & 7)));
                    e.U64(unchecked((ulong)src.Imm));
                    return;
                }

                if (reg.Size == RegSize.B64 && src.Imm >= 0 && src.Imm <= uint.MaxValue)
                {
                    var dest32 = new RegRef(RegClass.Gpr, RegSize.B32, reg.Index);
                    X64Encoder.WriteRex(e, false, false, false, reg.IsExtended);
                    e.Byte((byte)(0xB8 + (reg.Index & 7)));
                    e.I32((int)src.Imm);
                    return;
                }

                if (reg.Size == RegSize.B64)
                {
                    X64Encoder.WriteRex(e, true, false, false, reg.IsExtended);
                    e.Byte(0xC7);
                    X64Encoder.WriteModRMReg(e, 0, (byte)(reg.Index & 7));
                    e.I32((int)src.Imm);
                    return;
                }

                X64Encoder.WriteRex(e, false, false, false, reg.IsExtended);
                e.Byte((byte)(0xB8 + (reg.Index & 7)));
                EmitImm(e, src.Imm, reg.Size);
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Label)
            {
                var reg = dst.Reg;
                if (reg.Size != RegSize.B64)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "mov with label requires 64-bit destination");

                X64Encoder.WriteRex(e, true, false, false, reg.IsExtended);
                e.Byte((byte)(0xB8 + (reg.Index & 7)));
                int pos = e.Position;
                e.U64(0);
                fixups.Add(new LabelFixup { Label = src.LabelName, Position = pos, Size = 8, InstrEnd = -1, IsAbsolute64 = true });
                return;
            }

            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Immediate)
            {
                RegSize size = MemSizeHintToSize(dst.MemSize, ins);
                EmitOperandSizePrefix(e, size);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                if (size == RegSize.B8)
                {
                    X64Encoder.WriteRex(e, false, false, rexX, rexB);
                    e.Byte(0xC6);
                    X64Encoder.EmitMemOperand(e, 0, dst, fixups, 0, 0);
                    e.Byte((byte)(sbyte)src.Imm);
                    return;
                }
                X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);
                e.Byte(0xC7);
                X64Encoder.EmitMemOperand(e, 0, dst, fixups, 0, 0);
                EmitImm(e, src.Imm, size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
                return;
            }

            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid operands for mov");
        }

        private static void EmitMovabs(ParsedInstr ins, X64Emit e)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || dst.Reg.Size != RegSize.B64 || src.Kind != OperandKind.Immediate)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "movabs requires r64, imm64");
            X64Encoder.WriteRex(e, true, false, false, dst.Reg.IsExtended);
            e.Byte((byte)(0xB8 + (dst.Reg.Index & 7)));
            e.U64(unchecked((ulong)src.Imm));
        }

        private static void EmitTest(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                int opc = dst.Reg.Size == RegSize.B8 ? 0x84 : 0x85;
                EmitOperandSizePrefix(e, dst.Reg.Size);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                e.Byte((byte)opc);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Immediate)
            {
                var reg = dst.Reg;
                EmitOperandSizePrefix(e, reg.Size);
                if (reg.Size == RegSize.B8)
                {
                    if (reg.Index == 0 && !reg.IsExtended) { e.Byte(0xA8); e.Byte((byte)(sbyte)src.Imm); return; }
                    X64Encoder.WriteRex(e, false, false, false, reg.IsExtended);
                    e.Byte(0xF6);
                    X64Encoder.WriteModRMReg(e, 0, (byte)(reg.Index & 7));
                    e.Byte((byte)(sbyte)src.Imm);
                    return;
                }
                if (reg.Index == 0 && !reg.IsExtended)
                {
                    X64Encoder.WriteRex(e, reg.Size == RegSize.B64, false, false, false);
                    e.Byte(0xA9);
                    EmitImm(e, src.Imm, reg.Size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
                    return;
                }
                X64Encoder.WriteRex(e, reg.Size == RegSize.B64, false, false, reg.IsExtended);
                e.Byte(0xF7);
                X64Encoder.WriteModRMReg(e, 0, (byte)(reg.Index & 7));
                EmitImm(e, src.Imm, reg.Size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
                return;
            }

            if (dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Register)
            {
                int opc = src.Reg.Size == RegSize.B8 ? 0x84 : 0x85;
                EmitOperandSizePrefix(e, src.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                e.Byte((byte)opc);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }

            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid operands for test");
        }

        private static void EmitUnary(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int digit)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Register)
            {
                var reg = op.Reg;
                EmitOperandSizePrefix(e, reg.Size);
                X64Encoder.WriteRex(e, reg.Size == RegSize.B64, false, false, reg.IsExtended);
                e.Byte(reg.Size == RegSize.B8 ? (byte)0xFE : (byte)0xFF);
                X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(reg.Index & 7));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var size = MemSizeHintToSize(op.MemSize, ins);
                EmitOperandSizePrefix(e, size);
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);
                e.Byte(size == RegSize.B8 ? (byte)0xFE : (byte)0xFF);
                X64Encoder.EmitMemOperand(e, (byte)digit, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operand for {ins.Mnemonic}");
        }

        private static void EmitUnaryGroup3(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int digit)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Register)
            {
                var reg = op.Reg;
                EmitOperandSizePrefix(e, reg.Size);
                X64Encoder.WriteRex(e, reg.Size == RegSize.B64, false, false, reg.IsExtended);
                e.Byte(reg.Size == RegSize.B8 ? (byte)0xF6 : (byte)0xF7);
                X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(reg.Index & 7));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var size = MemSizeHintToSize(op.MemSize, ins);
                EmitOperandSizePrefix(e, size);
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);
                e.Byte(size == RegSize.B8 ? (byte)0xF6 : (byte)0xF7);
                X64Encoder.EmitMemOperand(e, (byte)digit, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operand for {ins.Mnemonic}");
        }

        private static void EmitImul(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            if (ins.Operands.Count == 1)
            {
                EmitUnaryGroup3(ins, e, fixups, 5);
                return;
            }
            if (ins.Operands.Count == 2)
            {
                var dst = ins.Operands[0];
                var src = ins.Operands[1];
                if (dst.Kind != OperandKind.Register)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "imul 2-operand requires reg dest");
                EmitOperandSizePrefix(e, dst.Reg.Size);
                if (src.Kind == OperandKind.Register)
                {
                    EnsureSameSize(ins, dst.Reg, src.Reg);
                    X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                    e.Bytes(0x0F, 0xAF);
                    X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                    return;
                }
                if (src.Kind == OperandKind.Memory)
                {
                    var (rexX, rexB) = X64Encoder.MemRexBits(src);
                    X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                    e.Bytes(0x0F, 0xAF);
                    X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                    return;
                }
            }
            if (ins.Operands.Count == 3)
            {
                var dst = ins.Operands[0];
                var src = ins.Operands[1];
                var imm = ins.Operands[2];
                if (dst.Kind != OperandKind.Register || src.Kind != OperandKind.Register || imm.Kind != OperandKind.Immediate)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "imul 3-op requires reg,reg,imm");
                EnsureSameSize(ins, dst.Reg, src.Reg);
                EmitOperandSizePrefix(e, dst.Reg.Size);
                bool fitsI8 = imm.Imm >= -128 && imm.Imm <= 127;
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Byte(fitsI8 ? (byte)0x6B : (byte)0x69);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                if (fitsI8) e.Byte((byte)(sbyte)imm.Imm);
                else EmitImm(e, imm.Imm, dst.Reg.Size == RegSize.B16 ? RegSize.B16 : RegSize.B32);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid imul form");
        }

        private static void EmitPush(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Register)
            {
                if (op.Reg.Size != RegSize.B64 && op.Reg.Size != RegSize.B16)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "push requires r64 or r16");
                if (op.Reg.Size == RegSize.B16) e.Byte(0x66);
                if (op.Reg.IsExtended) e.Byte(0x41);
                e.Byte((byte)(0x50 + (op.Reg.Index & 7)));
                return;
            }
            if (op.Kind == OperandKind.Immediate)
            {
                if (op.Imm >= -128 && op.Imm <= 127) { e.Byte(0x6A); e.Byte((byte)(sbyte)op.Imm); return; }
                e.Byte(0x68); e.I32((int)op.Imm); return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Byte(0xFF);
                X64Encoder.EmitMemOperand(e, 6, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid push");
        }

        private static void EmitPop(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Register)
            {
                if (op.Reg.Size != RegSize.B64 && op.Reg.Size != RegSize.B16)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "pop requires r64 or r16");
                if (op.Reg.Size == RegSize.B16) e.Byte(0x66);
                if (op.Reg.IsExtended) e.Byte(0x41);
                e.Byte((byte)(0x58 + (op.Reg.Index & 7)));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Byte(0x8F);
                X64Encoder.EmitMemOperand(e, 0, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid pop");
        }

        private static void EmitShift(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int digit)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            bool isCl = src.Kind == OperandKind.Register && X64Registers.TryParse("cl", out var clReg) && src.Reg.Class == RegClass.Gpr && src.Reg.Size == RegSize.B8 && src.Reg.Index == 1 && !src.Reg.IsHighByte;

            if (dst.Kind == OperandKind.Register)
            {
                EmitOperandSizePrefix(e, dst.Reg.Size);
                if (isCl)
                {
                    X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, false, false, dst.Reg.IsExtended);
                    e.Byte(dst.Reg.Size == RegSize.B8 ? (byte)0xD2 : (byte)0xD3);
                    X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(dst.Reg.Index & 7));
                    return;
                }
                if (src.Kind != OperandKind.Immediate)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "shift count must be cl or imm8");
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, false, false, dst.Reg.IsExtended);
                if (src.Imm == 1)
                {
                    e.Byte(dst.Reg.Size == RegSize.B8 ? (byte)0xD0 : (byte)0xD1);
                    X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(dst.Reg.Index & 7));
                    return;
                }
                e.Byte(dst.Reg.Size == RegSize.B8 ? (byte)0xC0 : (byte)0xC1);
                X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(dst.Reg.Index & 7));
                e.Byte((byte)(src.Imm & 0x3F));
                return;
            }

            if (dst.Kind == OperandKind.Memory)
            {
                var size = MemSizeHintToSize(dst.MemSize, ins);
                EmitOperandSizePrefix(e, size);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);
                if (isCl)
                {
                    e.Byte(size == RegSize.B8 ? (byte)0xD2 : (byte)0xD3);
                    X64Encoder.EmitMemOperand(e, (byte)digit, dst, fixups, 0, 0);
                    return;
                }
                if (src.Kind != OperandKind.Immediate)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "shift count must be cl or imm8");
                e.Byte(size == RegSize.B8 ? (byte)0xC0 : (byte)0xC1);
                X64Encoder.EmitMemOperand(e, (byte)digit, dst, fixups, 0, 0);
                e.Byte((byte)(src.Imm & 0x3F));
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid shift dst");
        }

        private static void EmitLea(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || src.Kind != OperandKind.Memory)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "lea requires reg, mem");
            if (dst.Reg.Size == RegSize.B8)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "lea dest must be 16/32/64-bit");
            EmitOperandSizePrefix(e, dst.Reg.Size);
            var (rexX, rexB) = X64Encoder.MemRexBits(src);
            X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
            e.Byte(0x8D);
            X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
        }

        private static void EmitRet(ParsedInstr ins, X64Emit e)
        {
            if (ins.Operands.Count == 0) { e.Byte(0xC3); return; }
            if (ins.Operands.Count == 1 && ins.Operands[0].Kind == OperandKind.Immediate)
            {
                e.Byte(0xC2);
                e.U16((ushort)(ins.Operands[0].Imm & 0xFFFF));
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid ret");
        }

        private static void EmitJmp(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Label)
            {
                e.Byte(0xE9);
                int pos = e.Position;
                e.I32(0);
                fixups.Add(new LabelFixup { Label = op.LabelName, Position = pos, Size = 4, InstrEnd = -1 });
                return;
            }
            if (op.Kind == OperandKind.Register)
            {
                if (op.Reg.Size != RegSize.B64)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "indirect jmp requires r64");
                X64Encoder.WriteRex(e, false, false, false, op.Reg.IsExtended);
                e.Byte(0xFF);
                X64Encoder.WriteModRMReg(e, 4, (byte)(op.Reg.Index & 7));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Byte(0xFF);
                X64Encoder.EmitMemOperand(e, 4, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid jmp");
        }

        private static void EmitCall(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind == OperandKind.Label)
            {
                e.Byte(0xE8);
                int pos = e.Position;
                e.I32(0);
                fixups.Add(new LabelFixup { Label = op.LabelName, Position = pos, Size = 4, InstrEnd = -1 });
                return;
            }
            if (op.Kind == OperandKind.Register)
            {
                if (op.Reg.Size != RegSize.B64)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "indirect call requires r64");
                X64Encoder.WriteRex(e, false, false, false, op.Reg.IsExtended);
                e.Byte(0xFF);
                X64Encoder.WriteModRMReg(e, 2, (byte)(op.Reg.Index & 7));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Byte(0xFF);
                X64Encoder.EmitMemOperand(e, 2, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid call");
        }

        private static readonly Dictionary<string, byte> _jccCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            { "jo",  0x80 }, { "jno", 0x81 }, { "jb",  0x82 }, { "jc",  0x82 }, { "jnae", 0x82 },
            { "jae", 0x83 }, { "jnb", 0x83 }, { "jnc", 0x83 },
            { "je",  0x84 }, { "jz",  0x84 }, { "jne", 0x85 }, { "jnz", 0x85 },
            { "jbe", 0x86 }, { "jna", 0x86 }, { "ja",  0x87 }, { "jnbe", 0x87 },
            { "js",  0x88 }, { "jns", 0x89 }, { "jp",  0x8A }, { "jpe", 0x8A },
            { "jnp", 0x8B }, { "jpo", 0x8B }, { "jl",  0x8C }, { "jnge", 0x8C },
            { "jge", 0x8D }, { "jnl", 0x8D }, { "jle", 0x8E }, { "jng", 0x8E },
            { "jg",  0x8F }, { "jnle", 0x8F }
        };

        private static bool IsJccMnemonic(string m) => _jccCodes.ContainsKey(m);

        private static void EmitJcc(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind != OperandKind.Label)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires a label");
            byte code = _jccCodes[ins.Mnemonic];
            e.Bytes(0x0F, code);
            int pos = e.Position;
            e.I32(0);
            fixups.Add(new LabelFixup { Label = op.LabelName, Position = pos, Size = 4, InstrEnd = -1 });
        }

        private static readonly Dictionary<string, byte> _setccCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            { "seto",  0x90 }, { "setno", 0x91 }, { "setb",  0x92 }, { "setc",  0x92 }, { "setnae", 0x92 },
            { "setae", 0x93 }, { "setnb", 0x93 }, { "setnc", 0x93 },
            { "sete",  0x94 }, { "setz",  0x94 }, { "setne", 0x95 }, { "setnz", 0x95 },
            { "setbe", 0x96 }, { "setna", 0x96 }, { "seta",  0x97 }, { "setnbe", 0x97 },
            { "sets",  0x98 }, { "setns", 0x99 }, { "setp",  0x9A }, { "setpe", 0x9A },
            { "setnp", 0x9B }, { "setpo", 0x9B }, { "setl",  0x9C }, { "setnge", 0x9C },
            { "setge", 0x9D }, { "setnl", 0x9D }, { "setle", 0x9E }, { "setng", 0x9E },
            { "setg",  0x9F }, { "setnle", 0x9F }
        };

        private static bool IsSetccMnemonic(string m) => _setccCodes.ContainsKey(m);

        private static void EmitSetcc(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            byte code = _setccCodes[ins.Mnemonic];
            if (op.Kind == OperandKind.Register)
            {
                if (op.Reg.Size != RegSize.B8)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires r8");
                X64Encoder.WriteRex(e, false, false, false, op.Reg.IsExtended);
                e.Bytes(0x0F, code);
                X64Encoder.WriteModRMReg(e, 0, (byte)(op.Reg.Index & 7));
                return;
            }
            if (op.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(op);
                X64Encoder.WriteRex(e, false, false, rexX, rexB);
                e.Bytes(0x0F, code);
                X64Encoder.EmitMemOperand(e, 0, op, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operand for {ins.Mnemonic}");
        }

        private static void EmitMovzxMovsx(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, bool signed, bool isMovsxd)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            if (dst.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be reg");
            if (dst.Reg.Size == RegSize.B8)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be >=16 bits");

            if (isMovsxd)
            {
                if (dst.Reg.Size != RegSize.B64)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "movsxd dst must be 64-bit");
                EmitOperandSizePrefix(e, dst.Reg.Size);
                if (src.Kind == OperandKind.Register)
                {
                    if (src.Reg.Size != RegSize.B32)
                        throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "movsxd src must be 32-bit");
                    X64Encoder.WriteRex(e, true, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                    e.Byte(0x63);
                    X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                    return;
                }
                if (src.Kind == OperandKind.Memory)
                {
                    var (rexX, rexB) = X64Encoder.MemRexBits(src);
                    X64Encoder.WriteRex(e, true, dst.Reg.IsExtended, rexX, rexB);
                    e.Byte(0x63);
                    X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                    return;
                }
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid movsxd src");
            }

            RegSize srcSize;
            if (src.Kind == OperandKind.Register) srcSize = src.Reg.Size;
            else if (src.Kind == OperandKind.Memory) srcSize = MemSizeHintToSize(src.MemSize, ins);
            else throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid {ins.Mnemonic} src");

            byte op2;
            if (srcSize == RegSize.B8) op2 = signed ? (byte)0xBE : (byte)0xB6;
            else if (srcSize == RegSize.B16) op2 = signed ? (byte)0xBF : (byte)0xB7;
            else throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} src must be 8 or 16 bits");

            EmitOperandSizePrefix(e, dst.Reg.Size);
            if (src.Kind == OperandKind.Register)
            {
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
            }
            else
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
            }
        }

        private static void EmitSse2(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix, byte opRm, byte opMr, bool allowBoth)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            if (prefix != 0) e.Byte(prefix);

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                EnsureXmm(ins, dst.Reg);
                EnsureXmm(ins, src.Reg);
                X64Encoder.WriteRex(e, false, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, opRm);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }

            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Memory)
            {
                EnsureXmm(ins, dst.Reg);
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, false, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, opRm);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }

            if (allowBoth && dst.Kind == OperandKind.Memory && src.Kind == OperandKind.Register)
            {
                EnsureXmm(ins, src.Reg);
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, false, src.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, opMr);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }

            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitSse2Binary(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix, byte op2)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (prefix != 0) e.Byte(prefix);
            EnsureXmm(ins, dst.Reg);
            if (src.Kind == OperandKind.Register)
            {
                EnsureXmm(ins, src.Reg);
                X64Encoder.WriteRex(e, false, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, false, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitCvtSi2(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            EnsureXmm(ins, dst.Reg);
            e.Byte(prefix);
            if (src.Kind == OperandKind.Register)
            {
                if (src.Reg.Class != RegClass.Gpr || (src.Reg.Size != RegSize.B32 && src.Reg.Size != RegSize.B64))
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "cvtsi2sd/ss src must be r32 or r64");
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, 0x2A);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, src.MemSize == MemSizeHint.Qword, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, 0x2A);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitCvt2Si(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix, byte op2)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Gpr)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be r32/r64");
            if (dst.Reg.Size != RegSize.B32 && dst.Reg.Size != RegSize.B64)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be r32 or r64");
            e.Byte(prefix);
            if (src.Kind == OperandKind.Register)
            {
                EnsureXmm(ins, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitMovd(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, bool q)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            e.Byte(0x66);
            if (dst.Kind == OperandKind.Register && src.Kind == OperandKind.Register)
            {
                if (dst.Reg.Class == RegClass.Xmm && src.Reg.Class == RegClass.Gpr)
                {
                    X64Encoder.WriteRex(e, q, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                    e.Bytes(0x0F, 0x6E);
                    X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                    return;
                }
                if (dst.Reg.Class == RegClass.Gpr && src.Reg.Class == RegClass.Xmm)
                {
                    X64Encoder.WriteRex(e, q, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                    e.Bytes(0x0F, 0x7E);
                    X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                    return;
                }
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitBswap(ParsedInstr ins, X64Emit e)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind != OperandKind.Register || op.Reg.Class != RegClass.Gpr || (op.Reg.Size != RegSize.B32 && op.Reg.Size != RegSize.B64))
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "bswap requires r32/r64");
            X64Encoder.WriteRex(e, op.Reg.Size == RegSize.B64, false, false, op.Reg.IsExtended);
            e.Bytes(0x0F, (byte)(0xC8 + (op.Reg.Index & 7)));
        }

        private static void EmitData(ParsedInstr ins, X64Emit e, int size)
        {
            if (ins.Operands.Count == 0)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"d{size}: requires data");
            foreach (var op in ins.Operands)
            {
                if (op.Kind == OperandKind.StringLiteral)
                {
                    if (size != 1) throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"string literals only valid for db, got d{size}");
                    foreach (var b in op.StringBytes) e.Byte(b);
                    continue;
                }
                if (op.Kind != OperandKind.Immediate)
                    throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "data directive requires immediates or string");
                long v = op.Imm;
                switch (size)
                {
                    case 1: e.Byte((byte)(v & 0xff)); break;
                    case 2: e.U16((ushort)(v & 0xffff)); break;
                    case 4: e.U32((uint)(v & 0xffffffff)); break;
                    case 8: e.U64(unchecked((ulong)v)); break;
                }
            }
        }

        private static void EmitReserve(ParsedInstr ins, X64Emit e, int unit)
        {
            ExpectOperands(ins, 1);
            if (ins.Operands[0].Kind != OperandKind.Immediate)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "res* requires count");
            long n = ins.Operands[0].Imm * unit;
            for (long k = 0; k < n; k++) e.Byte(0);
        }

        private static void EmitAlign(ParsedInstr ins, X64Emit e)
        {
            ExpectOperands(ins, 1);
            int align = (int)ins.Operands[0].Imm;
            if (align <= 0 || (align & (align - 1)) != 0)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "align must be a power of two");
            while ((e.Position & (align - 1)) != 0) e.Byte(0x90);
        }

        private static void EmitTimes(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "times must be lowered at parse stage");
        }

        private static void EmitXchg(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var a = ins.Operands[0];
            var b = ins.Operands[1];

            if (a.Kind == OperandKind.Register && b.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, a.Reg, b.Reg);
                EmitOperandSizePrefix(e, a.Reg.Size);
                if (a.Reg.Size != RegSize.B8 && (a.Reg.Index == 0 || b.Reg.Index == 0) && a.Reg.Size != RegSize.B16)
                {
                    var other = a.Reg.Index == 0 ? b.Reg : a.Reg;
                    if (other.Index != 0 || other.IsExtended)
                    {
                        X64Encoder.WriteRex(e, a.Reg.Size == RegSize.B64, false, false, other.IsExtended);
                        e.Byte((byte)(0x90 + (other.Index & 7)));
                        return;
                    }
                }
                X64Encoder.WriteRex(e, a.Reg.Size == RegSize.B64, a.Reg.IsExtended, false, b.Reg.IsExtended);
                e.Byte(a.Reg.Size == RegSize.B8 ? (byte)0x86 : (byte)0x87);
                X64Encoder.WriteModRMReg(e, (byte)(a.Reg.Index & 7), (byte)(b.Reg.Index & 7));
                return;
            }

            if (a.Kind == OperandKind.Memory && b.Kind == OperandKind.Register)
            {
                EmitOperandSizePrefix(e, b.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(a);
                X64Encoder.WriteRex(e, b.Reg.Size == RegSize.B64, b.Reg.IsExtended, rexX, rexB);
                e.Byte(b.Reg.Size == RegSize.B8 ? (byte)0x86 : (byte)0x87);
                X64Encoder.EmitMemOperand(e, (byte)(b.Reg.Index & 7), a, fixups, 0, 0);
                return;
            }
            if (a.Kind == OperandKind.Register && b.Kind == OperandKind.Memory)
            {
                EmitOperandSizePrefix(e, a.Reg.Size);
                var (rexX, rexB) = X64Encoder.MemRexBits(b);
                X64Encoder.WriteRex(e, a.Reg.Size == RegSize.B64, a.Reg.IsExtended, rexX, rexB);
                e.Byte(a.Reg.Size == RegSize.B8 ? (byte)0x86 : (byte)0x87);
                X64Encoder.EmitMemOperand(e, (byte)(a.Reg.Index & 7), b, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid operands for xchg");
        }

        private static void EmitXadd(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (src.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "xadd src must be register");
            byte op2 = src.Reg.Size == RegSize.B8 ? (byte)0xC0 : (byte)0xC1;
            EmitOperandSizePrefix(e, src.Reg.Size);
            if (dst.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                return;
            }
            if (dst.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid operands for xadd");
        }

        private static void EmitCmpxchg(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (src.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "cmpxchg src must be register");
            byte op2 = src.Reg.Size == RegSize.B8 ? (byte)0xB0 : (byte)0xB1;
            EmitOperandSizePrefix(e, src.Reg.Size);
            if (dst.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                return;
            }
            if (dst.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "invalid operands for cmpxchg");
        }

        private static void EmitCmpxchgN(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, bool sixteen)
        {
            ExpectOperands(ins, 1);
            var mem = ins.Operands[0];
            if (mem.Kind != OperandKind.Memory)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "cmpxchg8b/16b requires memory operand");
            var (rexX, rexB) = X64Encoder.MemRexBits(mem);
            X64Encoder.WriteRex(e, sixteen, false, rexX, rexB);
            e.Bytes(0x0F, 0xC7);
            X64Encoder.EmitMemOperand(e, 1, mem, fixups, 0, 0);
        }

        private static void EmitBitTest(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int digitOrSubop)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];

            if (src.Kind == OperandKind.Immediate)
            {
                byte modrmDigit = digitOrSubop == 4 ? (byte)4 : digitOrSubop == 5 ? (byte)5 : digitOrSubop == 6 ? (byte)6 : (byte)7;
                if (dst.Kind == OperandKind.Register)
                {
                    EmitOperandSizePrefix(e, dst.Reg.Size);
                    X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, false, false, dst.Reg.IsExtended);
                    e.Bytes(0x0F, 0xBA);
                    X64Encoder.WriteModRMReg(e, modrmDigit, (byte)(dst.Reg.Index & 7));
                    e.Byte((byte)(src.Imm & 0xff));
                    return;
                }
                if (dst.Kind == OperandKind.Memory)
                {
                    var size = MemSizeHintToSize(dst.MemSize, ins);
                    EmitOperandSizePrefix(e, size);
                    var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                    X64Encoder.WriteRex(e, size == RegSize.B64, false, rexX, rexB);
                    e.Bytes(0x0F, 0xBA);
                    X64Encoder.EmitMemOperand(e, modrmDigit, dst, fixups, 0, 0);
                    e.Byte((byte)(src.Imm & 0xff));
                    return;
                }
            }
            if (src.Kind == OperandKind.Register)
            {
                byte op2 = digitOrSubop switch { 4 => 0xA3, 5 => 0xAB, 6 => 0xB3, 7 => 0xBB, _ => 0xA3 };
                EmitOperandSizePrefix(e, src.Reg.Size);
                if (dst.Kind == OperandKind.Register)
                {
                    EnsureSameSize(ins, dst.Reg, src.Reg);
                    X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                    e.Bytes(0x0F, op2);
                    X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                    return;
                }
                if (dst.Kind == OperandKind.Memory)
                {
                    var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                    X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                    e.Bytes(0x0F, op2);
                    X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                    return;
                }
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitBsfBsr(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte op2)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "bsf/bsr dst must be reg");
            EmitOperandSizePrefix(e, dst.Reg.Size);
            if (src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitPopcnt(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix, byte op2)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be reg");
            e.Byte(prefix);
            EmitOperandSizePrefix(e, dst.Reg.Size);
            if (src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitClflush(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 1);
            var mem = ins.Operands[0];
            if (mem.Kind != OperandKind.Memory)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "clflush requires memory operand");
            var (rexX, rexB) = X64Encoder.MemRexBits(mem);
            X64Encoder.WriteRex(e, false, false, rexX, rexB);
            e.Bytes(0x0F, 0xAE);
            X64Encoder.EmitMemOperand(e, 7, mem, fixups, 0, 0);
        }

        private static void EmitPrefetch(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int hint)
        {
            ExpectOperands(ins, 1);
            var mem = ins.Operands[0];
            if (mem.Kind != OperandKind.Memory)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "prefetch requires memory operand");
            var (rexX, rexB) = X64Encoder.MemRexBits(mem);
            X64Encoder.WriteRex(e, false, false, rexX, rexB);
            e.Bytes(0x0F, 0x18);
            X64Encoder.EmitMemOperand(e, (byte)hint, mem, fixups, 0, 0);
        }

        private static void EmitGroup15(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, int digit)
        {
            ExpectOperands(ins, 1);
            var op = ins.Operands[0];
            if (op.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} requires register dest");
            EmitOperandSizePrefix(e, op.Reg.Size);
            X64Encoder.WriteRex(e, op.Reg.Size == RegSize.B64, false, false, op.Reg.IsExtended);
            e.Bytes(0x0F, 0xC7);
            X64Encoder.WriteModRMReg(e, (byte)digit, (byte)(op.Reg.Index & 7));
        }

        public static readonly Dictionary<string, byte> KnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            { "lock", 0xF0 },
            { "rep",   0xF3 },
            { "repe",  0xF3 },
            { "repz",  0xF3 },
            { "repne", 0xF2 },
            { "repnz", 0xF2 },
        };

        public static readonly string[] AllKnownMnemonics = new[]
        {
            "mov","movabs","movzx","movsx","movsxd","add","sub","or","and","xor","cmp","test","adc","sbb",
            "inc","dec","neg","not","mul","imul","div","idiv","push","pop","shl","shr","sar","sal","rol","ror","rcl","rcr",
            "lea","ret","retn","leave","nop","int3","syscall","cdq","cqo","cwd","jmp","call",
            "movsd","movss","addsd","subsd","mulsd","divsd","sqrtsd","ucomisd","comisd","ucomiss","comiss",
            "addss","subss","mulss","divss","sqrtss","cvtsi2sd","cvtsi2ss","cvtsd2si","cvttsd2si","cvtss2si","cvttss2si","cvtsd2ss","cvtss2sd",
            "xorps","xorpd","andps","andpd","orps","orpd","movd","movq","db","dw","dd","dq","resb","resw","resd","resq","align","times","bswap",
            "xchg","xadd","cmpxchg","cmpxchg8b","cmpxchg16b","bt","bts","btr","btc","bsf","bsr","popcnt","lzcnt","tzcnt",
            "mfence","lfence","sfence","pause","clflush","prefetcht0","prefetcht1","prefetcht2","prefetchnta",
            "rdtsc","rdtscp","cpuid","rdrand","rdseed","movsb","movsw","movsq","stosb","stosw","stosd","stosq","lodsb","lodsw","lodsd","lodsq","cmpsb","cmpsw","cmpsq","scasb","scasw","scasd","scasq",
            "rep","repe","repz","repne","repnz","lock",
            "movups","movupd","movaps","movapd","movdqa","movdqu","addps","subps","mulps","divps","addpd","subpd","mulpd","divpd","minps","maxps","minpd","maxpd",
            "paddb","paddw","paddd","paddq","psubb","psubw","psubd","psubq","pand","pandn","por","pxor","pcmpeqb","pcmpeqw","pcmpeqd","pcmpgtb","pcmpgtw","pcmpgtd",
            "pmovmskb","movmskps","movmskpd",
            "cmovne","cmovnz","cmove","cmovz","cmovl","cmovle","cmovg","cmovge","cmovb","cmovc","cmovnae","cmovae","cmovnb","cmovnc","cmovbe","cmovna","cmova","cmovnbe","cmovs","cmovns","cmovo","cmovno","cmovp","cmovpe","cmovnp","cmovpo",
            "shld","shrd","clc","stc","cmc","cld","std","pushfq","popfq","lahf","sahf","endbr64","endbr32","ud2","ud1","wait","fwait","andn",
            "vaddps","vaddpd","vsubps","vsubpd","vmulps","vmulpd","vdivps","vdivpd","vminps","vminpd","vmaxps","vmaxpd","vandps","vandpd","vorps","vorpd","vxorps","vxorpd",
            "vpaddb","vpaddw","vpaddd","vpaddq","vpsubb","vpsubw","vpsubd","vpsubq","vpand","vpandn","vpor","vpxor",
            "vmovups","vmovupd","vmovaps","vmovapd","vmovdqa","vmovdqu","vaddsd","vaddss","vsubsd","vmulsd","vdivsd","vsqrtsd",
            "vfmadd213sd","vfmadd213ss","vfmadd231sd","vfmadd231ss","vfmadd132sd","vbroadcastss","vbroadcastsd",
            "bzhi","pdep","pext","mulx","shlx","sarx","shrx",
            "je","jne","jl","jg","jle","jge","jb","ja","jae","jbe","jc","jnc","jz","jnz","jo","jno","js","jns","jp","jnp","jna","jnae","jnb","jnbe","jng","jnge","jnl","jnle","jpe","jpo",
        };

        private static string DidYouMean(string mnemonic)
        {
            int best = int.MaxValue;
            string suggestion = "";
            foreach (var m in AllKnownMnemonics)
            {
                int d = Levenshtein(mnemonic, m);
                if (d < best) { best = d; suggestion = m; }
            }
            if (best <= 2) return $"did you mean '{suggestion}'?";
            return "";
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[m];
        }

        private static void EmitPmovmskb(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Gpr)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "pmovmskb dst must be GPR");
            if (src.Kind != OperandKind.Register || src.Reg.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "pmovmskb src must be XMM");
            e.Byte(0x66);
            X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
            e.Bytes(0x0F, 0xD7);
            X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
        }

        private static void EmitMovmsk(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte prefix)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register || dst.Reg.Class != RegClass.Gpr)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "movmskps/pd dst must be GPR");
            if (src.Kind != OperandKind.Register || src.Reg.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "movmskps/pd src must be XMM");
            if (prefix != 0) e.Byte(prefix);
            X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
            e.Bytes(0x0F, 0x50);
            X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
        }

        private static void EmitCmov(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte op2)
        {
            ExpectOperands(ins, 2);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            if (dst.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} dst must be reg");
            EmitOperandSizePrefix(e, dst.Reg.Size);
            if (src.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, false, src.Reg.IsExtended);
                e.Bytes(0x0F, op2);
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src.Reg.Index & 7));
                return;
            }
            if (src.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(src);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, dst.Reg.IsExtended, rexX, rexB);
                e.Bytes(0x0F, op2);
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src, fixups, 0, 0);
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitShldShrd(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups, byte opImm, byte opCl)
        {
            ExpectOperands(ins, 3);
            var dst = ins.Operands[0];
            var src = ins.Operands[1];
            var amt = ins.Operands[2];
            if (src.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "shld/shrd 2nd op must be register");
            EmitOperandSizePrefix(e, src.Reg.Size);
            if (dst.Kind == OperandKind.Register)
            {
                EnsureSameSize(ins, dst.Reg, src.Reg);
                X64Encoder.WriteRex(e, dst.Reg.Size == RegSize.B64, src.Reg.IsExtended, false, dst.Reg.IsExtended);
                if (amt.Kind == OperandKind.Register)
                {
                    e.Bytes(0x0F, opCl);
                    X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                    return;
                }
                e.Bytes(0x0F, opImm);
                X64Encoder.WriteModRMReg(e, (byte)(src.Reg.Index & 7), (byte)(dst.Reg.Index & 7));
                e.Byte((byte)(amt.Imm & 0x3F));
                return;
            }
            if (dst.Kind == OperandKind.Memory)
            {
                var (rexX, rexB) = X64Encoder.MemRexBits(dst);
                X64Encoder.WriteRex(e, src.Reg.Size == RegSize.B64, src.Reg.IsExtended, rexX, rexB);
                if (amt.Kind == OperandKind.Register)
                {
                    e.Bytes(0x0F, opCl);
                    X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                    return;
                }
                e.Bytes(0x0F, opImm);
                X64Encoder.EmitMemOperand(e, (byte)(src.Reg.Index & 7), dst, fixups, 0, 0);
                e.Byte((byte)(amt.Imm & 0x3F));
                return;
            }
            throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"invalid operands for {ins.Mnemonic}");
        }

        private static void EmitBmiAndn(ParsedInstr ins, X64Emit e, List<LabelFixup> fixups)
        {
            ExpectOperands(ins, 3);
            var dst = ins.Operands[0];
            var src1 = ins.Operands[1];
            var src2 = ins.Operands[2];
            if (dst.Kind != OperandKind.Register || src1.Kind != OperandKind.Register)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "andn requires reg, reg, reg/mem");

            X64Vex.EmitVex(e, false, src1.Reg.Index, dst.Reg.Index, 0,
                src2.Kind == OperandKind.Register ? src2.Reg.Index : (byte)0,
                X64Vex.MapPp.None, X64Vex.MapMm.M0F38,
                dst.Reg.Size == RegSize.B64);
            e.Byte(0xF2);
            if (src2.Kind == OperandKind.Register)
                X64Encoder.WriteModRMReg(e, (byte)(dst.Reg.Index & 7), (byte)(src2.Reg.Index & 7));
            else if (src2.Kind == OperandKind.Memory)
                X64Encoder.EmitMemOperand(e, (byte)(dst.Reg.Index & 7), src2, fixups, 0, 0);
            else throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "andn 3rd op must be reg or mem");
        }

        private static void EmitImm(X64Emit e, long imm, RegSize size)
        {
            switch (size)
            {
                case RegSize.B8: e.Byte((byte)(sbyte)imm); break;
                case RegSize.B16: e.U16((ushort)imm); break;
                case RegSize.B32: e.U32(unchecked((uint)imm)); break;
                case RegSize.B64: e.U64(unchecked((ulong)imm)); break;
                default: throw new InvalidOperationException();
            }
        }

        private static void EmitOperandSizePrefix(X64Emit e, RegSize size)
        {
            if (size == RegSize.B16) e.Byte(0x66);
        }

        private static void EnsureSameSize(ParsedInstr ins, RegRef a, RegRef b)
        {
            if (a.Class != b.Class || a.Size != b.Size)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "operand size mismatch");
        }

        private static void EnsureXmm(ParsedInstr ins, RegRef r)
        {
            if (r.Class != RegClass.Xmm)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "xmm register expected");
        }

        private static void ExpectOperands(ParsedInstr ins, int count)
        {
            if (ins.Operands.Count != count)
                throw new AsmAssembleException(ins.LineNumber, ins.RawLine, $"{ins.Mnemonic} expects {count} operand(s), got {ins.Operands.Count}");
        }

        private static RegSize MemSizeHintToSize(MemSizeHint hint, ParsedInstr ins)
        {
            return hint switch
            {
                MemSizeHint.Byte => RegSize.B8,
                MemSizeHint.Word => RegSize.B16,
                MemSizeHint.Dword => RegSize.B32,
                MemSizeHint.Qword => RegSize.B64,
                _ => throw new AsmAssembleException(ins.LineNumber, ins.RawLine, "ambiguous memory operand size — use byte/word/dword/qword ptr")
            };
        }
    }
}

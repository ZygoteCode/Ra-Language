using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    internal sealed class X64Emit
    {
        private readonly List<byte> _bytes = new List<byte>(256);
        public int Position => _bytes.Count;
        public byte[] ToArray() => _bytes.ToArray();
        public void Patch(int pos, byte b) => _bytes[pos] = b;

        public void Byte(byte b) => _bytes.Add(b);
        public void Bytes(params byte[] bs) { foreach (var b in bs) _bytes.Add(b); }
        public void U16(ushort v) { _bytes.Add((byte)(v & 0xff)); _bytes.Add((byte)((v >> 8) & 0xff)); }
        public void U32(uint v) { _bytes.Add((byte)(v & 0xff)); _bytes.Add((byte)((v >> 8) & 0xff)); _bytes.Add((byte)((v >> 16) & 0xff)); _bytes.Add((byte)((v >> 24) & 0xff)); }
        public void U64(ulong v) { U32((uint)(v & 0xffffffff)); U32((uint)(v >> 32)); }
        public void I32(int v) => U32(unchecked((uint)v));

        public void PatchI32At(int pos, int v)
        {
            uint u = unchecked((uint)v);
            _bytes[pos]     = (byte)(u & 0xff);
            _bytes[pos + 1] = (byte)((u >> 8) & 0xff);
            _bytes[pos + 2] = (byte)((u >> 16) & 0xff);
            _bytes[pos + 3] = (byte)((u >> 24) & 0xff);
        }

        public void PatchU64At(int pos, ulong v)
        {
            for (int i = 0; i < 8; i++) _bytes[pos + i] = (byte)((v >> (i * 8)) & 0xff);
        }
    }

    internal sealed class LabelFixup
    {
        public string Label = "";
        public int Position;
        public int Size;
        public int InstrEnd;
        public bool IsAbsolute64;
    }

    internal static class X64Encoder
    {
        public static void WriteRex(X64Emit e, bool w, bool r, bool x, bool b)
        {
            byte rex = 0x40;
            if (w) rex |= 0x08;
            if (r) rex |= 0x04;
            if (x) rex |= 0x02;
            if (b) rex |= 0x01;
            if (rex != 0x40) e.Byte(rex);
        }

        public static void WriteModRMReg(X64Emit e, byte regField, byte rmField)
        {
            byte b = (byte)((3 << 6) | ((regField & 7) << 3) | (rmField & 7));
            e.Byte(b);
        }

        public static void EmitMemOperand(X64Emit e, byte regField, X64Operand mem, List<LabelFixup> fixups, int instrStart, int dispStart)
        {
            if (mem.MemIsRipRelative)
            {
                e.Byte((byte)(((regField & 7) << 3) | 0x05));
                int dispPos = e.Position;
                e.I32(0);
                if (mem.MemRipLabel != null)
                {
                    fixups.Add(new LabelFixup { Label = mem.MemRipLabel, Position = dispPos, Size = 4, InstrEnd = -1 });
                }
                else
                {
                    e.PatchI32At(dispPos, (int)mem.MemDisp);
                }
                return;
            }

            bool hasIndex = mem.HasMemIndex;
            bool baseIsRSP_R12 = mem.HasMemBase && (mem.MemBase.Index & 7) == 4;
            bool needSib = hasIndex || baseIsRSP_R12 || !mem.HasMemBase;

            bool baseIsRBP_R13 = mem.HasMemBase && (mem.MemBase.Index & 7) == 5;

            int dispSize;
            byte mod;

            if (!mem.HasMemBase && !hasIndex)
            {
                mod = 0;
                dispSize = 4;
            }
            else if (mem.MemDisp == 0 && !baseIsRBP_R13)
            {
                mod = 0;
                dispSize = 0;
            }
            else if (mem.MemDisp >= -128 && mem.MemDisp <= 127)
            {
                mod = 1;
                dispSize = 1;
            }
            else
            {
                mod = 2;
                dispSize = 4;
            }

            byte rm;
            if (needSib) rm = 4;
            else rm = (byte)(mem.MemBase.Index & 7);

            byte modrm = (byte)((mod << 6) | ((regField & 7) << 3) | rm);
            e.Byte(modrm);

            if (needSib)
            {
                byte scale = mem.MemScale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 0 };
                byte indexBits = hasIndex ? (byte)(mem.MemIndex.Index & 7) : (byte)4;
                byte baseBits = mem.HasMemBase ? (byte)(mem.MemBase.Index & 7) : (byte)5;
                if (!mem.HasMemBase)
                {
                    mod = 0;
                    dispSize = 4;
                }
                byte sib = (byte)((scale << 6) | ((indexBits & 7) << 3) | (baseBits & 7));
                e.Byte(sib);
            }

            if (dispSize == 1) e.Byte((byte)((sbyte)mem.MemDisp));
            else if (dispSize == 4) e.I32((int)mem.MemDisp);
        }

        public static (bool needRexX, bool needRexB) MemRexBits(X64Operand mem)
        {
            bool rexX = mem.HasMemIndex && mem.MemIndex.IsExtended;
            bool rexB = mem.HasMemBase && mem.MemBase.IsExtended;
            return (rexX, rexB);
        }
    }
}

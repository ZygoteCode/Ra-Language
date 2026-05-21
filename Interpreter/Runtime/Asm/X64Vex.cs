using System.Threading.Tasks;
using System;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// VEX prefix encoder (AVX, AVX2, BMI1, BMI2).
    ///
    /// Two-byte VEX (C5): used when VEX.X = 1 (no SIB index extension), VEX.B = 1
    /// (no rm base extension), VEX.W = 0, and map = 0F.
    /// Three-byte VEX (C4): used otherwise.
    ///
    /// AVX-512 (EVEX, byte 0x62) is intentionally out of scope for the first
    /// production cut. The encoder skeleton accepts the request and currently
    /// asserts; adding EVEX is a mechanical extension that doesn't change the
    /// surface API.
    /// </summary>
    internal static class X64Vex
    {
        public enum MapMm : byte { M0F = 1, M0F38 = 2, M0F3A = 3 }
        public enum MapPp : byte { None = 0, P66 = 1, PF3 = 2, PF2 = 3 }

        /// <summary>
        /// Emit a VEX prefix. dstIdx is the ModR/M.reg, srcIdx is the rm/base
        /// extended bit, idxIdx is the SIB.index extended bit, vvvvIdx is the
        /// non-destructive source register (use 0 / unused when none).
        /// </summary>
        public static void EmitVex(X64Emit e, bool l, byte vvvvIdx, byte dstIdx, byte idxIdx, byte rmIdx, MapPp pp, MapMm mm, bool w)
        {
            bool rexR = dstIdx >= 8;
            bool rexX = idxIdx >= 8;
            bool rexB = rmIdx >= 8;

            bool needThreeByte = w || mm != MapMm.M0F || rexX || rexB;

            byte vvvv = (byte)(~vvvvIdx & 0x0F);

            if (needThreeByte)
            {
                e.Byte(0xC4);
                byte b1 = (byte)((((rexR ? 0 : 1) & 1) << 7)
                                | (((rexX ? 0 : 1) & 1) << 6)
                                | (((rexB ? 0 : 1) & 1) << 5)
                                | ((byte)mm & 0x1F));
                e.Byte(b1);
                byte b2 = (byte)(((w ? 1 : 0) << 7) | (vvvv << 3) | ((l ? 1 : 0) << 2) | ((byte)pp & 3));
                e.Byte(b2);
            }
            else
            {
                e.Byte(0xC5);
                byte b1 = (byte)((((rexR ? 0 : 1) & 1) << 7) | (vvvv << 3) | ((l ? 1 : 0) << 2) | ((byte)pp & 3));
                e.Byte(b1);
            }
        }
    }
}

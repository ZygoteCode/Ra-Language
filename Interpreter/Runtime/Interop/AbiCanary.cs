using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Sentinel/canary pattern for native-call scratch buffers. When @dll_import(abi_canary = true)
    /// or env RA_FFI_CANARY=1 is set, every scratch buffer allocated for ref params is padded with
    /// magic bytes before and after the payload. After the call returns we verify the bytes
    /// are intact — a corruption (overflow / wrong size / wrong ABI signature) is detected
    /// instead of silently overwriting unrelated memory.
    /// </summary>
    public static class AbiCanary
    {
        public const uint Magic = 0xDEADBEEF;
        public const int PadBytes = 16;

        public static int Detected;

        public static IntPtr Wrap(int innerSize, out IntPtr innerPtr)
        {
            var total = innerSize + (PadBytes * 2);
            var buf = Marshal.AllocHGlobal(total);
            innerPtr = IntPtr.Add(buf, PadBytes);
            unsafe
            {
                byte* p = (byte*)buf;
                for (int i = 0; i < PadBytes; i += 4) *(uint*)(p + i) = Magic;
                byte* tail = (byte*)innerPtr + innerSize;
                for (int i = 0; i < PadBytes; i += 4) *(uint*)(tail + i) = Magic;
            }
            return buf;
        }

        public static bool Verify(IntPtr outerBuf, int innerSize, out string message)
        {
            message = "";
            unsafe
            {
                byte* p = (byte*)outerBuf;
                for (int i = 0; i < PadBytes; i += 4)
                {
                    if (*(uint*)(p + i) != Magic)
                    {
                        System.Threading.Interlocked.Increment(ref Detected);
                        message = $"ABI canary corruption: leading padding @{i} = 0x{*(uint*)(p + i):X8}, expected 0x{Magic:X8}";
                        return false;
                    }
                }
                byte* tail = p + PadBytes + innerSize;
                for (int i = 0; i < PadBytes; i += 4)
                {
                    if (*(uint*)(tail + i) != Magic)
                    {
                        System.Threading.Interlocked.Increment(ref Detected);
                        message = $"ABI canary corruption: trailing padding @{i} = 0x{*(uint*)(tail + i):X8}, expected 0x{Magic:X8}";
                        return false;
                    }
                }
            }
            return true;
        }

        public static void Free(IntPtr outerBuf)
        {
            if (outerBuf != IntPtr.Zero) Marshal.FreeHGlobal(outerBuf);
        }
    }
}

using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Allocates an executable memory region, copies the assembled byte
    /// sequence into it, and returns a pointer suitable for
    /// Marshal.GetDelegateForFunctionPointer.
    ///
    /// AOT note: this path is fully AOT-compatible — it only uses the platform
    /// APIs VirtualAlloc/VirtualProtect (Win64) and mmap/mprotect (POSIX) plus
    /// `Marshal.GetDelegateForFunctionPointer` with pre-declared delegate types
    /// from the FFI subsystem, none of which require System.Reflection.Emit.
    ///
    /// Scope: x64 only. The executor refuses to allocate executable memory if
    /// the process is not running on x64 (Architecture.X64 / Arm64-on-x64 with
    /// process running as x64).
    /// </summary>
    public static class AsmExecutor
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;
        private const int MAP_PRIVATE = 0x02;
        private const int MAP_ANON_LINUX = 0x20;
        private const int MAP_ANON_BSD = 0x1000;
        private const int MAP_FAILED_FLAG = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll")]
        private static extern void FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
        private static extern IntPtr LinuxMmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

        [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        private static extern int LinuxMprotect(IntPtr addr, UIntPtr len, int prot);

        [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
        private static extern int LinuxMunmap(IntPtr addr, UIntPtr length);

        private static long _totalAllocated;
        private static long _liveRegions;

        public static long TotalAllocatedBytes => Interlocked.Read(ref _totalAllocated);
        public static long LiveRegionCount => Interlocked.Read(ref _liveRegions);

        public static bool IsSupported
        {
            get
            {
                if (RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                    return false;
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            }
        }

        public static string Architecture => "x64";

        public sealed class ExecutableRegion : IDisposable
        {
            public IntPtr Address { get; private set; }
            public long Size { get; }
            private readonly bool _windows;
            private bool _disposed;

            internal ExecutableRegion(IntPtr addr, long size, bool windows)
            {
                Address = addr;
                Size = size;
                _windows = windows;
                Interlocked.Add(ref _totalAllocated, size);
                Interlocked.Increment(ref _liveRegions);
            }

            public void Dispose()
            {
                if (_disposed || Address == IntPtr.Zero) return;
                _disposed = true;
                if (_windows)
                {
                    VirtualFree(Address, UIntPtr.Zero, MEM_RELEASE);
                }
                else
                {
                    LinuxMunmap(Address, (UIntPtr)(ulong)Size);
                }
                Address = IntPtr.Zero;
                Interlocked.Add(ref _totalAllocated, -Size);
                Interlocked.Decrement(ref _liveRegions);
            }

            ~ExecutableRegion() { Dispose(); }
        }

        public static ExecutableRegion Allocate(byte[] code)
        {
            if (code == null || code.Length == 0)
                throw new ArgumentException("Executable code cannot be empty.", nameof(code));
            if (!IsSupported)
                throw new PlatformNotSupportedException("AsmExecutor requires x64 architecture. x86 is not supported.");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var size = (UIntPtr)(ulong)code.Length;
                IntPtr addr = VirtualAlloc(IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (addr == IntPtr.Zero)
                    throw new InvalidOperationException("VirtualAlloc failed (err " + Marshal.GetLastWin32Error() + ")");
                Marshal.Copy(code, 0, addr, code.Length);
                if (!VirtualProtect(addr, size, PAGE_EXECUTE_READ, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    VirtualFree(addr, UIntPtr.Zero, MEM_RELEASE);
                    throw new InvalidOperationException("VirtualProtect failed (err " + err + ")");
                }
                FlushInstructionCache(GetCurrentProcess(), addr, size);
                return new ExecutableRegion(addr, code.Length, true);
            }

            int anonFlag = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MAP_ANON_BSD : MAP_ANON_LINUX;
            IntPtr mAddr = LinuxMmap(IntPtr.Zero, (UIntPtr)(ulong)code.Length, PROT_READ | PROT_WRITE, MAP_PRIVATE | anonFlag, -1, IntPtr.Zero);
            if (mAddr == new IntPtr(MAP_FAILED_FLAG))
                throw new InvalidOperationException("mmap failed (errno " + Marshal.GetLastWin32Error() + ")");
            Marshal.Copy(code, 0, mAddr, code.Length);
            if (LinuxMprotect(mAddr, (UIntPtr)(ulong)code.Length, PROT_READ | PROT_EXEC) != 0)
            {
                int errno = Marshal.GetLastWin32Error();
                LinuxMunmap(mAddr, (UIntPtr)(ulong)code.Length);
                throw new InvalidOperationException("mprotect failed (errno " + errno + ")");
            }
            return new ExecutableRegion(mAddr, code.Length, false);
        }
    }
}

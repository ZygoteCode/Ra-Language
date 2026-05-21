using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Keeps assembled executable regions alive while their pointer is held by
    /// Ra runtime values. Regions are interned by content hash so identical
    /// assembly text reuses the same code page.
    /// </summary>
    public static class AsmRegionRegistry
    {

        public static IntPtr GetOrCompile(string source)
        {
            return GetOrCompile(source, null, null);
        }

        public static IntPtr GetOrCompile(string source, X64Preprocessor.Options? ppOpts, AsmSecurityPolicy? policy)
        {
            var bytes = X64Assembler.Assemble(source, ppOpts, policy);
            var hash = AsmCodePool.ComputeHash(source);
            var slot = AsmCodePool.Allocate(bytes, hash);
            return slot.Address;
        }

        public static (IntPtr address, byte[] bytes) CompileToNewRegion(string source)
        {
            var bytes = X64Assembler.Assemble(source);
            var hash = AsmCodePool.ComputeHash(source);
            var slot = AsmCodePool.Allocate(bytes, hash);
            return (slot.Address, bytes);
        }

        public static byte[] AssembleOnly(string source) => X64Assembler.Assemble(source);

        public static int InternedCount => AsmCodePool.InternedCount;
        public static int LiveRegionCount => AsmCodePool.InternedCount;

        public static void Clear()
        {
            AsmCodePool.Clear();
        }
    }
}

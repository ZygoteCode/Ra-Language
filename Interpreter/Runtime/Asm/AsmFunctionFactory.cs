using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.Runtime.Interop;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Bridge from assembled x64 code to the existing FFI machinery.
    ///
    /// We build a NativeBinding that is already "resolved" (FunctionPointer set
    /// to the executable region's address). This way the entire marshalling /
    /// invocation pipeline used by `@dll_import` and `native_invoke` is reused
    /// — same call conventions, same return-type widening, same AOT-friendly
    /// dispatch matrix.
    /// </summary>
    public static class AsmFunctionFactory
    {
        public static NativeFunctionValue Create(string name, IntPtr address, string signature)
        {
            var (ret, args) = AsmSignature.Parse(signature);

            var paramSpecs = new List<NativeParameterSpec>(args.Count);
            var paramNames = new List<string>(args.Count);
            for (int i = 0; i < args.Count; i++)
            {
                paramSpecs.Add(new NativeParameterSpec("a" + i, args[i], false, false));
                paramNames.Add("a" + i);
            }

            var binding = new NativeBinding(
                library: "<asm>",
                entryPoint: name,
                callingConvention: NativeCallingConvention.WinApi,
                charset: NativeCharset.Utf8,
                exactSpelling: true,
                setLastError: false,
                preserveSig: true,
                bestFitMapping: false,
                throwOnUnmappableChar: false,
                searchPaths: null,
                parameters: paramSpecs,
                returnKind: ret);

            binding.FunctionPointer = address;
            binding.IsResolved = true;

            return new NativeFunctionValue(name, paramNames, binding);
        }
    }
}

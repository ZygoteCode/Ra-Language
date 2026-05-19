using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public enum StringFreePolicy
    {
        None,
        CoTaskMem,
        HGlobal,
        LocalFree,
        FreeLibcStyle,
        CustomSymbol
    }

    public sealed class NativeBinding
    {
        public string Library { get; }
        public string EntryPoint { get; }
        public NativeCallingConvention CallingConvention { get; }
        public NativeCharset Charset { get; }
        public bool ExactSpelling { get; }
        public bool SetLastError { get; }
        public bool PreserveSig { get; }
        public bool BestFitMapping { get; }
        public bool ThrowOnUnmappableChar { get; }
        public IReadOnlyList<string>? SearchPaths { get; }
        public StringFreePolicy ReturnStringFree { get; }
        public string? CustomFreeSymbol { get; }

        public IReadOnlyList<NativeParameterSpec> Parameters { get; }
        public NativeTypeKind ReturnKind { get; }

        public bool Trace { get; }
        public bool StaThread { get; }
        public bool AbiCanary { get; }

        public readonly object BindingLock = new object();
        public bool IsResolved { get; set; }
        public IntPtr LibraryHandle { get; set; }
        public IntPtr FunctionPointer { get; set; }
        public string? ResolvedLibraryName { get; set; }
        public string? LastResolutionError { get; set; }

        public NativeBinding(
            string library,
            string entryPoint,
            NativeCallingConvention callingConvention,
            NativeCharset charset,
            bool exactSpelling,
            bool setLastError,
            bool preserveSig,
            bool bestFitMapping,
            bool throwOnUnmappableChar,
            IReadOnlyList<string>? searchPaths,
            IReadOnlyList<NativeParameterSpec> parameters,
            NativeTypeKind returnKind,
            StringFreePolicy returnStringFree = StringFreePolicy.None,
            string? customFreeSymbol = null,
            bool trace = false,
            bool staThread = false,
            bool abiCanary = false)
        {
            Trace = trace;
            StaThread = staThread;
            AbiCanary = abiCanary;
            Library = library;
            EntryPoint = entryPoint;
            CallingConvention = callingConvention;
            Charset = charset;
            ExactSpelling = exactSpelling;
            SetLastError = setLastError;
            PreserveSig = preserveSig;
            BestFitMapping = bestFitMapping;
            ThrowOnUnmappableChar = throwOnUnmappableChar;
            SearchPaths = searchPaths;
            Parameters = parameters;
            ReturnKind = returnKind;
            ReturnStringFree = returnStringFree;
            CustomFreeSymbol = customFreeSymbol;
        }
    }

    public sealed class NativeParameterSpec
    {
        public string Name { get; }
        public NativeTypeKind Kind { get; }
        public bool IsOptional { get; }
        public bool IsRef { get; }

        public NativeParameterSpec(string name, NativeTypeKind kind, bool isOptional, bool isRef)
        {
            Name = name;
            Kind = kind;
            IsOptional = isOptional;
            IsRef = isRef;
        }
    }
}

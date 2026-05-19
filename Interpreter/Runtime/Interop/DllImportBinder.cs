using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public static class DllImportBinder
    {
        public const string AnnotationName = "dll_import";

        public static (NativeFunctionValue? value, Error? error) TryBind(
            FunctionDefinitionNode node,
            string functionName,
            string targetKey,
            Context context)
        {
            var anns = MetadataRegistry.Global.GetByKey(targetKey);
            AnnotationInstanceValue? dll = null;
            for (int i = 0; i < anns.Count; i++)
            {
                if (anns[i].DefinitionName == AnnotationName) { dll = anns[i]; break; }
            }
            if (dll == null) return (null, null);

            var libraryArg = dll.Get("library") ?? dll.Get("name");
            if (libraryArg is not StringValue libVal || string.IsNullOrWhiteSpace(libVal.Value))
            {
                return (null, new RuntimeError(node.PositionStart, node.PositionEnd,
                    "@dll_import: 'library' argument is required and must be a non-empty string", context));
            }
            string library = libVal.Value;

            string entryPoint = functionName;
            if (dll.Get("entry_point") is StringValue ep && !string.IsNullOrWhiteSpace(ep.Value))
                entryPoint = ep.Value;

            var charsetRaw = dll.Get("charset");
            bool charsetExplicit = charsetRaw is StringValue csStr && !string.IsNullOrWhiteSpace(csStr.Value);
            var charset = ParseCharset(charsetRaw);

            if (!charsetExplicit)
            {
                charset = InferCharsetFromSymbol(entryPoint, functionName);
            }
            var callConv = ParseCallingConvention(dll.Get("calling_convention"));
            bool exactSpelling = AsBool(dll.Get("exact_spelling"), false);
            bool setLastError = AsBool(dll.Get("set_last_error"), false);
            bool preserveSig = AsBool(dll.Get("preserve_sig"), true);
            bool bestFitMapping = AsBool(dll.Get("best_fit_mapping"), true);
            bool throwOnUnmappableChar = AsBool(dll.Get("throw_on_unmappable_char"), false);

            IReadOnlyList<string>? searchPaths = null;
            if (dll.Get("search_paths") is ListValue paths)
            {
                var sp = new List<string>(paths.Elements.Count);
                foreach (var p in paths.Elements)
                {
                    if (p is StringValue sv && !string.IsNullOrWhiteSpace(sv.Value)) sp.Add(sv.Value);
                }
                searchPaths = sp;
            }

            var paramSpecs = new List<NativeParameterSpec>(node.ArgNameToks.Count);
            for (int i = 0; i < node.ArgNameToks.Count; i++)
            {
                var name = node.ArgNameToks[i].Value?.ToString() ?? $"arg{i}";
                TypeDescriptor? td = i < node.ArgTypes.Count ? node.ArgTypes[i] : null;
                bool isRef = i < node.IsRefParams.Count && node.IsRefParams[i];
                if (td == null)
                {
                    return (null, new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"@dll_import: parameter '{name}' must have an explicit type annotation", context));
                }
                var kind = NativeMarshaller.ResolveKind(td, charset);
                paramSpecs.Add(new NativeParameterSpec(name, kind, false, isRef));
            }

            NativeTypeKind returnKind;
            if (node.ReturnType == null) returnKind = NativeTypeKind.Void;
            else returnKind = NativeMarshaller.ResolveKind(node.ReturnType, charset);

            var (freePolicy, customSym) = ParseStringFreePolicy(dll.Get("string_free"));

            bool trace = AsBool(dll.Get("trace"), false) || Environment.GetEnvironmentVariable("RA_FFI_TRACE") == "1";
            bool staThread = AsBool(dll.Get("sta_thread"), false);
            bool abiCanary = AsBool(dll.Get("abi_canary"), false) || Environment.GetEnvironmentVariable("RA_FFI_CANARY") == "1";

            var binding = new NativeBinding(
                library, entryPoint, callConv, charset,
                exactSpelling, setLastError, preserveSig, bestFitMapping, throwOnUnmappableChar,
                searchPaths, paramSpecs, returnKind, freePolicy, customSym,
                trace, staThread, abiCanary);

            var paramNames = paramSpecs.Select(p => p.Name).ToList();
            var nfv = new NativeFunctionValue(functionName, paramNames, binding)
                .SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            return ((NativeFunctionValue)nfv, null);
        }

        private static NativeCharset ParseCharset(RuntimeValue? v)
        {
            if (v is not StringValue s) return NativeCharset.Auto;
            switch (s.Value.ToLowerInvariant())
            {
                case "utf16": case "unicode": case "wide": return NativeCharset.Utf16;
                case "utf8": case "utf-8": return NativeCharset.Utf8;
                case "ansi": return NativeCharset.Ansi;
                case "native": return NativeCharset.Native;
                case "auto": return NativeCharset.Auto;
                default: return NativeCharset.Auto;
            }
        }

        private static NativeCallingConvention ParseCallingConvention(RuntimeValue? v)
        {
            if (v is not StringValue s) return NativeCallingConvention.PlatformDefault;
            switch (s.Value.ToLowerInvariant())
            {
                case "cdecl": return NativeCallingConvention.Cdecl;
                case "stdcall": return NativeCallingConvention.StdCall;
                case "fastcall": return NativeCallingConvention.FastCall;
                case "thiscall": return NativeCallingConvention.ThisCall;
                case "winapi": return NativeCallingConvention.WinApi;
                case "platform_default": case "default": case "native": return NativeCallingConvention.PlatformDefault;
                default: return NativeCallingConvention.PlatformDefault;
            }
        }

        private static bool AsBool(RuntimeValue? v, bool defaultValue)
        {
            if (v == null) return defaultValue;
            if (v is BooleanValue b) return b.Value;
            if (v is NullValue) return defaultValue;
            return v.IsTrue();
        }

        // Windows-style API name suffix convention: `XxxxA` -> ANSI, `XxxxW` -> UTF-16.
        // When the user did NOT explicitly set `charset`, infer from the entry_point first
        // (it is the actual exported symbol that will be called), then from the Ra function name.
        // On non-Windows platforms, A/W suffixes are not part of POSIX convention, so we still
        // honour the heuristic (it is safe: a Linux symbol genuinely ending in `A`/`W` would be
        // unusual, and the user can always set `charset` explicitly to override).
        private static NativeCharset InferCharsetFromSymbol(string entryPoint, string raName)
        {
            foreach (var name in new[] { entryPoint, raName })
            {
                if (string.IsNullOrEmpty(name) || name.Length < 2) continue;
                char last = name[name.Length - 1];
                char prev = name[name.Length - 2];
                bool prevIsLower = char.IsLower(prev) || char.IsDigit(prev) || prev == 'x';
                bool prevIsUpper = char.IsUpper(prev);
                if ((last == 'A' || last == 'W') && (prevIsLower || prevIsUpper))
                {
                    return last == 'A' ? NativeCharset.Ansi : NativeCharset.Utf16;
                }
            }
            return NativeCharset.Auto;
        }

        private static (StringFreePolicy policy, string? customSymbol) ParseStringFreePolicy(RuntimeValue? v)
        {
            if (v is not StringValue sv) return (StringFreePolicy.None, null);
            var s = sv.Value.Trim();
            if (string.IsNullOrEmpty(s) || s == "none") return (StringFreePolicy.None, null);
            if (s == "cotask" || s == "cotaskmem") return (StringFreePolicy.CoTaskMem, null);
            if (s == "hglobal") return (StringFreePolicy.HGlobal, null);
            if (s == "localfree") return (StringFreePolicy.LocalFree, null);
            if (s == "free" || s == "libc") return (StringFreePolicy.FreeLibcStyle, null);
            if (s.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return (StringFreePolicy.CustomSymbol, s.Substring("custom:".Length));
            return (StringFreePolicy.None, null);
        }
    }
}

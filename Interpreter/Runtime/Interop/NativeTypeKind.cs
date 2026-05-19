namespace RaLanguage.Interpreter.Runtime.Interop
{
    public enum NativeTypeKind
    {
        Void,
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        IntPtr,
        Float,
        Double,
        Bool,
        StringUtf16,
        StringUtf8,
        StringAnsi,
        Handle,
        Buffer,
        Pointer
    }

    public enum NativeCallingConvention
    {
        PlatformDefault,
        Cdecl,
        StdCall,
        FastCall,
        ThisCall,
        WinApi
    }

    public enum NativeCharset
    {
        Auto,
        Utf16,
        Utf8,
        Ansi,
        Native
    }
}

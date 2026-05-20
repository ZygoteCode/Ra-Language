using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Csharp
{
    public sealed class CsharpCompileException : Exception
    {
        public IReadOnlyList<string> Diagnostics { get; }

        public CsharpCompileException(string message, IReadOnlyList<string> diagnostics) : base(message)
        {
            Diagnostics = diagnostics;
        }
    }

    public sealed class CsharpRuntimeException : Exception
    {
        public CsharpRuntimeException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class CsharpUnsupportedException : Exception
    {
        public CsharpUnsupportedException(string message) : base(message) { }
    }
}

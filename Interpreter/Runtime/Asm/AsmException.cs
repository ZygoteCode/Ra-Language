using System;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    public sealed class AsmAssembleException : Exception
    {
        public int LineNumber { get; }
        public string LineText { get; }

        public AsmAssembleException(int lineNumber, string lineText, string message)
            : base($"line {lineNumber}: {message}: \"{lineText}\"")
        {
            LineNumber = lineNumber;
            LineText = lineText;
        }
    }
}

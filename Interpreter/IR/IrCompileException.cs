using System;

namespace RaLanguage.Interpreter.IR
{
    // Thrown by expression / statement compilers when they encounter an AST
    // node they cannot lower yet. Caught at the *statement* boundary in
    // IrCompiler so the rest of the script keeps compiling and only the
    // offending statement falls back to OP_VISIT_AST.
    //
    // Intentionally not derived from RuntimeError — this is a compile-time
    // signal, never surfaced to user code. The message is logged at debug
    // level only.
    internal sealed class IrCompileException : Exception
    {
        public IrCompileException(string reason) : base(reason) { }
    }
}

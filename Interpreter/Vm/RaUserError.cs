using System;
using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Vm
{
    // Internal sentinel exception used to unwind out of the VM dispatch
    // switch when an opcode raises a user-visible Ra error. Caught at the
    // outer dispatch-loop level so the exception-handler table can be
    // scanned for a matching try/catch region. If no handler matches, the
    // outer caller propagates the wrapped Error as a normal
    // RuntimeResult.Failure.
    //
    // Performance: throwing managed exceptions is slower than returning a
    // sentinel value, but Ra-level errors are rare on the hot path
    // (arithmetic / read / call paths). The throw path matches the AST
    // visitor's cost profile (which also pays a cold-error penalty via
    // RuntimeResult.Failure propagation). Hot loops with predictable
    // control flow are unaffected.
    internal sealed class RaUserError : Exception
    {
        public readonly Error Err;

        public RaUserError(Error err) : base(err?.ToString() ?? "<error>")
        {
            Err = err!;
        }
    }
}

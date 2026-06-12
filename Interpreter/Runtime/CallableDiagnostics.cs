using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Upgrades a "callable bound to a typed `fn(...)` slot" failure into a
    // precise signature-diff diagnostic (expected/found + actionable hint).
    // Returns null when the mismatch is NOT a function-type-vs-callable case,
    // so the caller falls back to its own generic message. Centralises the
    // wiring every binding site shares — variable declaration / assignment and
    // argument binding across the call kinds (plain fn, method, extension,
    // overload group) — so the diagnostic reads identically everywhere.
    internal static class CallableDiagnostics
    {
        public static RuntimeError? TryFunctionMismatch(
            Context ctx, TypeDescriptor expected, RuntimeValue value,
            Position p1, Position p2, string? argName = null)
        {
            if (!TypeSystem.TryDescribeFunctionMismatch(ctx, expected, value, out var msg, out var hint))
                return null;
            var full = argName != null ? $"argument '{argName}': {msg}" : msg;
            return new RuntimeError(p1, p2, full, ctx,
                code: DiagnosticCode.RuntimeTypeMismatch,
                primaryLabel: "callable signature mismatch", help: hint);
        }
    }
}

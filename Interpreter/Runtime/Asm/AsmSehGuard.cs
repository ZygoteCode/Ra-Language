using System.Threading.Tasks;
using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Wraps an asm-region invocation so that access-violations / structured
    /// exceptions on Windows surface as managed exceptions the Ra runtime can
    /// convert into a clean RuntimeError.
    ///
    /// The CLR by default already converts SEH to managed `SEHException` /
    /// `AccessViolationException` when the legacy COM-style exception model is
    /// in effect. Some hosting modes treat AVs as corrupted-state exceptions
    /// (CSEs) and refuse to deliver them to managed catch blocks. This guard
    /// uses <see cref="HandleProcessCorruptedStateExceptionsAttribute"/> via a
    /// dedicated helper to ensure delivery, and falls back to a try/catch
    /// boundary that callers can rely on.
    /// </summary>
    public static class AsmSehGuard
    {
        public sealed class GuardedFailure : Exception
        {
            public uint? ErrorCode { get; }
            public GuardedFailure(string message, uint? code = null) : base(message) { ErrorCode = code; }
        }

        [HandleProcessCorruptedStateExceptions]
        public static T Run<T>(Func<T> body)
        {
            try
            {
                return body();
            }
            catch (SEHException sehx)
            {
                throw new GuardedFailure($"asm region raised SEH exception 0x{sehx.ErrorCode:X}", (uint)sehx.ErrorCode);
            }
            catch (AccessViolationException avx)
            {
                throw new GuardedFailure($"asm region access violation: {avx.Message}");
            }
            catch (DataMisalignedException dme)
            {
                throw new GuardedFailure($"asm region data misalignment: {dme.Message}");
            }
            catch (StackOverflowException)
            {
                throw new GuardedFailure("asm region overflowed the managed stack");
            }
        }

        [HandleProcessCorruptedStateExceptions]
        public static void RunVoid(Action body)
        {
            try { body(); }
            catch (SEHException sehx) { throw new GuardedFailure($"asm region raised SEH 0x{sehx.ErrorCode:X}", (uint)sehx.ErrorCode); }
            catch (AccessViolationException avx) { throw new GuardedFailure($"asm region AV: {avx.Message}"); }
            catch (DataMisalignedException dme) { throw new GuardedFailure($"asm region misalignment: {dme.Message}"); }
        }
    }
}

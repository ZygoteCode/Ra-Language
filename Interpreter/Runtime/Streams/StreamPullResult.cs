using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime.Streams
{
    // Result of one PullNext step. Three mutually exclusive states:
    //
    //   1. (!Done, Value != null, Error == null)  — element produced.
    //   2. (Done, Value == null, Error == null)   — stream exhausted normally.
    //   3. (Done, Value == null, Error != null)   — stream terminated with an
    //                                                error; the caller surfaces it.
    //
    // Cancellation surfaces as case (2): the upstream observes the
    // StreamValue.IsCancelled flag and returns DoneResult. Operators check
    // their own state first so a cancelled `take(10)` over an infinite source
    // stops asking the source immediately.
    //
    // Single value type, no allocation on the steady-state pull path: the
    // struct lives on the stack across the ValueTask continuation chain.
    public readonly struct StreamPullResult
    {
        public readonly bool Done;
        public readonly RuntimeValue? Value;
        public readonly Error? Error;

        private StreamPullResult(bool done, RuntimeValue? value, Error? error)
        {
            Done = done;
            Value = value;
            Error = error;
        }

        public static StreamPullResult OfValue(RuntimeValue v) => new(false, v, null);
        public static StreamPullResult DoneResult { get; } = new(true, null, null);
        public static StreamPullResult OfError(Error e) => new(true, null, e);
    }
}

using System.Threading.Tasks;
using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Values
{
    // Stack-allocated replacement for the (RuntimeValue?, Error?) tuple returned by
    // every arithmetic / comparison / coercion operator on RuntimeValue. Tuples in
    // C# allocate when they cross method boundaries on AOT in some paths and always
    // pay a Deconstruct + reassign tax; this struct passes two references inline.
    //
    // Item1 / Item2 mirror the original tuple layout so existing call sites that
    // index into the result keep working unchanged. Deconstruct keeps the
    // `var (v, e) = ...` syntax. The implicit tuple-conversion lets `return (val, err);`
    // statements stay as-is across the codebase.
    public readonly struct ValueResult
    {
        public readonly RuntimeValue? Value;
        public readonly Error? Error;

        public ValueResult(RuntimeValue? value, Error? error)
        {
            Value = value;
            Error = error;
        }

        public RuntimeValue? Item1 => Value;
        public Error? Item2 => Error;

        public void Deconstruct(out RuntimeValue? value, out Error? error)
        {
            value = Value;
            error = Error;
        }

        public static implicit operator ValueResult((RuntimeValue?, Error?) tuple)
            => new ValueResult(tuple.Item1, tuple.Item2);
    }
}

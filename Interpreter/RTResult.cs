using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter
{
    public class RTResult
    {
        public RuntimeValue? Value { get; private set; }
        public Error? Error { get; private set; }
        public RuntimeValue? FuncReturnValue { get; private set; }
        public bool LoopShouldContinue { get; private set; }
        public bool LoopShouldBreak { get; private set; }

        public void Reset()
        {
            Value = null;
            Error = null;
            FuncReturnValue = null;
            LoopShouldContinue = false;
            LoopShouldBreak = false;
        }

        public RuntimeValue Register(RTResult res)
        {
            Error = res.Error;
            FuncReturnValue = res.FuncReturnValue;
            LoopShouldContinue = res.LoopShouldContinue;
            LoopShouldBreak = res.LoopShouldBreak;
            return res.Value;
        }

        public RTResult Success(RuntimeValue? value)
        {
            Reset();
            Value = value;
            return this;
        }

        public RTResult SuccessReturn(RuntimeValue value)
        {
            Reset();
            FuncReturnValue = value;
            return this;
        }

        public RTResult SuccessContinue()
        {
            Reset();
            LoopShouldContinue = true;
            return this;
        }

        public RTResult SuccessBreak()
        {
            Reset();
            LoopShouldBreak = true;
            return this;
        }

        public RTResult Failure(Error error)
        {
            Reset();
            Error = error;
            return this;
        }

        public bool ShouldReturn()
        {
            return Error != null || FuncReturnValue != null || LoopShouldContinue || LoopShouldBreak;
        }
    }
}
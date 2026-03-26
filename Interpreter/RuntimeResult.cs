using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;
using System;

namespace RaLanguage.Interpreter
{
    public class RuntimeResult : IDisposable
    {
        public RuntimeValue? Value { get; private set; }
        public Error? Error { get; private set; }
        public RuntimeValue? FuncReturnValue { get; private set; }

        public bool LoopShouldContinue { get; private set; }
        public bool LoopShouldBreak { get; set; }

        public RuntimeValue? YieldValue { get; private set; }
        public bool ShouldYield { get; private set; }

        private bool _disposed;

        public void Reset()
        {
            Value = null;
            Error = null;
            FuncReturnValue = null;
            LoopShouldContinue = false;
            LoopShouldBreak = false;
            YieldValue = null;
            ShouldYield = false;
        }

        private void DisposeInternal()
        {
            if (!_disposed)
            {
                Reset();
                _disposed = true;
            }
        }

        public void Dispose()
        {
            DisposeInternal();
            GC.SuppressFinalize(this);
        }

        public RuntimeResult Success(RuntimeValue? value)
        {
            DisposeInternal();
            _disposed = false;
            Value = value;
            return this;
        }

        public RuntimeResult SuccessReturn(RuntimeValue value)
        {
            DisposeInternal();
            _disposed = false;
            FuncReturnValue = value;
            return this;
        }

        public RuntimeResult SuccessContinue()
        {
            DisposeInternal();
            _disposed = false;
            LoopShouldContinue = true;
            return this;
        }

        public RuntimeResult SuccessBreak()
        {
            DisposeInternal();
            _disposed = false;
            LoopShouldBreak = true;
            return this;
        }

        public RuntimeResult SuccessYield(RuntimeValue value)
        {
            DisposeInternal();
            _disposed = false;
            YieldValue = value;
            ShouldYield = true;
            Value = value;
            return this;
        }

        public RuntimeResult Failure(Error error)
        {
            DisposeInternal();
            _disposed = false;
            Error = error;
            return this;
        }

        public RuntimeValue? Register(RuntimeResult res, bool propagateLoopControl = true)
        {
            Error = res.Error;
            FuncReturnValue = res.FuncReturnValue;

            if (propagateLoopControl)
            {
                LoopShouldContinue = res.LoopShouldContinue;
                LoopShouldBreak = res.LoopShouldBreak;
            }

            return res.Value;
        }

        public bool ShouldReturn()
        {
            return Error != null || FuncReturnValue != null || LoopShouldContinue || LoopShouldBreak;
        }
    }
}
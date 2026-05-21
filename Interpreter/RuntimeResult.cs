using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;
using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter
{
    // Single-byte control-flow tag. Replaces the prior 4-bool fan
    // (Error / FuncReturnValue / LoopShouldContinue / LoopShouldBreak)
    // plus the ShouldYield flag. Hot-path `ShouldReturn` collapses to
    // one cmp/jne.
    public enum FlowState : byte
    {
        Normal = 0,
        Error = 1,
        Return = 2,
        Continue = 3,
        Break = 4,
        Yield = 5,
    }

    // Stack-allocated visitor return value. Was a class — every AST node visit
    // paid a heap allocation. As a struct it lives on the stack across the call
    // chain. Mutating methods rely on the implicit `ref this` of struct instance
    // methods so existing `res.Success(...)` / `res.Register(...)` call sites
    // mutate the local in place; the returned value is the mutated struct.
    public struct RuntimeResult
    {
        private FlowState _state;
        private RuntimeValue? _value;
        private Error? _error;
        // Shared slot for return value / yield value — both are flow payloads
        // that never coexist with each other on the same RuntimeResult.
        private RuntimeValue? _flowValue;

        public FlowState State
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state;
        }

        public RuntimeValue? Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        public Error? Error
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Error ? _error : null;
        }

        public RuntimeValue? FuncReturnValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Return ? _flowValue : null;
        }

        public bool LoopShouldContinue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Continue;
        }

        public bool LoopShouldBreak
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Break;
            set
            {
                if (value)
                {
                    _state = FlowState.Break;
                }
                else if (_state == FlowState.Break)
                {
                    _state = FlowState.Normal;
                }
            }
        }

        public RuntimeValue? YieldValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Yield ? _flowValue : null;
        }

        public bool ShouldYield
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == FlowState.Yield;
        }

        public void Reset()
        {
            _state = FlowState.Normal;
            _value = null;
            _error = null;
            _flowValue = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult Success(RuntimeValue? value)
        {
            _value = value;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult SuccessReturn(RuntimeValue value)
        {
            _state = FlowState.Return;
            _flowValue = value;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult SuccessContinue()
        {
            _state = FlowState.Continue;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult SuccessBreak()
        {
            _state = FlowState.Break;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult SuccessYield(RuntimeValue value)
        {
            _state = FlowState.Yield;
            _flowValue = value;
            _value = value;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeResult Failure(Error error)
        {
            _state = FlowState.Error;
            _error = error;
            return this;
        }

        // Inherit child's flow state. Pack-priority: Error > Return > Yield > Cont/Break > Normal.
        // Legacy multi-bool semantics: child's Error/Return/Yield always overwrite parent;
        // loop control (Cont/Break) overwrites only when propagateLoopControl=true.
        public RuntimeValue? Register(RuntimeResult res, bool propagateLoopControl = true)
        {
            var cs = res._state;

            if (propagateLoopControl)
            {
                _state = cs;
            }
            else if (cs == FlowState.Error || cs == FlowState.Return || cs == FlowState.Yield)
            {
                _state = cs;
            }
            else
            {
                // child is Normal or Cont/Break with !propagate.
                // Legacy clears parent Error/Return/Yield in this case.
                if (_state == FlowState.Error || _state == FlowState.Return || _state == FlowState.Yield)
                {
                    _state = FlowState.Normal;
                }
                // else: keep parent loop state (Continue/Break) or Normal as-is.
            }

            _error = res._error;
            _flowValue = res._flowValue;
            return res._value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldReturn() => _state != FlowState.Normal;
    }
}

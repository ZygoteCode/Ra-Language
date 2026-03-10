using RaLanguage.Interpreter.Values;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolEntry : IDisposable
    {
        public RuntimeValue? Value { get; set; }
        public bool IsLet { get; set; }
        public bool IsMoved { get; set; }

        public TypeDescriptor? DeclaredType { get; set; }
        public bool IsStaticallyTyped { get; set; } = false;

        private bool _disposed;

        public SymbolEntry(RuntimeValue value, bool isLet = false)
        {
            Value = value;
            IsLet = isLet;
            IsMoved = false;
            DeclaredType = null;
            IsStaticallyTyped = false;
            _disposed = false;
        }

        public SymbolEntry(RuntimeValue value, bool isLet, TypeDescriptor? declaredType, bool isStaticallyTyped)
            : this(value, isLet)
        {
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                if (Value is IDisposable d)
                {
                    try { d.Dispose(); } catch { }
                }

                Value = null;
            }
            _disposed = true;
        }

        ~SymbolEntry() { Dispose(false); }

        public void ClearReference()
        {
            Value = null;
            IsLet = false;
            IsMoved = false;
            DeclaredType = null;
            IsStaticallyTyped = false;
        }
    }
}
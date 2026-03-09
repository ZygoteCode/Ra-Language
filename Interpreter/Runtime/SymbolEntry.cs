using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolEntry : IDisposable
    {
        public RuntimeValue? Value { get; set; }
        public bool IsLet { get; set; }
        public bool IsMoved { get; set; }

        private bool _disposed;

        public SymbolEntry(RuntimeValue value, bool isLet = false)
        {
            Value = value;
            IsLet = isLet;
            IsMoved = false;
            _disposed = false;
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
                    try { d.Dispose(); } catch {}
                }

                Value = null;
            }

            _disposed = true;
        }

        ~SymbolEntry()
        {
            Dispose(false);
        }

        public void ClearReference()
        {
            Value = null;
            IsLet = false;
            IsMoved = false;
        }
    }
}
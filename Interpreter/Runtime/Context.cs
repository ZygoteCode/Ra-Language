using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    public class Context : IDisposable
    {
        public string DisplayName { get; }
        public Context? Parent { get; }
        public Position? ParentEntryPos { get; }

        public SymbolTable? SymbolTable { get; set; }

        private bool _disposed;

        public ExtensionRegistry Extensions { get; }

        public Context(string displayName, Context? parent = null, Position? parentEntryPos = null, ExtensionRegistry? extensions = null)
        {
            DisplayName = displayName;
            Parent = parent;
            ParentEntryPos = parentEntryPos;
            SymbolTable = new SymbolTable(parent?.SymbolTable);
            Extensions = extensions ?? parent?.Extensions ?? new ExtensionRegistry();
        }

        public Context Copy()
        {
            var newCtx = new Context(DisplayName, this, ParentEntryPos, Extensions);
            newCtx.SymbolTable = new SymbolTable(newCtx.Parent?.SymbolTable);
            return newCtx;
        }

        public void ApplyChangesFrom(Context context)
        {
            if (context.SymbolTable == null) return;
            SymbolTable?.ApplyChangesFrom(context.SymbolTable);
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
                if (SymbolTable != null)
                {
                    try { SymbolTable.Dispose(); } catch { }
                    SymbolTable = null;
                }
            }

            _disposed = true;
        }

        ~Context()
        {
            Dispose(false);
        }

        public void DisposeSymbolTableRecursively()
        {
            if (SymbolTable == null) return;
            SymbolTable.DisposeRecursively();
            SymbolTable = null;
        }
    }
}
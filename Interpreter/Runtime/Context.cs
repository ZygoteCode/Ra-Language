using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    public class Context
    {
        public string DisplayName { get; }
        public Context? Parent { get; }
        public Position? ParentEntryPos { get; }

        public SymbolTable? SymbolTable { get; set; }

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
    }
}
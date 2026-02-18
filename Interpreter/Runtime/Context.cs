using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    public class Context
    {
        public string DisplayName { get; }
        public Context? Parent { get; }
        public Position? ParentEntryPos { get; }
        public SymbolTable SymbolTable { get; set; }

        public Context(string displayName, Context? parent = null, Position? parentEntryPos = null)
        {
            DisplayName = displayName;
            Parent = parent;
            ParentEntryPos = parentEntryPos;
        }

        public Context Copy()
        {
            Context newContext = new Context(DisplayName, this);
            newContext.SymbolTable = new SymbolTable(SymbolTable);
            return newContext;
        }

        public void ApplyChangesFrom(Context context)
        {
            SymbolTable.ApplyChangesFrom(context.SymbolTable);
        }
    }
}
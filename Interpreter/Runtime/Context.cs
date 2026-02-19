using RaLanguage.Lexer;
using System.Xml.Linq;

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
            var newCtx = new Context(DisplayName, this);
            newCtx.SymbolTable = new SymbolTable(newCtx.Parent?.SymbolTable);
            return newCtx;
        }

        public void ApplyChangesFrom(Context context)
        {
            SymbolTable.ApplyChangesFrom(context.SymbolTable);
        }
    }
}
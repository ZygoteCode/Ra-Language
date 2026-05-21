using RaLanguage.Lexer;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime
{
    public class Context
    {
        public string DisplayName { get; }
        public Context? Parent { get; }
        public Position? ParentEntryPos { get; }

        public SymbolTable? SymbolTable { get; set; }

        public ExtensionRegistry Extensions { get; }
        public bool AreCallsBlocked { get; set; }
        public bool IsInConstructor { get; set; }
        public AsyncContext? AsyncCtx { get; set; }

        // Loop bodies (For/While/ForEach) reuse a single child SymbolTable per iteration and
        // clear it between iterations. When such a body is itself a ScopeNode the default
        // ScopeNodeVisitor would Copy() again, undoing the saving. Setting this to true tells
        // ScopeNodeVisitor "the caller already gave you a fresh scope, do not Copy()."
        // The visitor consumes (clears) the flag on entry so nested scopes inside the body
        // continue to isolate normally.
        public bool ScopeSkipCopy { get; set; }

        // The class whose method body is currently executing, captured lexically (NOT the
        // dynamic type of `self`). Used by `super` resolution so that, inside B's method body
        // invoked on a C instance, `super` walks B.BaseClass instead of C.BaseClass.
        public ClassTypeValue? CurrentClassMethodOwner { get; set; }

        public Context(string displayName, Context? parent = null, Position? parentEntryPos = null, ExtensionRegistry? extensions = null)
        {
            DisplayName = displayName;
            Parent = parent;
            ParentEntryPos = parentEntryPos;
            SymbolTable = new SymbolTable(parent?.SymbolTable);
            Extensions = extensions ?? parent?.Extensions ?? new ExtensionRegistry();
            IsInConstructor = false;
            AsyncCtx = parent?.AsyncCtx;
            CurrentClassMethodOwner = parent?.CurrentClassMethodOwner;
        }

        public Context Copy()
        {
            // Constructor already allocates a SymbolTable parented to this.SymbolTable; do not
            // create a second one. Single allocation per Copy instead of two.
            var newCtx = new Context(DisplayName, this, ParentEntryPos, Extensions);
            newCtx.IsInConstructor = IsInConstructor;
            newCtx.AsyncCtx = AsyncCtx;
            newCtx.CurrentClassMethodOwner = CurrentClassMethodOwner;
            return newCtx;
        }
    }
}
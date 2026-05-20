using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class ScopeNodeVisitor : NodeVisitor<ScopeNode>
    {
        protected sealed override RuntimeResult VisitNode(ScopeNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            // Loop body hot path: when the caller has already produced a fresh child scope
            // (e.g. ForNodeVisitor/WhileNodeVisitor), reuse it directly. Saves a Context +
            // SymbolTable allocation on every iteration.
            Context newContext;
            bool reused = context.ScopeSkipCopy;
            if (reused)
            {
                context.ScopeSkipCopy = false; // nested scopes inside body must still isolate
                newContext = context;
            }
            else
            {
                newContext = context.Copy();
            }

            var nodes = node.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                res.Register(interpreter.Visit(nodes[i], newContext));

                if (res.FuncReturnValue != null)
                {
                    if (!reused) newContext.SymbolTable?.ReleaseLocalBorrows();
                    return res;
                }
                if (res.ShouldReturn())
                {
                    if (!reused) newContext.SymbolTable?.ReleaseLocalBorrows();
                    return res;
                }
            }

            // No write-back to `context`. The child scope's locals die with newContext.
            // Mutations to outer-scope variables already took effect in place because
            // SymbolTable.SetWithDeclarationType / TryAssign walk the parent chain and
            // mutate the shared SymbolEntry on the owning scope. Borrows held by the
            // dying locals are decremented before the table is abandoned so the source
            // entries' borrow counters do not leak across scope boundaries.
            if (!reused) newContext.SymbolTable?.ReleaseLocalBorrows();

            if (res.FuncReturnValue != null) return res;
            return res.Success(NullValue.Null);
        }
    }
}
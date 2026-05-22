using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class ScopeNodeVisitor : NodeVisitor<ScopeNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ScopeNode node, Context context, IInterpreter interpreter)
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
            RaLanguage.Interpreter.Values.RuntimeValue? lastValue = null;
            for (int i = 0; i < nodes.Count; i++)
            {
                var child = res.Register(await interpreter.Visit(nodes[i], newContext));
                if (child != null) lastValue = child;

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

            if (!reused) newContext.SymbolTable?.ReleaseLocalBorrows();

            if (res.FuncReturnValue != null) return res;
            // Return the value of the last statement in the block so block-as-
            // expression (`if X { 10 } else { 0 }`) yields the inner result.
            if (lastValue != null) return res.Success(lastValue);
            return res.Success(NullValue.Null);
        }
    }
}
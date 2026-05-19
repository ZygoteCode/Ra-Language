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

                if (res.FuncReturnValue != null) return res;
                if (res.ShouldReturn()) return res;
            }

            if (!reused)
            {
                context.ApplyChangesFrom(newContext);
            }

            if (res.FuncReturnValue != null) return res;
            return res.Success(NullValue.Null);
        }
    }
}
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class IfNodeVisitor : NodeVisitor<IfNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(IfNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var cases = node.Cases;

            for (int i = 0; i < cases.Count; i++)
            {
                var (condition, expr, shouldReturnNull) = cases[i];

                // Conditions are pure expressions — they cannot legally declare bindings.
                // Evaluate them in the surrounding scope so reads see live outer state
                // (this is what makes `if x == latest_outer_value` correct after an
                // inner mutation that happened in a previous case).
                var conditionValue = res.Register(await interpreter.Visit(condition, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (conditionValue.IsTrue())
                {
                    // Only allocate a child Context+SymbolTable when the body actually
                    // declares a binding (var / let / fn / class / ... — see
                    // AstScopeAnalysis). Bodies that are pure expressions, assignments,
                    // function calls, or nested control flow can run in the surrounding
                    // context: outer-scope mutations already propagate through
                    // SymbolEntry pointers, and nested control flow brings its own Copy.
                    Context bodyContext;
                    bool freshScope = node.BranchNeedsScope(i, expr);
                    if (freshScope) bodyContext = context.Copy();
                    else bodyContext = context;

                    var exprValue = res.Register(await interpreter.Visit(expr, bodyContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                    return res.Success(shouldReturnNull ? NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
                }
            }

            if (node.ElseCase != null)
            {
                var (expr, shouldReturnNull) = node.ElseCase.Value;
                Context elseContext;
                bool freshScope = node.BranchNeedsScope(cases.Count, expr);
                if (freshScope) elseContext = context.Copy();
                else elseContext = context;
                var exprValue = res.Register(await interpreter.Visit(expr, elseContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(shouldReturnNull ? NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
            }

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

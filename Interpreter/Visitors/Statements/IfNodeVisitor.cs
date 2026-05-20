using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class IfNodeVisitor : NodeVisitor<IfNode>
    {
        protected sealed override RuntimeResult VisitNode(IfNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            foreach (var (condition, expr, shouldReturnNull) in node.Cases)
            {
                // Conditions are pure expressions — they cannot legally declare bindings.
                // Evaluate them in the surrounding scope so reads see live outer state
                // (this is what makes `if x == latest_outer_value` correct after an
                // inner mutation that happened in a previous case).
                var conditionValue = res.Register(interpreter.Visit(condition, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (conditionValue.IsTrue())
                {
                    // Single fresh child scope for the body. Locals declared here die
                    // with bodyContext when the case completes; mutations to outer vars
                    // propagate via the shared SymbolEntry on the owning scope.
                    var bodyContext = context.Copy();
                    var exprValue = res.Register(interpreter.Visit(expr, bodyContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                    return res.Success(shouldReturnNull ? NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
                }
            }

            if (node.ElseCase != null)
            {
                var (expr, shouldReturnNull) = node.ElseCase.Value;
                var elseContext = context.Copy();
                var exprValue = res.Register(interpreter.Visit(expr, elseContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(shouldReturnNull ? NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
            }

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

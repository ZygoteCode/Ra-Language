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
            var newContext = context.Copy();

            foreach (var (condition, expr, shouldReturnNull) in node.Cases)
            {
                Context caseContext = newContext.Copy();
                var conditionValue = res.Register(interpreter.Visit(condition, caseContext));
                if (res.Error != null) return res;

                if (res.ShouldReturn())
                {
                    context.ApplyChangesFrom(caseContext);
                    return res;
                }

                if (conditionValue.IsTrue())
                {
                    Context realCaseContext = caseContext.Copy();
                    var exprValue = res.Register(interpreter.Visit(expr, realCaseContext));
                    if (res.Error != null) return res;
                    context.ApplyChangesFrom(realCaseContext);

                    if (res.ShouldReturn()) return res;
                    return res.Success(shouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
                }
                else
                {
                    context.ApplyChangesFrom(caseContext);
                }
            }

            if (node.ElseCase != null)
            {
                Context elseCaseContext = newContext.Copy();
                var (expr, shouldReturnNull) = node.ElseCase.Value;
                var exprValue = res.Register(interpreter.Visit(expr, elseCaseContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(elseCaseContext);
                if (res.ShouldReturn()) return res;
                return res.Success(shouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
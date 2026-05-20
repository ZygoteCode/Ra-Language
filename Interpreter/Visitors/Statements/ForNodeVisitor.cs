using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class ForNodeVisitor : NodeVisitor<ForNode>
    {
        protected sealed override RuntimeResult VisitNode(ForNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            // Single child scope holds the iteration variable plus any side effects from
            // evaluating bounds. Replaces the original initContext + newContext chain.
            var loopContext = context.Copy();
            var loopSymbols = loopContext.SymbolTable!;

            var startValue = res.Register(interpreter.Visit(node.StartValueNode, loopContext));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            var endValue = res.Register(interpreter.Visit(node.EndValueNode, loopContext));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            RuntimeValue stepValue;
            if (node.StepValueNode != null)
            {
                stepValue = res.Register(interpreter.Visit(node.StepValueNode, loopContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
            }
            else
            {
                stepValue = NumberValue.One;
            }

            BigNumber i = ((NumberValue)startValue).Value;
            BigNumber end = ((NumberValue)endValue).Value;
            BigNumber step = ((NumberValue)stepValue).Value;
            bool ascending = step >= BigNumber.Zero;

            string varName = node.VarNameTok.Value!.ToString()!;

            // Body context lives once and is reused across every iteration. Locals declared
            // inside the body get dropped via Clear() at the start of each iteration. Mutations
            // to outer-scope variables still propagate because SymbolTable.Set walks parents.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            // Seed the iteration variable in the loop scope, then update its entry directly to
            // avoid the parent-chain walk inside SymbolTable.Set on every iteration.
            loopSymbols.Set(varName, NumberValue.OfBigNumber(i));
            var iterEntry = loopSymbols.GetEntry(varName);

            while (ascending ? i < end : i > end)
            {
                iterEntry!.Value = NumberValue.OfBigNumber(i);
                i += step;

                bodySymbols.Clear();
                bodyContext.ScopeSkipCopy = true;
                res.Register(interpreter.Visit(node.BodyNode, bodyContext));
                if (res.Error != null) return res;

                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
                if (res.ShouldReturn()) return res;
            }

            // No write-back. Mutations to outer-scope variables took effect in place
            // via the shared SymbolEntry; locals declared inside the body died when
            // bodySymbols.Clear() ran between iterations and again when bodyContext
            // becomes unreachable on return.
            return res.Success(NullValue.Null);
        }
    }
}

using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Patterns
{
    // Destructuring declaration evaluator:
    //
    //   let (a, b) = expr;
    //   let [head, ..tail] = expr;
    //   let User { name, age: a } = expr;
    //
    // Semantics:
    //   * the initializer expression is evaluated exactly once;
    //   * the pattern engine matches it in a *transient* bindings list;
    //   * if the match succeeds, each binding is committed to the enclosing
    //     scope using the chosen declaration kind (let / var / const / final);
    //   * if the match fails, a RuntimeError is produced (refutable patterns
    //     should have been rejected at parse / analyzer time, so this is a
    //     defence-in-depth net).
    //
    // The visitor never produces a value (declarations are statements); it
    // returns NullValue.Null to keep the standard RuntimeResult shape.
    public class DestructuringDeclarationNodeVisitor : NodeVisitor<DestructuringDeclarationNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(DestructuringDeclarationNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(DestructuringDeclarationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var initEval = await IrExpressionEvaluator.Evaluate(node.Initializer, context, interpreter);
            if (initEval.Error != null) return res.Failure(initEval.Error);
            if (initEval.FuncReturnValue != null) return res.SuccessReturn(initEval.FuncReturnValue);
            if (initEval.LoopShouldBreak) return res.SuccessBreak();
            if (initEval.LoopShouldContinue) return res.SuccessContinue();
            var scrutinee = initEval.Value ?? NullValue.Null;

            var bindings = new List<(string Name, RuntimeValue Value)>();
            if (!MatchNodeVisitor.TryMatch(node.Pattern, scrutinee, context, bindings, out var err))
            {
                if (err != null) return res.Failure(err);
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "destructuring pattern did not match the initializer value",
                    context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: "pattern would not bind for this value",
                    help: "use 'if let' or 'match' for patterns that may fail at runtime; 'let' destructuring requires an irrefutable pattern"));
            }

            foreach (var (name, value) in bindings)
            {
                context.SymbolTable.SetLocal(name, value);
            }
            return res.Success(NullValue.Null);
        }
    }
}

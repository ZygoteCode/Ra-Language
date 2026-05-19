using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class ForEachNodeVisitor : NodeVisitor<ForEachNode>
    {
        protected sealed override RuntimeResult VisitNode(ForEachNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            string varName = node.VarNameToken.Value?.ToString();

            if (context.SymbolTable.Get(varName) != null)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"Variable '{varName}' is already defined", context
                ));
            }

            var loopContext = context.Copy();
            var loopSymbols = loopContext.SymbolTable!;
            var collection = res.Register(interpreter.Visit(node.CollectionNode, loopContext));
            if (res.Error != null) return res;

            if (collection.Type != RuntimeValueType.List && collection.Type != RuntimeValueType.Set && collection.Type != RuntimeValueType.Map && collection.Type != RuntimeValueType.Tuple)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"Must iter onto a collection", context
                ));
            }

            // Seed the iteration variable so subsequent updates hit the entry directly without
            // walking the parent scope chain.
            loopSymbols.Set(varName, NullValue.Null);
            var iterEntry = loopSymbols.GetEntry(varName);

            // Body scope reused across iterations; cleared between iterations to drop locals.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            if (collection.Type == RuntimeValueType.List)
            {
                var elements = ((ListValue)collection).Elements;
                for (int idx = 0; idx < elements.Count; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res, context)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }
            else if (collection.Type == RuntimeValueType.Set)
            {
                foreach (var element in ((SetValue)collection).Elements)
                {
                    iterEntry!.Value = element;
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res, context)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }
            else if (collection.Type == RuntimeValueType.Tuple)
            {
                var elements = ((TupleValue)collection).Elements;
                for (int idx = 0; idx < elements.Count; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res, context)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }
            else if (collection.Type == RuntimeValueType.Map)
            {
                var pairs = ((MapValue)collection).Pairs;
                for (int idx = 0; idx < pairs.Count; idx++)
                {
                    var pair = pairs[idx];
                    iterEntry!.Value = new TupleValue(new System.Collections.Generic.List<RuntimeValue> { pair.Key, pair.Value });
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res, context)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }

            context.ApplyChangesFrom(bodyContext);
            return res.Success(NullValue.Null);
        }

        private static bool ExecuteBody(ForEachNode node, Context bodyContext, RaLanguage.Interpreter.Runtime.SymbolTable bodySymbols, IInterpreter interpreter, RuntimeResult res, Context outer)
        {
            bodySymbols.Clear();
            bodyContext.ScopeSkipCopy = true;
            res.Register(interpreter.Visit(node.BodyNode, bodyContext));
            if (res.Error != null) return true;
            outer.ApplyChangesFrom(bodyContext);

            if (res.LoopShouldContinue) return false;
            if (res.LoopShouldBreak) return false;
            if (res.ShouldReturn()) return true;
            return false;
        }
    }
}

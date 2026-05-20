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

            // NOTE: an existing outer `varName` is NOT a conflict. The iter variable lives
            // in the loop's own child scope and lexically shadows any outer binding. The
            // outer binding remains intact and reappears once the loop exits.

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

            // Seed the iteration variable LOCALLY in the loop scope so subsequent updates
            // hit this entry directly without walking the parent scope chain and without
            // accidentally rebinding an outer same-named variable.
            loopSymbols.SetLocal(varName, NullValue.Null);
            var iterEntry = loopSymbols.GetLocalEntry(varName);

            // Body scope reused across iterations; cleared between iterations to drop locals.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            if (collection.Type == RuntimeValueType.List)
            {
                var elements = ((ListValue)collection).Elements;
                for (int idx = 0; idx < elements.Count; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }
            else if (collection.Type == RuntimeValueType.Set)
            {
                foreach (var element in ((SetValue)collection).Elements)
                {
                    iterEntry!.Value = element;
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }
            else if (collection.Type == RuntimeValueType.Tuple)
            {
                var elements = ((TupleValue)collection).Elements;
                for (int idx = 0; idx < elements.Count; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res)) return res;
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
                    if (ExecuteBody(node, bodyContext, bodySymbols, interpreter, res)) return res;
                    if (res.LoopShouldBreak) break;
                }
            }

            // No write-back. Outer mutations already propagated via shared SymbolEntry refs;
            // loop locals (the iter var and body locals) die when loopContext / bodyContext
            // become unreachable.
            return res.Success(NullValue.Null);
        }

        private static bool ExecuteBody(ForEachNode node, Context bodyContext, RaLanguage.Interpreter.Runtime.SymbolTable bodySymbols, IInterpreter interpreter, RuntimeResult res)
        {
            bodySymbols.Clear();
            bodyContext.ScopeSkipCopy = true;
            res.Register(interpreter.Visit(node.BodyNode, bodyContext));
            if (res.Error != null) return true;

            if (res.LoopShouldContinue) return false;
            if (res.LoopShouldBreak) return false;
            if (res.ShouldReturn()) return true;
            return false;
        }
    }
}

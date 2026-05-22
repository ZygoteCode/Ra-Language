using System.Threading.Tasks;
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
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ForEachNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            string varName = node.VarNameToken.Value?.ToString();

            // NOTE: an existing outer `varName` is NOT a conflict. The iter variable lives
            // in the loop's own child scope and lexically shadows any outer binding. The
            // outer binding remains intact and reappears once the loop exits.

            var loopContext = context.Copy();
            var loopSymbols = loopContext.SymbolTable!;
            var collection = res.Register(await interpreter.Visit(node.CollectionNode, loopContext));
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

            // ExecuteBody is inlined: RuntimeResult is a struct, so it cannot be
            // passed to a helper that mutates `res` for the caller. The previous
            // implementation lost LoopShouldBreak / LoopShouldContinue across the
            // by-value copy and the loop never exited on `break;`.
            bool shouldBreak = false;
            if (collection.Type == RuntimeValueType.List)
            {
                var elements = ((ListValue)collection).Elements;
                for (int idx = 0; idx < elements.Count && !shouldBreak; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    bodySymbols.Clear();
                    bodyContext.ScopeSkipCopy = true;
                    res.Register(await interpreter.Visit(node.BodyNode, bodyContext));
                    if (res.Error != null) return res;
                    if (res.FuncReturnValue != null) return res;
                    if (res.LoopShouldBreak) { res.LoopShouldBreak = false; shouldBreak = true; continue; }
                    if (res.LoopShouldContinue) { res.LoopShouldContinue = false; continue; }
                }
            }
            else if (collection.Type == RuntimeValueType.Set)
            {
                foreach (var element in ((SetValue)collection).Elements)
                {
                    if (shouldBreak) break;
                    iterEntry!.Value = element;
                    bodySymbols.Clear();
                    bodyContext.ScopeSkipCopy = true;
                    res.Register(await interpreter.Visit(node.BodyNode, bodyContext));
                    if (res.Error != null) return res;
                    if (res.FuncReturnValue != null) return res;
                    if (res.LoopShouldBreak) { res.LoopShouldBreak = false; shouldBreak = true; continue; }
                    if (res.LoopShouldContinue) { res.LoopShouldContinue = false; continue; }
                }
            }
            else if (collection.Type == RuntimeValueType.Tuple)
            {
                var elements = ((TupleValue)collection).Elements;
                for (int idx = 0; idx < elements.Count && !shouldBreak; idx++)
                {
                    iterEntry!.Value = elements[idx];
                    bodySymbols.Clear();
                    bodyContext.ScopeSkipCopy = true;
                    res.Register(await interpreter.Visit(node.BodyNode, bodyContext));
                    if (res.Error != null) return res;
                    if (res.FuncReturnValue != null) return res;
                    if (res.LoopShouldBreak) { res.LoopShouldBreak = false; shouldBreak = true; continue; }
                    if (res.LoopShouldContinue) { res.LoopShouldContinue = false; continue; }
                }
            }
            else if (collection.Type == RuntimeValueType.Map)
            {
                var pairs = ((MapValue)collection).Pairs;
                for (int idx = 0; idx < pairs.Count && !shouldBreak; idx++)
                {
                    var pair = pairs[idx];
                    iterEntry!.Value = new TupleValue(new System.Collections.Generic.List<RuntimeValue> { pair.Key, pair.Value });
                    bodySymbols.Clear();
                    bodyContext.ScopeSkipCopy = true;
                    res.Register(await interpreter.Visit(node.BodyNode, bodyContext));
                    if (res.Error != null) return res;
                    if (res.FuncReturnValue != null) return res;
                    if (res.LoopShouldBreak) { res.LoopShouldBreak = false; shouldBreak = true; continue; }
                    if (res.LoopShouldContinue) { res.LoopShouldContinue = false; continue; }
                }
            }

            return res.Success(NullValue.Null);
        }
    }
}

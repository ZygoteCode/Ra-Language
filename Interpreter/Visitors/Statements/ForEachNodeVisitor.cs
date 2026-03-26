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
        protected override RuntimeResult VisitNode(ForEachNode node, Context context, IInterpreter interpreter)
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

            var newContext = context.Copy();
            var collection = res.Register(interpreter.Visit(node.CollectionNode, newContext));
            if (res.Error != null) return res;

            if (collection.Type != RuntimeValueType.List && collection.Type != RuntimeValueType.Set && collection.Type != RuntimeValueType.Map && collection.Type != RuntimeValueType.Tuple)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"Must iter onto a collection", context
                ));
            }

            List<RuntimeValue> iterElements = new List<RuntimeValue>();

            if (collection.Type == RuntimeValueType.List)
            {
                iterElements = ((ListValue)collection).Elements;
            }
            else if (collection.Type == RuntimeValueType.Set)
            {
                iterElements = ((SetValue)collection).Elements.ToList();
            }
            else if (collection.Type == RuntimeValueType.Tuple)
            {
                iterElements = ((TupleValue)collection).Elements;
            }
            else if (collection.Type == RuntimeValueType.Map)
            {
                MapValue m = (MapValue)collection;

                foreach (var pair in m.Pairs)
                {
                    List<RuntimeValue> values = new List<RuntimeValue>();

                    values.Add(pair.Key);
                    values.Add(pair.Value);

                    iterElements.Add(new TupleValue(values).SetContext(context));
                }
            }

            foreach (RuntimeValue runtimeValue in iterElements)
            {
                newContext.SymbolTable.Set(varName, runtimeValue);
                Context actualContext = newContext.Copy();
                var value = res.Register(interpreter.Visit(node.BodyNode, actualContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
            }

            newContext.Dispose();
            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
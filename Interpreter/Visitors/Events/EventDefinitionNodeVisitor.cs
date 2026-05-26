using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Events;

namespace RaLanguage.Interpreter.Visitors.Events
{
    // Event definitions are NOT dispatched at top level — they live as
    // members on a class / record class / interface / trait body and are
    // consumed by those visitors at type-build time via EventBuilder.
    // A bare EventDefinitionNode reaching the dispatcher is a parser
    // bug; surface it explicitly.
    public class EventDefinitionNodeVisitor : NodeVisitor<EventDefinitionNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(EventDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                node.PositionStart, node.PositionEnd,
                "event declarations may only appear inside a class, record class, interface, or trait body",
                context)));
        }
    }
}

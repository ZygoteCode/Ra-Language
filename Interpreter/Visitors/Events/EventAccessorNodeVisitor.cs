using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Events;

namespace RaLanguage.Interpreter.Visitors.Events
{
    // Event accessor nodes are never dispatched directly — they exist
    // only to carry the per-accessor visibility override inside an
    // EventDefinitionNode. The visitor is registered for the dense
    // _visitors[] table only; calling it is a contract violation.
    public class EventAccessorNodeVisitor : NodeVisitor<EventAccessorNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(EventAccessorNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                node.PositionStart, node.PositionEnd,
                "event accessor nodes are not directly executable — they are consumed by the event-definition build pass",
                context)));
        }
    }
}

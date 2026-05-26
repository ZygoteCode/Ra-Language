using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Properties;

namespace RaLanguage.Interpreter.Visitors.Properties
{
    // Property definitions are *not* dispatched at top level — they live
    // as members on a class / struct / record / interface / trait and are
    // consumed by those visitors at type-build time via
    // PropertyApplier.BuildDescriptor. A bare PropertyDefinitionNode
    // reaching the dispatcher is a parser bug; surface it explicitly.
    public class PropertyDefinitionNodeVisitor : NodeVisitor<PropertyDefinitionNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(PropertyDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                node.PositionStart, node.PositionEnd,
                "property declarations may only appear inside a class, struct, record, interface, or trait body",
                context)));
        }
    }
}

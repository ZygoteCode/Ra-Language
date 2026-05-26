using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Properties;

namespace RaLanguage.Interpreter.Visitors.Properties
{
    // Accessor nodes are never dispatched directly — they are consumed by
    // the property pipeline (see PropertyAccessOps). Their visitor exists
    // only to keep the AstNodeType→visitor table dense (same pattern as
    // ArgumentNodeVisitor / InterfaceMethodSignatureNodeVisitor).
    public class PropertyAccessorNodeVisitor : NodeVisitor<PropertyAccessorNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(PropertyAccessorNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                node.PositionStart, node.PositionEnd,
                "property accessor blocks are part of a property declaration and may not appear on their own",
                context)));
        }
    }
}

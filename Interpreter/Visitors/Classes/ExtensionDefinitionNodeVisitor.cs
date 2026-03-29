using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Classes;

namespace RaLanguage.Interpreter.Visitors.Extensions
{
    public class ExtensionDefinitionNodeVisitor : NodeVisitor<ExtensionDefinitionNode>
    {
        protected override RuntimeResult VisitNode(ExtensionDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var targetName = node.TargetType.Name;
            if (string.IsNullOrWhiteSpace(targetName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid extension target type", context));

            foreach (var method in node.Methods)
            {
                context.Extensions.Register(targetName, method);
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
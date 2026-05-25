using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Classes;

namespace RaLanguage.Interpreter.Visitors.Extensions
{
    public class ExtensionDefinitionNodeVisitor : NodeVisitor<ExtensionDefinitionNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(ExtensionDefinitionNode node, Context context, IInterpreter interpreter)
            => Apply(node, context);

        // Public static entry-point — shared by the AST visitor and the
        // VM's OP_DEFINE_EXTENSION opcode. Avoids interpreter._visitors[]
        // dispatch when running via the VM dispatch loop.
        public static RuntimeResult Apply(ExtensionDefinitionNode node, Context context)
        {
            var res = new RuntimeResult();

            var targetName = node.TargetType.Name;
            if (string.IsNullOrWhiteSpace(targetName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid extension target type", context));

            foreach (var method in node.Methods)
            {
                context.Extensions.Register(targetName, method);
            }

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
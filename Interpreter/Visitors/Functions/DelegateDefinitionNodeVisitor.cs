using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    // Installs a `delegate Name = fn(...) -> R` alias into the active
    // SymbolTable. The binding holds a DelegateTypeValue whose
    // SignatureType is the structural fn TypeDescriptor — `type` is the
    // declared symbol kind so subsequent type-position references
    // (`var x: Name`, `fn f(p: Name)`) resolve through the existing
    // SymbolTable.Get lookup.
    public sealed class DelegateDefinitionNodeVisitor : NodeVisitor<DelegateDefinitionNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(
            DelegateDefinitionNode node,
            Context context,
            IInterpreter interpreter)
            => new ValueTask<RuntimeResult>(Apply(node, context));

        public static RuntimeResult Apply(DelegateDefinitionNode node, Context context)
        {
            var res = new RuntimeResult();
            var name = node.NameTok.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(name))
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    "delegate declaration is missing a name",
                    context));
            }

            // Build + register via the shared helper so the IR-lowered
            // OP_DEFINE_TYPE handler installs a byte-identical DelegateTypeValue.
            var (value, err) = DelegateDefOps.Register(
                name, node.SignatureType, node.GenericTypeParams, node.WhereConstraints,
                node.IsPublic, context, node.PositionStart, node.PositionEnd);
            if (err != null) return res.Failure(err);
            return res.Success(value!);
        }
    }
}

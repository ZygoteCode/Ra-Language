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

            var existing = context.SymbolTable.Get(name);
            if (existing != null && !(existing is DelegateTypeValue))
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"name '{name}' is already declared in this scope",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "duplicate declaration",
                    help: "delegate aliases share the type namespace with classes / structs / enums"));
            }

            var value = new DelegateTypeValue(
                name,
                node.SignatureType,
                node.GenericTypeParams,
                node.WhereConstraints,
                node.IsPublic)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                name,
                value,
                isLet: true,
                declaredType: new TypeDescriptor("type"),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            return res.Success(value);
        }
    }
}

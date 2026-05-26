using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class WithExpressionNodeVisitor : NodeVisitor<WithExpressionNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(WithExpressionNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(WithExpressionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var recvRes = await interpreter.Visit(node.Receiver, context);
            if (recvRes.Error != null) return res.Failure(recvRes.Error);

            var recv = recvRes.Value;
            if (recv is not RecordInstanceValue recordInstance)
            {
                return res.Failure(new RuntimeError(
                    node.Receiver.PositionStart, node.Receiver.PositionEnd,
                    $"'with' expression requires a record instance on the left-hand side; got '{recv?.Type.ToString() ?? "null"}'",
                    context));
            }

            // Build the new instance via a shallow clone. Records are
            // immutable by contract, so aliasing the unchanged field
            // values is safe — the clone owns its own dictionary, the
            // values flow through unchanged. Targeted overrides land
            // on top.
            var clone = recordInstance.ShallowCloneForWith();

            foreach (var (nameTok, valueExpr) in node.Updates)
            {
                var fieldName = nameTok.Value?.ToString() ?? "";

                // Confirm the field belongs to the record's primary
                // shape. Anything else (body-derived properties,
                // typos, etc.) gets a precise diagnostic pinned to
                // the offending pair.
                var pf = recordInstance.Definition.PrimaryFields
                    .FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), fieldName, StringComparison.Ordinal));
                if (pf == null)
                {
                    return res.Failure(new RuntimeError(
                        nameTok.PositionStart, nameTok.PositionEnd,
                        $"Record '{recordInstance.Definition.StructName}' has no primary field '{fieldName}'",
                        context));
                }

                var valRes = await interpreter.Visit(valueExpr, context);
                if (valRes.Error != null) return res.Failure(valRes.Error);
                var newValue = valRes.Value!;

                if (pf.FieldType != null && !TypeSystem.IsAssignable(context, pf.FieldType, newValue))
                {
                    return res.Failure(new RuntimeError(
                        valueExpr.PositionStart, valueExpr.PositionEnd,
                        $"Type mismatch updating record field '{fieldName}': expected '{pf.FieldType}'",
                        context));
                }

                // SetField re-syncs both the dictionary store and the
                // shape-indexed slot array. Honors the original field
                // visibility / declaration kind so introspection
                // remains stable across with-derived siblings.
                clone.SetField(
                    fieldName,
                    newValue,
                    pf.IsPublic,
                    clone.GetFieldDeclarationType(fieldName));
            }

            clone.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
            return res.Success(clone);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared `recv with { f: v, ... }` copy-update logic, factored out of
    // WithExpressionNodeVisitor so the IR-lowered OP_WITH handler and the
    // visitor fallback run byte-identical rules. The caller has already
    // evaluated the receiver and every update value (the IR lays them out in
    // contiguous slots; the visitor visits them up front) — this validates the
    // field names / types against the record's primary shape, shallow-clones,
    // applies the overrides, and returns the new instance.
    public static class WithExpressionOps
    {
        // `values[i]` is the pre-evaluated value for `node.Updates[i]`.
        public static ValueResult Apply(
            RuntimeValue? receiver, WithExpressionNode node,
            IReadOnlyList<RuntimeValue> values, Context context)
        {
            if (receiver is not RecordInstanceValue recordInstance)
            {
                return (null, new RuntimeError(
                    node.Receiver.PositionStart, node.Receiver.PositionEnd,
                    $"'with' expression requires a record instance on the left-hand side; got '{receiver?.Type.ToString() ?? "null"}'",
                    context));
            }

            // Shallow clone: unchanged field values alias through (records are
            // immutable by contract), targeted overrides land on top.
            var clone = recordInstance.ShallowCloneForWith();

            for (int i = 0; i < node.Updates.Count; i++)
            {
                var (nameTok, valueExpr) = node.Updates[i];
                var fieldName = nameTok.Value?.ToString() ?? "";

                var pf = recordInstance.Definition.PrimaryFields
                    .FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), fieldName, System.StringComparison.Ordinal));
                if (pf == null)
                {
                    return (null, new RuntimeError(
                        nameTok.PositionStart, nameTok.PositionEnd,
                        $"Record '{recordInstance.Definition.StructName}' has no primary field '{fieldName}'",
                        context));
                }

                var newValue = values[i];

                if (pf.FieldType != null && !TypeSystem.IsAssignable(context, pf.FieldType, newValue))
                {
                    return (null, new RuntimeError(
                        valueExpr.PositionStart, valueExpr.PositionEnd,
                        $"Type mismatch updating record field '{fieldName}': expected '{pf.FieldType}'",
                        context));
                }

                clone.SetField(
                    fieldName,
                    newValue,
                    pf.IsPublic,
                    clone.GetFieldDeclarationType(fieldName));
            }

            clone.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
            return (clone, null);
        }
    }
}

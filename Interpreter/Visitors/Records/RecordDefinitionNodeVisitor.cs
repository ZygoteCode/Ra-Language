using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Records
{
    public class RecordDefinitionNodeVisitor : NodeVisitor<RecordDefinitionNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(RecordDefinitionNode node, Context context, IInterpreter interpreter)
            => Apply(node, context, interpreter);

        public static RuntimeResult Apply(RecordDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var name = node.NameTok.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid record name", context));

            if (context.SymbolTable.Get(name) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is already defined", context));

            // Synthesize the struct field list from the record's
            // primary fields. The synthesized field carries the same
            // type / default / visibility plus the FINAL declaration
            // type (or VARIABLE for `mut` primary fields), which is
            // what the MemberAssignmentHelper consults when refusing
            // reassignment after construction.
            var syntheticFields = new List<StructFieldDefinitionNode>();
            foreach (var pf in node.PrimaryFields)
            {
                var declType = pf.IsMutable
                    ? VariableDeclarationType.VARIABLE
                    : VariableDeclarationType.FINAL;

                syntheticFields.Add(new StructFieldDefinitionNode(
                    pf.IsPublic,
                    pf.NameTok,
                    pf.FieldType,
                    pf.DefaultValueNode,
                    isStatic: false,
                    isAbstract: false,
                    isOverride: false,
                    declarationType: declType));
            }

            // Records forbid extra `priv var x = ...` style instance
            // fields inside the body — that constraint is enforced
            // by the parser. Constructors are not allowed either; the
            // primary-field list IS the constructor.
            foreach (var method in node.Methods)
            {
                if (method.IsConstructor)
                {
                    return res.Failure(new RuntimeError(
                        method.PositionStart, method.PositionEnd,
                        $"Records cannot declare explicit constructors. The primary-field list defines '{name}'s only constructor.",
                        context));
                }
            }

            // `to_string` shape validation reused from struct/class.
            // The user may override the auto record-style ToString by
            // providing `fn to_string(): string`. The signature must
            // still match the convention.
            ValidateToStringMethod(node, context, ref res);
            if (res.ShouldReturn()) return res;

            var recordValue = (RecordTypeValue) new RecordTypeValue(
                name,
                node.IsPublic,
                node.IsRefRecord,
                node.PrimaryFields,
                syntheticFields,
                node.Methods,
                node.Operators,
                node.GenericTypeParams,
                node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            context.SymbolTable.Set(
                name,
                recordValue,
                isLet: true,
                declaredType: new TypeDescriptor(name),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            // Annotation routing. Records use the struct annotation
            // target kind — there's no semantic difference at the
            // annotation registry level, so reusing the kind keeps
            // existing @-annotations (`@derive`, `@target(Struct)`,
            // etc.) usable on records out of the box.
            var recordTarget = new MetadataTarget(AnnotationTargetKind.Struct, null, name);
            if (node.HasAnnotations)
            {
                var annErr = AnnotationProcessor.Process(node.Annotations, recordTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var pf in node.PrimaryFields)
            {
                if (!pf.HasAnnotations) continue;
                var fieldTarget = new MetadataTarget(AnnotationTargetKind.Field, name, pf.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(pf.Annotations, fieldTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var method in node.Methods)
            {
                if (!method.HasAnnotations) continue;
                var methodTarget = new MetadataTarget(AnnotationTargetKind.Method, name, method.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(method.Annotations, methodTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var op in node.Operators)
            {
                if (!op.HasAnnotations) continue;
                var opTarget = new MetadataTarget(AnnotationTargetKind.Operator, name, op.OperatorTok.Type.ToString());
                var annErr = AnnotationProcessor.Process(op.Annotations, opTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(recordValue);
        }

        private static void ValidateToStringMethod(RecordDefinitionNode node, Context context, ref RuntimeResult res)
        {
            var toStringMethod = node.Methods.FirstOrDefault(m =>
                string.Equals(m.NameTok.Value?.ToString(), "to_string", StringComparison.Ordinal));

            if (toStringMethod == null) return;

            if (toStringMethod.ArgNameToks.Count > 0)
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart, toStringMethod.PositionEnd,
                    "Record method 'to_string' must not have parameters",
                    context));
                return;
            }

            if (toStringMethod.ReturnType == null ||
                !string.Equals(toStringMethod.ReturnType.Name, "string", StringComparison.Ordinal))
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart, toStringMethod.PositionEnd,
                    "Record method 'to_string' must return type 'string'",
                    context));
            }
        }
    }
}

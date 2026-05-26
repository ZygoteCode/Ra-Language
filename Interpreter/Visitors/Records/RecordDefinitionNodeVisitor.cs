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

            // Inheritance is only allowed on `record class`; value records
            // are always sealed by construction.
            RecordTypeValue? baseRecord = null;
            if (node.BaseType != null)
            {
                if (!node.IsRefRecord)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Value record '{name}' cannot inherit from '{node.BaseType.Name}'. Only 'record class' supports inheritance.",
                        context));
                }

                var baseSym = context.SymbolTable.Get(node.BaseType.Name);
                if (baseSym is not RecordTypeValue baseRec)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Base type '{node.BaseType.Name}' of record '{name}' is not a record type",
                        context));
                }

                if (!baseRec.IsRefRecord)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Record '{name}' cannot inherit from value record '{baseRec.StructName}' — only 'record class' bases are inheritable",
                        context));
                }

                if (!baseRec.IsAbstract)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Record '{name}' cannot inherit from non-abstract record '{baseRec.StructName}' — only 'abstract record class' bases are inheritable. Mark the parent with 'abstract' to opt into controlled inheritance.",
                        context));
                }

                baseRecord = baseRec;
            }

            // Compose the effective primary-field list. Base fields come
            // first (so children stack on top), with duplicate-name
            // detection. Inherited fields are not redeclared by the child;
            // adding them in the child header would silently mask the
            // parent and break the merged equality/to_string/deconstruct
            // shape.
            var effectiveFields = new List<RecordPrimaryFieldNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (baseRecord != null)
            {
                foreach (var bp in baseRecord.PrimaryFields)
                {
                    var pname = bp.NameTok.Value?.ToString() ?? "";
                    if (!seen.Add(pname)) continue;
                    effectiveFields.Add(bp);
                }
            }
            foreach (var pf in node.PrimaryFields)
            {
                var pname = pf.NameTok.Value?.ToString() ?? "";
                if (!seen.Add(pname))
                {
                    return res.Failure(new RuntimeError(pf.PositionStart, pf.PositionEnd,
                        $"Record '{name}' redeclares primary field '{pname}' already inherited from '{baseRecord?.StructName}'. Inherited primary fields cannot be shadowed.",
                        context));
                }
                effectiveFields.Add(pf);
            }

            // Synthesize the struct field list from the record's
            // effective primary fields. The synthesized field carries
            // the same type / default / visibility plus the FINAL
            // declaration type (or VARIABLE for `mut` primary fields),
            // which is what the MemberAssignmentHelper consults when
            // refusing reassignment after construction.
            var syntheticFields = new List<StructFieldDefinitionNode>();
            foreach (var pf in effectiveFields)
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

            // Merge methods/operators from base. Children may override
            // by re-declaring; later declarations win. Body method
            // bindings (FrameId / ParamBindings) on base methods are
            // already resolved at base definition time, so the merge is
            // a straight reference copy.
            var mergedMethods = new List<StructMethodDefinitionNode>();
            var mergedOps = new List<Parser.Nodes.Classes.OperatorDefinitionNode>();
            if (baseRecord != null)
            {
                foreach (var bm in baseRecord.Methods) mergedMethods.Add(bm);
                foreach (var bop in baseRecord.Operators) mergedOps.Add(bop);
            }
            foreach (var m in node.Methods)
            {
                int idx = mergedMethods.FindIndex(x => string.Equals(x.NameTok.Value?.ToString(), m.NameTok.Value?.ToString(), StringComparison.Ordinal));
                if (idx >= 0) mergedMethods[idx] = m; else mergedMethods.Add(m);
            }
            foreach (var op in node.Operators) mergedOps.Add(op);

            var recordValue = (RecordTypeValue) new RecordTypeValue(
                name,
                node.IsPublic,
                node.IsRefRecord,
                node.IsAbstract,
                effectiveFields,
                syntheticFields,
                mergedMethods,
                mergedOps,
                node.GenericTypeParams,
                node.WhereConstraints,
                autoEquals: node.AutoEquals,
                autoToString: node.AutoToString)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            recordValue.BaseRecord = baseRecord;

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

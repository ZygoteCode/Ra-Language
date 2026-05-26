using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Records
{
    // Runtime definition value for a record. Inherits from
    // StructTypeValue so all the existing struct machinery (method
    // binding via BoundStructMethodValue, hidden-class shape index,
    // operator overload table, generic-type-parameter list) works
    // without duplication. The construction path, however, is custom:
    // records have no user-defined constructors — primary-field
    // positions ARE the constructor. Execute(args) skips the struct
    // ctor lookup entirely and binds primary fields by position with
    // optional named-argument refinement.
    public sealed class RecordTypeValue : StructTypeValue
    {
        public bool IsRefRecord { get; }
        public List<RecordPrimaryFieldNode> PrimaryFields { get; }

        public RecordTypeValue(
            string recordName,
            bool isPublic,
            bool isRefRecord,
            List<RecordPrimaryFieldNode> primaryFields,
            List<StructFieldDefinitionNode> syntheticFields,
            List<StructMethodDefinitionNode> bodyMethods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams,
            List<WhereConstraintNode>? whereConstraints)
            : base(recordName, isPublic, syntheticFields, bodyMethods, operators, genericTypeParams, whereConstraints)
        {
            IsRefRecord = isRefRecord;
            PrimaryFields = primaryFields;
        }

        public override RuntimeValueType Type => RuntimeValueType.RecordType;
        public override bool IsCopy => true;

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteRecord(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        // The struct base exposes ExecuteWithNamedArgs as a regular
        // method, not a virtual one — so when a caller has a
        // StructTypeValue reference and invokes ExecuteWithNamedArgs
        // statically, the base implementation runs. The
        // FunctionCallExecutor dispatches through Execute(...) which
        // we DO override above, so the named-arg path also routes
        // through ExecuteRecord. The `new` here is intentional and
        // matches the C# slot-replacement: when callers hold a
        // RecordTypeValue reference they get the record-aware impl.
        public new async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
            => await ExecuteRecord(positionalArgs, namedArgs);

        private async ValueTask<RuntimeResult> ExecuteRecord(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            if (positionalArgs.Count > PrimaryFields.Count)
            {
                return res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"Too many positional arguments for record '{StructName}': expected at most {PrimaryFields.Count}, got {positionalArgs.Count}",
                    Context));
            }

            var instance = (RecordInstanceValue)new RecordInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            var positionalConsumed = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < PrimaryFields.Count; i++)
            {
                var pf = PrimaryFields[i];
                var fieldName = pf.NameTok.Value?.ToString() ?? "";
                RuntimeValue? value = null;

                if (i < positionalArgs.Count)
                {
                    if (namedArgs.ContainsKey(fieldName))
                    {
                        return res.Failure(new RuntimeError(
                            PositionStart, PositionEnd,
                            $"Argument for field '{fieldName}' of record '{StructName}' was supplied both positionally and by name",
                            Context));
                    }

                    value = positionalArgs[i];
                    positionalConsumed.Add(fieldName);
                }
                else if (namedArgs.TryGetValue(fieldName, out var named))
                {
                    value = named;
                }
                else if (pf.DefaultValueNode != null)
                {
                    var initRes = await new Interpreter().Visit(pf.DefaultValueNode, Context);
                    if (initRes.Error != null) return res.Failure(initRes.Error);
                    value = initRes.Value;
                }
                else
                {
                    return res.Failure(new RuntimeError(
                        PositionStart, PositionEnd,
                        $"Missing required field '{fieldName}' when constructing record '{StructName}'",
                        Context));
                }

                if (pf.FieldType != null && !TypeSystem.IsAssignable(Context, pf.FieldType, value!))
                {
                    return res.Failure(new RuntimeError(
                        pf.NameTok.PositionStart, pf.NameTok.PositionEnd,
                        $"Type mismatch for primary field '{fieldName}' of record '{StructName}': expected '{pf.FieldType}'",
                        Context));
                }

                instance.SetField(
                    fieldName,
                    value ?? NullValue.Null,
                    pf.IsPublic,
                    pf.IsMutable ? VariableDeclarationType.VARIABLE : VariableDeclarationType.FINAL);
            }

            // Any leftover named-args that did not correspond to a
            // primary field are programmer errors — surface them up
            // front rather than silently dropping the value.
            foreach (var kv in namedArgs)
            {
                if (positionalConsumed.Contains(kv.Key)) continue;
                bool matched = false;
                for (int i = 0; i < PrimaryFields.Count; i++)
                {
                    var name = PrimaryFields[i].NameTok.Value?.ToString() ?? "";
                    if (string.Equals(name, kv.Key, StringComparison.Ordinal)) { matched = true; break; }
                }
                if (!matched)
                {
                    return res.Failure(new RuntimeError(
                        PositionStart, PositionEnd,
                        $"Unknown named argument '{kv.Key}' for record '{StructName}'",
                        Context));
                }
            }

            return res.Success(instance);
        }

        // Record type definitions have nominal identity — two record
        // instances compare equal only when they share the exact same
        // declared type. Cloning the definition (as IsCopy=true would
        // imply on Aliased()) breaks that identity and turns
        // `Point(1,2) == Point(1,2)` into false because each construction
        // observes a fresh definition reference. Return `this` so the
        // definition stays a singleton — the type value is conceptually
        // immutable.
        public override RuntimeValue Copy() => this;

        public override string ToString() => IsRefRecord ? $"<record class {StructName}>" : $"<record {StructName}>";
    }
}

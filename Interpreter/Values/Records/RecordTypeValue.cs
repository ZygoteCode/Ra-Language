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
    //
    // Inheritance model: only `record class` may participate (value
    // records are always sealed). `abstract record class` parents are
    // non-instantiable; their primary fields are PREPENDED to the
    // child's primary-field list at definition time so the
    // synthesized layout, equality, to_string, and deconstruct all see
    // the merged field set without runtime indirection. Structural
    // equality continues to require the EXACT same Definition reference
    // — parent vs child instances are never equal, which sidesteps
    // the EqualityContract trap that C# carries in its record
    // hierarchy.
    public sealed class RecordTypeValue : StructTypeValue
    {
        public bool IsRefRecord { get; }
        public bool IsAbstract { get; }
        public RecordTypeValue? BaseRecord { get; set; }

        // Primary fields visible to the constructor and the auto-
        // generated equality / to_string / deconstruct passes. When
        // BaseRecord is set, the field list is the concatenation of
        // base.PrimaryFields and the child-declared fields — so
        // children "see" parent fields by position automatically.
        public List<RecordPrimaryFieldNode> PrimaryFields { get; }

        // Auto-derive control flags. Default true; the DeriveTransformer
        // can flip them via @derive(equals=false, to_string=false).
        public bool AutoEquals { get; }
        public bool AutoToString { get; }

        public RecordTypeValue(
            string recordName,
            bool isPublic,
            bool isRefRecord,
            bool isAbstract,
            List<RecordPrimaryFieldNode> primaryFields,
            List<StructFieldDefinitionNode> syntheticFields,
            List<StructMethodDefinitionNode> bodyMethods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams,
            List<WhereConstraintNode>? whereConstraints,
            bool autoEquals = true,
            bool autoToString = true)
            : base(recordName, isPublic, syntheticFields, bodyMethods, operators, genericTypeParams, whereConstraints)
        {
            IsRefRecord = isRefRecord;
            IsAbstract = isAbstract;
            PrimaryFields = primaryFields;
            AutoEquals = autoEquals;
            AutoToString = autoToString;
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

            if (IsAbstract)
            {
                return res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"Cannot instantiate abstract record '{StructName}'",
                    Context));
            }

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

            // After primary fields are bound, walk this record's
            // body-declared properties and apply their default
            // initialisers. Lazy properties skip; computed have no
            // backing.
            foreach (var prop in Properties)
            {
                if (prop.IsAbstract) continue;
                if (!prop.HasBacking) continue;
                if (prop.IsLazy) continue;

                RuntimeValue val = NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
                if (prop.DefaultValueNode != null)
                {
                    var initRes = await new Interpreter().Visit(prop.DefaultValueNode, Context);
                    if (initRes.Error != null) return res.Failure(initRes.Error);
                    val = initRes.Value ?? val;
                }
                instance.SetField(prop.Name, val, prop.IsPublic, Parser.Nodes.Variables.VariableDeclarationType.VARIABLE);
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

        public override string ToString()
        {
            string kind = IsRefRecord ? (IsAbstract ? "abstract record class" : "record class") : "record";
            return $"<{kind} {StructName}>";
        }
    }
}

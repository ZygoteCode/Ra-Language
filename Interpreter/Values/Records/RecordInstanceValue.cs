using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Values.Records
{
    // Runtime instance of a record.
    //
    // Inherits from StructInstanceValue so member access, method
    // binding, and bound-method execution all flow through the
    // existing struct dispatch. The record-specific surface area is:
    //
    //   * Type            → RecordInstance (distinct from
    //                       StructInstance so visitors can branch).
    //   * IsCopy          → mirrors the definition's IsRefRecord flag:
    //                       value records (default) are copy-like,
    //                       reference records alias on read.
    //   * Equality        → structural, deep, scoped to the exact
    //                       declared record type. Two instances of
    //                       different record types are never equal,
    //                       even if their field shapes line up.
    //   * to_string       → record-style "Name(field=value, ...)"
    //                       unless the user provided their own
    //                       to_string body, in which case the user
    //                       impl wins.
    //   * Copy            → returns a RecordInstanceValue (preserves
    //                       identity through the with-expression
    //                       clone path).
    public sealed class RecordInstanceValue : StructInstanceValue
    {
        public new RecordTypeValue Definition { get; }

        public RecordInstanceValue(RecordTypeValue definition) : base(definition)
        {
            Definition = definition;
        }

        public override RuntimeValueType Type => RuntimeValueType.RecordInstance;
        public override bool IsCopy => !Definition.IsRefRecord;

        public override ValueResult GetComparisonEq(RuntimeValue other) => StructuralEq(other, strict: false);

        public override ValueResult GetComparisonNe(RuntimeValue other)
        {
            var (eqVal, err) = StructuralEq(other, strict: false);
            if (err != null) return (null, err);
            if (eqVal is BooleanValue bv) return (BooleanValue.Of(!bv.Value), null);
            return (BooleanValue.Of(true), null);
        }

        public override ValueResult GetComparisonStrictEq(RuntimeValue other) => StructuralEq(other, strict: true);

        public override ValueResult GetComparisonStrictNe(RuntimeValue other)
        {
            var (eqVal, err) = StructuralEq(other, strict: true);
            if (err != null) return (null, err);
            if (eqVal is BooleanValue bv) return (BooleanValue.Of(!bv.Value), null);
            return (BooleanValue.Of(true), null);
        }

        // Structural equality. Two record instances are equal when
        //   1. they share the EXACT same record-type identity
        //      (records are nominal — `record A(x: int)` and
        //      `record B(x: int)` are never equal even though their
        //      shape lines up), and
        //   2. each primary field compares equal pairwise via the
        //      field's own GetComparisonEq / GetComparisonStrictEq.
        //
        // The strict-eq variant propagates strictness recursively so
        // nested records also use ===.
        //
        // User-overridden `operator ==` on the record body short-
        // circuits this path: we first ask the struct-operator
        // dispatcher whether the user provided a custom == /
        // !=; if so, that overload wins.
        private ValueResult StructuralEq(RuntimeValue other, bool strict)
        {
            // User-provided operator overload, if any, takes precedence.
            // Mirrors how StructInstanceValue.TryOperatorDispatch finds
            // the operator on the struct definition.
            var opTok = strict ? TokenType.STRICT_EE : TokenType.EE;
            var customOp = Definition.ResolveOperator(opTok, ResolveOtherTypeName(other));
            if (customOp != null)
            {
                // Defer to the struct base; it already handles the
                // user-operator dispatch and falls back to the default
                // value-by-value Equals when there's no overload.
                return strict ? base.GetComparisonStrictEq(other) : base.GetComparisonEq(other);
            }

            if (other is not RecordInstanceValue r)
                return (BooleanValue.Of(false), null);

            if (!ReferenceEquals(r.Definition, Definition))
                return (BooleanValue.Of(false), null);

            for (int i = 0; i < Definition.PrimaryFields.Count; i++)
            {
                var name = Definition.PrimaryFields[i].NameTok.Value?.ToString() ?? "";
                if (!Fields.TryGetValue(name, out var lhs)) lhs = NullValue.Null;
                if (!r.Fields.TryGetValue(name, out var rhs)) rhs = NullValue.Null;

                var (cmp, err) = strict ? lhs.GetComparisonStrictEq(rhs) : lhs.GetComparisonEq(rhs);
                if (err != null)
                {
                    // GetComparisonEq returns an IllegalOperation when
                    // the two sides aren't compatible — but for record
                    // equality we want a definitive false rather than
                    // an error, so swallow the diag and report
                    // "not equal".
                    return (BooleanValue.Of(false), null);
                }

                if (cmp is BooleanValue b && !b.Value)
                    return (BooleanValue.Of(false), null);
                if (cmp is NumberValue n && n.Value.Equals(BigNumber.Zero))
                    return (BooleanValue.Of(false), null);
            }

            return (BooleanValue.Of(true), null);
        }

        private static string ResolveOtherTypeName(RuntimeValue other)
        {
            if (other is RecordInstanceValue r) return r.Definition.StructName;
            if (other is StructInstanceValue s) return s.Definition.StructName;
            return other.Type.ToString();
        }

        public override RuntimeValue Copy()
        {
            var copy = new RecordInstanceValue(Definition);
            foreach (var kv in Fields)
            {
                copy.Fields[kv.Key] = kv.Value.IsCopy ? kv.Value.Copy() : kv.Value;
                copy.FieldPublicity[kv.Key] = FieldPublicity.TryGetValue(kv.Key, out var p) && p;
                copy.FieldDeclarationTypes[kv.Key] = FieldDeclarationTypes.TryGetValue(kv.Key, out var dt) ? dt : VariableDeclarationType.VARIABLE;
                int idx = Definition.GetFieldSlotIndex(kv.Key);
                if ((uint)idx < (uint)copy.FieldSlots.Length) copy.FieldSlots[idx] = copy.Fields[kv.Key];
            }

            return copy.SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        // Internal — used by WithExpression to produce a sibling
        // instance that shares all field values by reference (shallow
        // clone), then receives targeted overrides. Distinct from
        // Copy() which mirrors IsCopy semantics for the type.
        internal RecordInstanceValue ShallowCloneForWith()
        {
            var clone = new RecordInstanceValue(Definition);
            foreach (var kv in Fields)
            {
                clone.Fields[kv.Key] = kv.Value;
                clone.FieldPublicity[kv.Key] = FieldPublicity.TryGetValue(kv.Key, out var p) && p;
                clone.FieldDeclarationTypes[kv.Key] = FieldDeclarationTypes.TryGetValue(kv.Key, out var dt) ? dt : VariableDeclarationType.VARIABLE;
                int idx = Definition.GetFieldSlotIndex(kv.Key);
                if ((uint)idx < (uint)clone.FieldSlots.Length) clone.FieldSlots[idx] = kv.Value;
            }
            clone.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return clone;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Definition.StructName);
            sb.Append('(');
            for (int i = 0; i < Definition.PrimaryFields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var name = Definition.PrimaryFields[i].NameTok.Value?.ToString() ?? "";
                sb.Append(name);
                sb.Append('=');
                if (Fields.TryGetValue(name, out var v))
                {
                    sb.Append(RaLanguage.Utilities.StringConversionUtility.ConvertToString(v));
                }
                else
                {
                    sb.Append("null");
                }
            }
            sb.Append(')');
            return sb.ToString();
        }
    }
}

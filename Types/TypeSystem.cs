using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Types
{
    public static class TypeSystem
    {
        public static bool IsAssignable(Context context, TypeDescriptor target, RuntimeValue value)
        {
            if (target == null) return true;
            if (string.Equals(target.Name, "any", StringComparison.Ordinal)) return true;
            if (target.IsTypeParameter) return true;

            // Union targets accept a value when at least one alternative
            // accepts it. Walking the member list short-circuits on first
            // match, so the cost is proportional to the chosen branch, not
            // the union's arity. `null` is treated as a regular runtime
            // value here — a union has to include a null-permitting member
            // (typically `null`) for null values to land cleanly.
            if (target.IsUnionType && target.UnionMembers != null)
            {
                for (int i = 0; i < target.UnionMembers.Count; i++)
                    if (IsAssignable(context, target.UnionMembers[i], value)) return true;
                return false;
            }

            // Structural function-type check. Any BaseFunctionValue qualifies
            // for the arity check; declared parameter / return types apply
            // contravariant (params) / covariant (return) rules. Unknown slots
            // (null or "any") match anything.
            if (target.IsFunctionType)
            {
                return IsAssignableToFunctionType(context, target, value);
            }

            if (target.IsRefType)
            {
                if (value.Type != RuntimeValueType.Reference)
                    return false;
                
                if (target.RefElementType != null)
                {
                    if (value is IReferenceValue refValue)
                    {
                        try
                        {
                            var actualValue = refValue.Value;
                            if (actualValue != null)
                            {
                                return IsAssignable(context, target.RefElementType, actualValue);
                            }
                        }
                        catch
                        {
                            return false;
                        }
                    }
                    return false;
                }
                return true;
            }

            if (value.Type == RuntimeValueType.Reference)
            {
                return false;
            }

            if (string.Equals(target.Name, "string", StringComparison.Ordinal))
                return true;

            var symbol = context?.SymbolTable?.Get(target.Name);

            if (symbol is EnumTypeValue enumType)
            {
                if (value.Type != RuntimeValueType.Enum) return false;
                var ev = (EnumValue)value;
                return string.Equals(ev.EnumName, enumType.EnumName, StringComparison.Ordinal);
            }

            if (symbol is StructTypeValue structType)
            {
                // RecordTypeValue inherits from StructTypeValue (records
                // reuse the struct field-shape + method-binding machinery
                // at runtime), and RecordInstanceValue likewise inherits
                // from StructInstanceValue. The nominal-name check below
                // already guards against accidental cross-type aliasing,
                // so widening the runtime-type guard to accept both kinds
                // is exactly the right thing.
                if (value.Type == RuntimeValueType.StructInstance || value.Type == RuntimeValueType.RecordInstance)
                {
                    return string.Equals(((StructInstanceValue)value).Definition.StructName, structType.StructName, StringComparison.Ordinal);
                }
                return false;
            }

            if (symbol is ClassTypeValue targetClass)
            {
                if (value.Type == RuntimeValueType.ClassInstance)
                {
                    var inst = (ClassInstanceValue)value;
                    return inst.Definition.InheritsFrom(targetClass.ClassName);
                }

                return false;
            }

            if (symbol is InterfaceTypeValue iface)
            {
                return SatisfiesInterface(context, iface, value);
            }

            if (symbol is TraitTypeValue trait)
            {
                return SatisfiesTrait(context, trait, value);
            }

            // Delegate aliases unfold to their structural fn signature. The
            // delegate's declared generic parameters get bound from the
            // target's GenericArgs so `Predicate<int>` checks against
            // `fn(int) -> bool` rather than `fn(T) -> bool`.
            if (symbol is RaLanguage.Interpreter.Values.Functions.DelegateTypeValue del)
            {
                var instantiated = del.InstantiateWith(target.GenericArgs);
                if (instantiated.IsFunctionType)
                    return IsAssignableToFunctionType(context, instantiated, value);
                return IsAssignable(context, instantiated, value);
            }

            // Backward-compat: a type name that is not a known primitive and not a registered
            // symbol (class/struct/enum/interface/trait) is treated as an opaque/unresolved
            // type-parameter-like name. This preserves the historical behavior where any
            // uppercase identifier acted as a type parameter, so existing programs that use
            // names like "String" continue to type-check.
            if (symbol == null && IsLikelyUnresolvedUserType(target))
                return true;

            switch (value.Type)
            {
                case RuntimeValueType.Number:
                case RuntimeValueType.Integer:
                case RuntimeValueType.Long:
                case RuntimeValueType.Float:
                case RuntimeValueType.Double:
                case RuntimeValueType.UnsignedInteger:
                case RuntimeValueType.UnsignedLong:
                case RuntimeValueType.Short:
                case RuntimeValueType.UnsignedShort:
                case RuntimeValueType.Int128:
                case RuntimeValueType.UnsignedInt128:
                case RuntimeValueType.Decimal:
                case RuntimeValueType.Byte:
                    return string.Equals(target.Name, "number", StringComparison.Ordinal)
                        || string.Equals(target.Name, "int", StringComparison.Ordinal)
                        || string.Equals(target.Name, "long", StringComparison.Ordinal)
                        || string.Equals(target.Name, "float", StringComparison.Ordinal)
                        || string.Equals(target.Name, "double", StringComparison.Ordinal)
                        || string.Equals(target.Name, "uint", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ulong", StringComparison.Ordinal)
                        || string.Equals(target.Name, "short", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ushort", StringComparison.Ordinal)
                        || string.Equals(target.Name, "int128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "uint128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "decimal", StringComparison.Ordinal)
                        || string.Equals(target.Name, "byte", StringComparison.Ordinal);
                case RuntimeValueType.String:
                    return string.Equals(target.Name, "string", StringComparison.Ordinal);
                case RuntimeValueType.Boolean:
                    return string.Equals(target.Name, "bool", StringComparison.Ordinal);
                case RuntimeValueType.Null:
                    return true;
                case RuntimeValueType.List:
                    if (!string.Equals(target.Name, "list", StringComparison.Ordinal)) return false;
                    var l = (ListValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    var inner = target.GenericArgs[0];
                    foreach (var el in l.Elements)
                        if (!IsAssignable(context, inner.Substitute(new Dictionary<string, TypeDescriptor>()), el)) return false;
                    return true;
                case RuntimeValueType.Set:
                    if (!string.Equals(target.Name, "set", StringComparison.Ordinal)) return false;
                    var s = (SetValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    var sin = target.GenericArgs[0];
                    foreach (var el in s.Elements)
                        if (!IsAssignable(context, sin, el)) return false;
                    return true;
                case RuntimeValueType.Map:
                    if (!string.Equals(target.Name, "map", StringComparison.Ordinal)) return false;
                    var m = (MapValue)value;
                    if (target.GenericArgs.Count < 2) return true;
                    var kT = target.GenericArgs[0];
                    var vT = target.GenericArgs[1];
                    foreach (var kv in m.Pairs)
                    {
                        if (!IsAssignable(context, kT, kv.Key) || !IsAssignable(context, vT, kv.Value)) return false;
                    }
                    return true;
                case RuntimeValueType.Tuple:
                    if (!string.Equals(target.Name, "tuple", StringComparison.Ordinal)) return false;
                    var t = (TupleValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    if (target.GenericArgs.Count != t.Elements.Count) return false;
                    for (int i = 0; i < t.Elements.Count; i++)
                        if (!IsAssignable(context, target.GenericArgs[i], t.Elements[i])) return false;
                    return true;
                case RuntimeValueType.Function:
                    return string.Equals(target.Name, "function", StringComparison.Ordinal);
                case RuntimeValueType.Task:
                    return string.Equals(target.Name, "task", StringComparison.Ordinal);
                case RuntimeValueType.Channel:
                    return string.Equals(target.Name, "channel", StringComparison.Ordinal);
                case RuntimeValueType.Stream:
                    return string.Equals(target.Name, "stream", StringComparison.Ordinal)
                        || string.Equals(target.Name, "Stream", StringComparison.Ordinal);
                case RuntimeValueType.AsyncStream:
                    return string.Equals(target.Name, "async_stream", StringComparison.Ordinal)
                        || string.Equals(target.Name, "AsyncStream", StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        // Structural compatibility between a structural `fn(...) -> R` target
        // type and any BaseFunctionValue at the value side. Rules:
        //   - Arity: declared params count must match the target's expected
        //     arity. Variadic functions accept any arity >= the fixed prefix.
        //   - Params are contravariant: target slot must be assignable from
        //     the value's declared slot type (or the value declares `any`).
        //   - Return is covariant: value's declared return must be assignable
        //     to the target's declared return.
        //   - Unknown slots (null / `any`) on either side are wildcards.
        //   - Built-in / opaque callables with no declared signature get the
        //     arity check only — the per-call type system still enforces
        //     types at invocation time. This keeps assignment to `fn(...)`
        //     from forcing every built-in to gain declared parameter types.
        private static bool IsAssignableToFunctionType(Context context, TypeDescriptor target, RuntimeValue value)
        {
            if (value == null) return false;
            if (value.Type == RuntimeValueType.Null) return true;
            if (!(value is RaLanguage.Interpreter.Values.Functions.BaseFunctionValue bfn))
                return false;

            var expectedParams = target.FunctionParamTypes ?? new List<TypeDescriptor>();
            var expectedRet = target.FunctionReturnType;

            // Pull a declared signature off the value when we have one.
            // FunctionValue and the bound-method values all expose
            // ArgTypes/ReturnType through reflection on a small interface
            // we add here: callables that don't carry types simply skip
            // the parameter/return check (arity check still runs).
            List<TypeDescriptor?>? actualParamTypes;
            TypeDescriptor? actualReturnType;
            bool actualHasVarArgs;
            TypeDescriptor? actualVarArgType;
            int actualArity;
            if (!TryGetCallableSignature(bfn, out actualParamTypes, out actualReturnType,
                out actualHasVarArgs, out actualVarArgType, out actualArity))
            {
                // Opaque callable: accept any arity, defer to per-call typing.
                return true;
            }

            // Arity check.
            if (actualHasVarArgs)
            {
                if (expectedParams.Count < actualArity) return false;
            }
            else
            {
                if (expectedParams.Count != actualArity) return false;
            }

            // Param contravariance.
            for (int i = 0; i < expectedParams.Count; i++)
            {
                var ep = expectedParams[i];
                TypeDescriptor? ap = null;
                if (i < actualArity)
                {
                    ap = actualParamTypes != null && i < actualParamTypes.Count ? actualParamTypes[i] : null;
                }
                else if (actualHasVarArgs)
                {
                    ap = actualVarArgType;
                }
                if (ap == null) continue; // value side wildcard
                if (string.Equals(ap.Name, "any", StringComparison.Ordinal)) continue;
                if (ep == null) continue;
                if (string.Equals(ep.Name, "any", StringComparison.Ordinal)) continue;
                // Contravariance: the target's accepted parameter type must
                // be assignable into the value's declared parameter type.
                // Simulate with a synthetic "type-only" check: if ep equals
                // ap, OK; otherwise require IsAssignableType(ep, ap).
                if (!IsAssignableType(context, ap, ep)) return false;
            }

            // Return covariance.
            if (expectedRet != null
                && !string.Equals(expectedRet.Name, "any", StringComparison.Ordinal)
                && actualReturnType != null
                && !string.Equals(actualReturnType.Name, "any", StringComparison.Ordinal))
            {
                if (!IsAssignableType(context, expectedRet, actualReturnType)) return false;
            }

            return true;
        }

        // Type-vs-type assignability for callable variance checks. Mirrors
        // IsAssignable(Context, TypeDescriptor, RuntimeValue) but with the
        // RHS being a TypeDescriptor — no runtime value to inspect.
        public static bool IsAssignableType(Context context, TypeDescriptor target, TypeDescriptor source)
        {
            if (target == null || source == null) return true;
            if (target.Equals(source)) return true;
            if (string.Equals(target.Name, "any", StringComparison.Ordinal)) return true;
            if (string.Equals(source.Name, "any", StringComparison.Ordinal)) return true;
            if (target.IsTypeParameter || source.IsTypeParameter) return true;

            // Union-vs-anything subtyping:
            //   - source = T1 | ... | Tn  ⇒  target must accept *every* alternative;
            //   - target = U1 | ... | Um  ⇒  source must fit *some* alternative.
            // The source side is checked first because that yields the
            // strongest type guarantee — if every alternative of a union
            // source fits the target, the target is at least as wide as the
            // source for any concrete value in it.
            if (source.IsUnionType && source.UnionMembers != null)
            {
                foreach (var s in source.UnionMembers)
                    if (!IsAssignableType(context, target, s)) return false;
                return true;
            }
            if (target.IsUnionType && target.UnionMembers != null)
            {
                foreach (var t in target.UnionMembers)
                    if (IsAssignableType(context, t, source)) return true;
                return false;
            }

            // Structural fn-vs-fn (full variance walk).
            if (target.IsFunctionType && source.IsFunctionType)
            {
                var lp = target.FunctionParamTypes ?? new List<TypeDescriptor>();
                var rp = source.FunctionParamTypes ?? new List<TypeDescriptor>();
                if (lp.Count != rp.Count) return false;
                for (int i = 0; i < lp.Count; i++)
                {
                    // contravariant: source param must accept target param
                    if (!IsAssignableType(context, rp[i], lp[i])) return false;
                }
                if (target.FunctionReturnType != null && source.FunctionReturnType != null)
                {
                    if (!IsAssignableType(context, target.FunctionReturnType, source.FunctionReturnType)) return false;
                }
                return true;
            }

            // Numeric family is interchangeable for callable-signature
            // purposes — matches the runtime IsAssignable behavior where
            // any numeric value lands in any numeric slot.
            if (IsNumericTypeName(target.Name) && IsNumericTypeName(source.Name)) return true;

            // Nominal generic walk.
            if (!string.Equals(target.Name, source.Name, StringComparison.Ordinal)) return false;
            if (target.GenericArgs.Count != source.GenericArgs.Count) return false;
            for (int i = 0; i < target.GenericArgs.Count; i++)
                if (!IsAssignableType(context, target.GenericArgs[i], source.GenericArgs[i])) return false;
            return true;
        }

        private static bool IsNumericTypeName(string name)
        {
            switch (name)
            {
                case "number":
                case "int":
                case "long":
                case "float":
                case "double":
                case "uint":
                case "ulong":
                case "short":
                case "ushort":
                case "int128":
                case "uint128":
                case "decimal":
                case "byte":
                    return true;
                default:
                    return false;
            }
        }

        // Pulls a declared signature off any BaseFunctionValue subtype.
        // Returns false for opaque callables (built-ins, partial wrappers
        // that erase their target's types, …) — callers treat that as
        // "skip parameter/return checks, arity-only contract."
        private static bool TryGetCallableSignature(
            RaLanguage.Interpreter.Values.Functions.BaseFunctionValue bfn,
            out List<TypeDescriptor?>? paramTypes,
            out TypeDescriptor? returnType,
            out bool hasVarArgs,
            out TypeDescriptor? varArgType,
            out int arity)
        {
            paramTypes = null;
            returnType = null;
            hasVarArgs = false;
            varArgType = null;
            arity = 0;

            if (bfn is RaLanguage.Interpreter.Values.Functions.FunctionValue fv)
            {
                paramTypes = fv.ArgTypes;
                returnType = fv.ReturnType;
                hasVarArgs = fv.HasVarArgs;
                varArgType = fv.VarArgType;
                arity = fv.ArgNames?.Count ?? 0;
                return true;
            }

            // The bound-method values keep their FunctionDefinitionNode handy;
            // peek at its declared signature for variance.
            if (bfn is RaLanguage.Interpreter.Values.Primitives.BoundClassMethodValue bcm
                && bcm.MethodNode != null)
            {
                paramTypes = bcm.MethodNode.ArgTypes;
                returnType = bcm.MethodNode.ReturnType;
                hasVarArgs = bcm.MethodNode.HasVarArgs;
                varArgType = bcm.MethodNode.VarArgType;
                arity = bcm.MethodNode.ArgNames?.Count ?? 0;
                return true;
            }

            if (bfn is RaLanguage.Interpreter.Values.Classes.BoundClassMethodGroupValue bmg
                && bmg.Candidates != null && bmg.Candidates.Count == 1)
            {
                var only = bmg.Candidates[0];
                paramTypes = only.ArgTypes;
                returnType = only.ReturnType;
                hasVarArgs = only.HasVarArgs;
                varArgType = only.VarArgType;
                arity = only.ArgNames?.Count ?? 0;
                return true;
            }

            return false;
        }

        public static Dictionary<string, TypeDescriptor>? UnifyGenericParameters(TypeDescriptor formal, TypeDescriptor actual, Dictionary<string, TypeDescriptor>? bindings = null)
        {
            if (bindings == null) bindings = new Dictionary<string, TypeDescriptor>();

            if (formal.IsTypeParameter)
            {
                var name = formal.TypeParameterName;
                if (bindings.TryGetValue(name, out var existing))
                {
                    if (!existing.Equals(actual)) return null;
                }
                else
                {
                    bindings[name] = actual;
                }
                return bindings;
            }

            if (!string.Equals(formal.Name, actual.Name, StringComparison.Ordinal)) return null;
            if (formal.GenericArgs.Count != actual.GenericArgs.Count) return null;

            for (int i = 0; i < formal.GenericArgs.Count; i++)
            {
                var sub = UnifyGenericParameters(formal.GenericArgs[i], actual.GenericArgs[i], bindings);
                if (sub == null) return null;
            }

            return bindings;
        }

        private static readonly HashSet<string> _knownPrimitiveTypeNames = new(StringComparer.Ordinal)
        {
            "any", "number", "string", "bool", "null",
            "int", "long", "float", "double",
            "uint", "ulong", "short", "ushort",
            "int128", "uint128", "decimal", "byte",
            "list", "set", "map", "tuple", "function", "type",
            "task", "channel", "stream"
        };

        private static bool IsLikelyUnresolvedUserType(TypeDescriptor target)
        {
            if (target == null) return false;
            if (target.IsTypeParameter) return true;
            if (string.IsNullOrEmpty(target.Name)) return false;
            if (_knownPrimitiveTypeNames.Contains(target.Name)) return false;
            return true;
        }

        public static string? ValidateWhereConstraints(Dictionary<string, TypeDescriptor> bindings, List<WhereConstraintNode> constraints)
        {
            if (constraints == null || constraints.Count == 0) return null;

            foreach (var constraint in constraints)
            {
                if (!bindings.TryGetValue(constraint.ParameterName, out var boundType))
                {
                    return $"Generic parameter '{constraint.ParameterName}' is not bound";
                }

                var expected = constraint.ConstraintType.Substitute(bindings);

                if (RaLanguage.Interpreter.Runtime.Annotations.ConstraintAnnotationRegistry.IsConstraintAnnotation(expected.Name))
                {
                    if (!RaLanguage.Interpreter.Runtime.Annotations.ConstraintAnnotationRegistry.IsSatisfied(expected.Name, boundType))
                    {
                        return $"Generic parameter '{constraint.ParameterName}' is bound to '{boundType}', but constraint '{expected.Name}' is not satisfied";
                    }
                    continue;
                }

                if (!StrictTypeEquals(boundType, expected))
                {
                    return $"Generic parameter '{constraint.ParameterName}' is bound to '{boundType}', but the 'where' clause requires exactly '{expected}'";
                }
            }

            return null;
        }

        // Runtime type test — the semantics of `x is T`. Stricter than
        // IsAssignable, which is calibrated for declaration sites and
        // intentionally lets numeric primitives interchange, lets `string`
        // sponge anything (Ra coerces via ToString), and lets `null` flow
        // into any slot. None of those are correct for a runtime test: a
        // user writing `x is int` wants false for `"hello"`, false for a
        // float, false for null.
        //
        // Rules:
        //   target = any                       ⇒ true
        //   target = null                      ⇒ value.Type == Null
        //   target = primitive (int, …)        ⇒ matching RuntimeValueType
        //   target = string                    ⇒ value.Type == String
        //   target = bool                      ⇒ value.Type == Boolean
        //   target = class C                   ⇒ value is ClassInstance and
        //                                        its definition inherits from C
        //   target = struct/record/enum/trait/
        //            interface                 ⇒ existing nominal check from
        //                                        IsAssignable (no permissive
        //                                        widening to worry about)
        //   target = list/set/map/tuple        ⇒ container shape match,
        //                                        recursively element-wise
        //   target = fn(...)                   ⇒ structural fn-type check
        //   target = T (type parameter)        ⇒ conservative true (caller
        //                                        decides — at narrowing time
        //                                        we already know the param is
        //                                        unbound)
        //   target = U1 | U2 | …               ⇒ any member matches
        //
        // Numeric primitives match by exact runtime tag — `5 is float` is
        // false, `5.0 is int` is false. The interchangeable-numerics
        // behavior stays in IsAssignable for declarations.
        public static bool IsRuntimeTypeMatch(Context context, TypeDescriptor target, RuntimeValue value)
        {
            if (target == null) return true;
            if (string.Equals(target.Name, "any", StringComparison.Ordinal)) return true;
            if (target.IsTypeParameter) return true;

            if (target.IsUnionType && target.UnionMembers != null)
            {
                for (int i = 0; i < target.UnionMembers.Count; i++)
                    if (IsRuntimeTypeMatch(context, target.UnionMembers[i], value)) return true;
                return false;
            }

            if (target.IsFunctionType)
            {
                return IsAssignableToFunctionType(context, target, value);
            }

            // Null is its own type — only the null value matches `null`.
            if (string.Equals(target.Name, "null", StringComparison.Ordinal))
                return value.Type == RuntimeValueType.Null;

            // Null value: matches only the `null` type at runtime. The
            // declaration-time permissiveness ("null fits anywhere") would
            // make `x is int` true for null, which is never what users mean.
            if (value.Type == RuntimeValueType.Null) return false;

            // Strict primitive matching by runtime tag. Note that the wide
            // `Number` runtime tag (Ra's BigNumber container for literals
            // without an explicit numeric suffix) also passes every concrete
            // numeric target — without this rule the very common pattern
            //   let x = 42; x is int   (true expected, but x is Number)
            // would surprise users. The trade-off is that `5 is float` is
            // also true, which is consistent with how Ra freely interchanges
            // numeric primitives in IsAssignable.
            switch (target.PrimitiveKind)
            {
                case PrimitiveTypeKind.Int: return value.Type == RuntimeValueType.Integer || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Long: return value.Type == RuntimeValueType.Long || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Float: return value.Type == RuntimeValueType.Float || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Double: return value.Type == RuntimeValueType.Double || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.UInt: return value.Type == RuntimeValueType.UnsignedInteger || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.ULong: return value.Type == RuntimeValueType.UnsignedLong || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Short: return value.Type == RuntimeValueType.Short || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.UShort: return value.Type == RuntimeValueType.UnsignedShort || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Int128: return value.Type == RuntimeValueType.Int128 || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.UInt128: return value.Type == RuntimeValueType.UnsignedInt128 || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Decimal: return value.Type == RuntimeValueType.Decimal || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Byte: return value.Type == RuntimeValueType.Byte || value.Type == RuntimeValueType.Number;
                case PrimitiveTypeKind.Bool: return value.Type == RuntimeValueType.Boolean;
                case PrimitiveTypeKind.String: return value.Type == RuntimeValueType.String;
                case PrimitiveTypeKind.Number:
                    // `number` is Ra's wide-numeric tag — matches every
                    // numeric runtime value (Number/Integer/Long/…/Decimal).
                    return value.Type == RuntimeValueType.Number
                        || value.Type == RuntimeValueType.Integer
                        || value.Type == RuntimeValueType.Long
                        || value.Type == RuntimeValueType.Float
                        || value.Type == RuntimeValueType.Double
                        || value.Type == RuntimeValueType.UnsignedInteger
                        || value.Type == RuntimeValueType.UnsignedLong
                        || value.Type == RuntimeValueType.Short
                        || value.Type == RuntimeValueType.UnsignedShort
                        || value.Type == RuntimeValueType.Int128
                        || value.Type == RuntimeValueType.UnsignedInt128
                        || value.Type == RuntimeValueType.Decimal
                        || value.Type == RuntimeValueType.Byte;
            }

            // Container shape match: `[1,2,3] is list<int>` requires the
            // value to be a list and every element to match. We delegate to
            // IsAssignable for the structural recursion since the
            // permissive primitive rules don't matter once we're inside a
            // typed container — the container element type was provided by
            // the user and is enforced strictly there.
            switch (target.Name)
            {
                case "list":
                    if (value.Type != RuntimeValueType.List) return false;
                    return target.GenericArgs.Count == 0
                        || IsAssignable(context, target, value);
                case "set":
                    if (value.Type != RuntimeValueType.Set) return false;
                    return target.GenericArgs.Count == 0
                        || IsAssignable(context, target, value);
                case "map":
                    if (value.Type != RuntimeValueType.Map) return false;
                    return target.GenericArgs.Count == 0
                        || IsAssignable(context, target, value);
                case "tuple":
                    if (value.Type != RuntimeValueType.Tuple) return false;
                    return target.GenericArgs.Count == 0
                        || IsAssignable(context, target, value);
                case "function":
                    return value.Type == RuntimeValueType.Function;
                case "task":
                    return value.Type == RuntimeValueType.Task;
                case "channel":
                    return value.Type == RuntimeValueType.Channel;
                case "stream":
                case "Stream":
                    return value.Type == RuntimeValueType.Stream;
                case "async_stream":
                case "AsyncStream":
                    return value.Type == RuntimeValueType.AsyncStream;
            }

            // Nominal user types — reuse the existing assignability path,
            // which already handles class hierarchy walk, struct / record /
            // enum name matching, and interface / trait satisfaction. None
            // of these have permissive widening that would confuse the test.
            return IsAssignable(context, target, value);
        }

        // Narrow `declared` to the subset compatible with `tested`. Used by
        // the flow analyzer to refine a variable's type in a branch where
        // `x is Tested` is known to hold. Rules:
        //   - declared = any                       ⇒ tested
        //   - declared = U1 | ... | Un             ⇒ Union(Ui where Ui ⊑ tested)
        //   - declared ⊑ tested                    ⇒ declared (already narrow)
        //   - tested ⊑ declared                    ⇒ tested  (concrete refinement)
        //   - otherwise                            ⇒ tested  (assume the runtime
        //                                            test confirmed the witness)
        // Returns null when the intersection is provably empty — callers
        // surface that as an "impossible narrowing" diagnostic.
        public static TypeDescriptor? NarrowToType(Context context, TypeDescriptor declared, TypeDescriptor tested)
        {
            if (declared == null) return tested;
            if (tested == null) return declared;
            if (string.Equals(declared.Name, "any", StringComparison.Ordinal)) return tested;
            if (declared.Equals(tested)) return declared;

            if (declared.IsUnionType && declared.UnionMembers != null)
            {
                var hits = new List<TypeDescriptor>();
                foreach (var m in declared.UnionMembers)
                {
                    if (IsAssignableType(context, tested, m))
                    {
                        hits.Add(m);
                    }
                    else if (IsAssignableType(context, m, tested))
                    {
                        hits.Add(tested);
                    }
                }
                if (hits.Count == 0) return null;
                return TypeDescriptor.Union(hits);
            }

            if (IsAssignableType(context, tested, declared)) return declared;
            if (IsAssignableType(context, declared, tested)) return tested;
            return null;
        }

        // Narrow `declared` to the subset *incompatible* with `tested`. Used
        // for the `else` branch of an `is` test, or for sequential refinement
        // after an early `return` inside an `is` guard.
        //   - declared = U1 | ... | Un  ⇒ Union(Ui where !(Ui ⊑ tested))
        //   - declared ⊑ tested         ⇒ null  (no value survives the negation)
        //   - declared and tested disjoint ⇒ declared
        // Returns null when no alternative survives — callers surface that
        // as "this branch is unreachable" or fall back to the declared type
        // depending on context.
        public static TypeDescriptor? NarrowAwayFromType(Context context, TypeDescriptor declared, TypeDescriptor tested)
        {
            if (declared == null) return null;
            if (tested == null) return declared;
            if (string.Equals(declared.Name, "any", StringComparison.Ordinal)) return declared;

            if (declared.IsUnionType && declared.UnionMembers != null)
            {
                var survivors = new List<TypeDescriptor>();
                foreach (var m in declared.UnionMembers)
                {
                    if (!IsAssignableType(context, tested, m))
                    {
                        survivors.Add(m);
                    }
                }
                if (survivors.Count == 0) return null;
                return TypeDescriptor.Union(survivors);
            }

            if (IsAssignableType(context, tested, declared)) return null;
            return declared;
        }

        // Reports whether two descriptors share at least one common value
        // shape — i.e. the narrowing intersection is non-empty. Used to
        // detect "this `is` test can never succeed" cases at analysis time.
        public static bool TypesOverlap(Context context, TypeDescriptor a, TypeDescriptor b)
        {
            if (a == null || b == null) return true;
            if (string.Equals(a.Name, "any", StringComparison.Ordinal)) return true;
            if (string.Equals(b.Name, "any", StringComparison.Ordinal)) return true;
            if (a.IsTypeParameter || b.IsTypeParameter) return true;

            if (a.IsUnionType && a.UnionMembers != null)
            {
                foreach (var m in a.UnionMembers)
                    if (TypesOverlap(context, m, b)) return true;
                return false;
            }
            if (b.IsUnionType && b.UnionMembers != null)
            {
                foreach (var m in b.UnionMembers)
                    if (TypesOverlap(context, a, m)) return true;
                return false;
            }

            // Concrete-vs-concrete: overlap iff either one assigns into the other.
            return IsAssignableType(context, a, b) || IsAssignableType(context, b, a);
        }

        public static bool StrictTypeEquals(TypeDescriptor a, TypeDescriptor b)
        {
            if (a == null || b == null) return false;
            if (a.IsTypeParameter || b.IsTypeParameter)
            {
                if (a.IsTypeParameter && b.IsTypeParameter)
                    return string.Equals(a.TypeParameterName, b.TypeParameterName, StringComparison.Ordinal);
                return false;
            }

            // Union descriptors all share the synthetic name "union" but the
            // set-equality test on members is the actual identity test.
            // Delegate to Equals which already implements set equality on
            // the normalized member list.
            if (a.IsUnionType || b.IsUnionType)
            {
                return a.Equals(b);
            }

            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
            if (a.GenericArgs.Count != b.GenericArgs.Count) return false;
            for (int i = 0; i < a.GenericArgs.Count; i++)
            {
                if (!StrictTypeEquals(a.GenericArgs[i], b.GenericArgs[i])) return false;
            }

            return true;
        }

        public static bool IsStrictlyAssignable(Context context, TypeDescriptor target, RuntimeValue value)
        {
            if (target == null) return false;
            var actual = GetDescriptorFromRuntimeValue(value);
            return StrictTypeEquals(target, actual);
        }

        public static Dictionary<string, TypeDescriptor>? InferBindingsFromArgs(List<TypeDescriptor> formals, List<RuntimeValue> actuals)
        {
            var bindings = new Dictionary<string, TypeDescriptor>();
            if (actuals.Count < formals.Count) return null;
            for (int i = 0; i < formals.Count; i++)
            {
                var formal = formals[i];
                var actualDesc = GetDescriptorFromRuntimeValue(actuals[i]);
                var unify = UnifyGenericParameters(formal, actualDesc, bindings);
                if (unify == null) return null;
                bindings = unify;
            }
            return bindings;
        }

        public static TypeDescriptor GetDescriptorFromRuntimeValue(RuntimeValue val)
        {
            switch (val.Type)
            {
                case RuntimeValueType.Number: return new TypeDescriptor("number");
                case RuntimeValueType.String: return new TypeDescriptor("string");
                case RuntimeValueType.Boolean: return new TypeDescriptor("bool");
                case RuntimeValueType.Integer: return new TypeDescriptor("int");
                case RuntimeValueType.Long: return new TypeDescriptor("long");
                case RuntimeValueType.Float: return new TypeDescriptor("float");
                case RuntimeValueType.Double: return new TypeDescriptor("double");
                case RuntimeValueType.UnsignedInteger: return new TypeDescriptor("uint");
                case RuntimeValueType.UnsignedLong: return new TypeDescriptor("ulong");
                case RuntimeValueType.Short: return new TypeDescriptor("short");
                case RuntimeValueType.UnsignedShort: return new TypeDescriptor("ushort");
                case RuntimeValueType.Int128: return new TypeDescriptor("int128");
                case RuntimeValueType.UnsignedInt128: return new TypeDescriptor("uint128");
                case RuntimeValueType.Decimal: return new TypeDescriptor("decimal");
                case RuntimeValueType.Byte: return new TypeDescriptor("byte");
                case RuntimeValueType.List:
                    var l = (ListValue)val;
                    if (l.Elements.Count == 0) return new TypeDescriptor("list");
                    var innerDesc = GetDescriptorFromRuntimeValue(l.Elements[0]);
                    return new TypeDescriptor("list", new List<TypeDescriptor> { innerDesc });
                case RuntimeValueType.Set:
                    var s = (SetValue)val;
                    if (s.Elements.Count == 0) return new TypeDescriptor("set");
                    return new TypeDescriptor("set", new List<TypeDescriptor> { GetDescriptorFromRuntimeValue(s.Elements.ToList()[0]) });
                case RuntimeValueType.Map:
                    var m = (MapValue)val;
                    if (m.Pairs.Count == 0) return new TypeDescriptor("map");
                    var first = m.Pairs[0];
                    return new TypeDescriptor("map", new List<TypeDescriptor> { GetDescriptorFromRuntimeValue(first.Key), GetDescriptorFromRuntimeValue(first.Value) });
                case RuntimeValueType.Tuple:
                    var t = (TupleValue)val;
                    var args = t.Elements.Select(GetDescriptorFromRuntimeValue).ToList();
                    return new TypeDescriptor("tuple", args);
                case RuntimeValueType.Task:
                    return new TypeDescriptor("task");
                case RuntimeValueType.Channel:
                    return new TypeDescriptor("channel");
                case RuntimeValueType.Stream:
                    return new TypeDescriptor("stream");
                case RuntimeValueType.AsyncStream:
                    return new TypeDescriptor("async_stream");
                case RuntimeValueType.ClassInstance:
                    return new TypeDescriptor(((ClassInstanceValue)val).Definition.ClassName);
                case RuntimeValueType.ClassType:
                    return new TypeDescriptor(((ClassTypeValue)val).ClassName);
                case RuntimeValueType.StructInstance:
                case RuntimeValueType.RecordInstance:
                    return new TypeDescriptor(((StructInstanceValue)val).Definition.StructName);
                case RuntimeValueType.StructType:
                case RuntimeValueType.RecordType:
                    return new TypeDescriptor(((StructTypeValue)val).StructName);
                case RuntimeValueType.Enum:
                    return new TypeDescriptor(((EnumValue)val).EnumName);
                case RuntimeValueType.EnumType:
                    return new TypeDescriptor(((EnumTypeValue)val).EnumName);
                case RuntimeValueType.Reference:
                    if (val is IReferenceValue refValueAny)
                    {
                        try
                        {
                            var actualValue = refValueAny.Value;
                            if (actualValue != null)
                            {
                                var elementType = GetDescriptorFromRuntimeValue(actualValue);
                                return TypeDescriptor.RefType(elementType);
                            }
                        }
                        catch
                        {
                            return new TypeDescriptor("ref unknown");
                        }
                    }
                    return new TypeDescriptor("ref unknown");
                default:
                    return new TypeDescriptor(val.Type.ToString().ToLower());
            }
        }

        public static bool SatisfiesInterface(Context context, InterfaceTypeValue iface, RuntimeValue value)
        {
            if (value.Type == RuntimeValueType.ClassInstance)
            {
                var inst = (ClassInstanceValue)value;
                return ClassSatisfiesInterface(inst.Definition, iface);
            }

            return false;
        }

        private static bool ClassSatisfiesInterface(ClassTypeValue classType, InterfaceTypeValue iface)
        {
            foreach (var required in iface.Methods)
            {
                var candidates = classType.GetAllMethodsByName(required.NameTok.Value?.ToString() ?? "");
                if (!candidates.Any(m => InterfaceCompatibility.AreCompatible(m, required)))
                    return false;
            }

            return true;
        }

        public static bool SatisfiesTrait(Context context, TraitTypeValue trait, RuntimeValue value)
        {
            if (value.Type != RuntimeValueType.ClassInstance)
                return false;

            var instance = (ClassInstanceValue)value;

            foreach (var required in trait.GetRequiredMethods())
            {
                if (!instance.Definition.HasMethodSignatureInHierarchy(required))
                    return false;
            }

            return true;
        }

        public static string GetExtensionTargetName(RuntimeValue value)
        {
            return value.Type switch
            {
                RuntimeValueType.String => "string",
                RuntimeValueType.Boolean => "bool",
                RuntimeValueType.Number => "number",
                RuntimeValueType.Integer => "int",
                RuntimeValueType.Long => "long",
                RuntimeValueType.Float => "float",
                RuntimeValueType.Double => "double",
                RuntimeValueType.UnsignedInteger => "uint",
                RuntimeValueType.UnsignedLong => "ulong",
                RuntimeValueType.Short => "short",
                RuntimeValueType.UnsignedShort => "ushort",
                RuntimeValueType.Int128 => "int128",
                RuntimeValueType.UnsignedInt128 => "uint128",
                RuntimeValueType.Decimal => "decimal",
                RuntimeValueType.Byte => "byte",
                RuntimeValueType.List => "list",
                RuntimeValueType.Set => "set",
                RuntimeValueType.Map => "map",
                RuntimeValueType.Tuple => "tuple",
                RuntimeValueType.Null => "null",
                RuntimeValueType.StructInstance => ((StructInstanceValue)value).Definition.StructName,
                RuntimeValueType.RecordInstance => ((StructInstanceValue)value).Definition.StructName,
                RuntimeValueType.ClassInstance => ((ClassInstanceValue)value).Definition.ClassName,
                RuntimeValueType.Enum => ((EnumValue)value).EnumName,
                RuntimeValueType.EnumType => ((EnumTypeValue)value).EnumName,
                RuntimeValueType.TraitType => "trait",
                _ => value.Type.ToString()
            };
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Types
{
    public static class TypeSystem
    {
        // Verifica se runtime value è assegnabile al TypeDescriptor target
        public static bool IsAssignable(TypeDescriptor target, RuntimeValue value)
        {
            if (target == null) return true;

            // any built-in fallback: if target name is "any" -> allow
            if (string.Equals(target.Name, "any", StringComparison.OrdinalIgnoreCase)) return true;

            // handle type parameter: assume any value allowed (concrete binding must be done at call site)
            if (target.IsTypeParameter) return true;

            // map runtime value.Type to descriptor name:
            switch (value.Type)
            {
                case RuntimeValueType.Number:
                    return string.Equals(target.Name, "number", StringComparison.OrdinalIgnoreCase);
                case RuntimeValueType.String:
                    return string.Equals(target.Name, "string", StringComparison.OrdinalIgnoreCase);
                case RuntimeValueType.Boolean:
                    return string.Equals(target.Name, "boolean", StringComparison.OrdinalIgnoreCase) || string.Equals(target.Name, "bool", StringComparison.OrdinalIgnoreCase);
                case RuntimeValueType.Null:
                    // allow null assignable only to nullable types? For now treat as assignable to any reference type
                    return true;
                case RuntimeValueType.List:
                    if (!string.Equals(target.Name, "list", StringComparison.OrdinalIgnoreCase)) return false;
                    var l = (ListValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    var inner = target.GenericArgs[0];
                    foreach (var el in l.Elements)
                        if (!IsAssignable(inner.Substitute(new Dictionary<string, TypeDescriptor>()), el)) return false;
                    return true;
                case RuntimeValueType.Set:
                    if (!string.Equals(target.Name, "set", StringComparison.OrdinalIgnoreCase)) return false;
                    var s = (SetValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    var sin = target.GenericArgs[0];
                    foreach (var el in s.Elements)
                        if (!IsAssignable(sin, el)) return false;
                    return true;
                case RuntimeValueType.Map:
                    if (!string.Equals(target.Name, "map", StringComparison.OrdinalIgnoreCase)) return false;
                    var m = (MapValue)value;
                    if (target.GenericArgs.Count < 2) return true;
                    var kT = target.GenericArgs[0];
                    var vT = target.GenericArgs[1];
                    foreach (var kv in m.Pairs)
                    {
                        if (!IsAssignable(kT, kv.Key) || !IsAssignable(vT, kv.Value)) return false;
                    }
                    return true;
                case RuntimeValueType.Tuple:
                    if (!string.Equals(target.Name, "tuple", StringComparison.OrdinalIgnoreCase)) return false;
                    var t = (TupleValue)value;
                    if (target.GenericArgs.Count == 0) return true;
                    if (target.GenericArgs.Count != t.Elements.Count) return false;
                    for (int i = 0; i < t.Elements.Count; i++)
                        if (!IsAssignable(target.GenericArgs[i], t.Elements[i])) return false;
                    return true;
                case RuntimeValueType.Function:
                    return string.Equals(target.Name, "function", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        // Simple unification: tries to bind type parameters declared in 'formal' to concrete types from 'actual'
        // Returns map from type parameter name -> concrete TypeDescriptor, or null if cannot unify.
        // formal and actual are same-shape TypeDescriptor (formal may contain type params)
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

            if (!string.Equals(formal.Name, actual.Name, StringComparison.OrdinalIgnoreCase)) return null;
            if (formal.GenericArgs.Count != actual.GenericArgs.Count) return null;

            for (int i = 0; i < formal.GenericArgs.Count; i++)
            {
                var sub = UnifyGenericParameters(formal.GenericArgs[i], actual.GenericArgs[i], bindings);
                if (sub == null) return null;
            }

            return bindings;
        }

        // Helper that attempts to infer bindings from a list of formals and list of actual runtime values.
        public static Dictionary<string, TypeDescriptor>? InferBindingsFromArgs(List<TypeDescriptor> formals, List<RuntimeValue> actuals)
        {
            var bindings = new Dictionary<string, TypeDescriptor>();
            if (actuals.Count < formals.Count) return null; // not enough args to infer (higher-level checks handle defaults)
            for (int i = 0; i < formals.Count; i++)
            {
                var formal = formals[i];
                // derive actual type descriptor from runtime value
                var actualDesc = GetDescriptorFromRuntimeValue(actuals[i]);
                var unify = UnifyGenericParameters(formal, actualDesc, bindings);
                if (unify == null) return null;
                bindings = unify;
            }
            return bindings;
        }

        // Construct a TypeDescriptor that approximates the runtime value's type
        public static TypeDescriptor GetDescriptorFromRuntimeValue(RuntimeValue val)
        {
            switch (val.Type)
            {
                case RuntimeValueType.Number: return new TypeDescriptor("number");
                case RuntimeValueType.String: return new TypeDescriptor("string");
                case RuntimeValueType.Boolean: return new TypeDescriptor("boolean");
                case RuntimeValueType.List:
                    var l = (ListValue)val;
                    if (l.Elements.Count == 0) return new TypeDescriptor("list"); // unknown inner
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
                default:
                    return new TypeDescriptor(val.Type.ToString().ToLower());
            }
        }
    }
}
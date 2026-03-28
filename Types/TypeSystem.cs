using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;

namespace RaLanguage.Types
{
    public static class TypeSystem
    {
        public static bool IsAssignable(Context context, TypeDescriptor target, RuntimeValue value)
        {
            if (target == null) return true;
            if (string.Equals(target.Name, "any", StringComparison.Ordinal)) return true;
            if (target.IsTypeParameter) return true;

            var symbol = context?.SymbolTable?.Get(target.Name);

            if (symbol is EnumTypeValue enumType)
            {
                if (value.Type != RuntimeValueType.Enum) return false;
                var ev = (EnumValue)value;
                return string.Equals(ev.EnumName, enumType.EnumName, StringComparison.Ordinal);
            }

            if (symbol is StructTypeValue structType)
            {
                return value.Type == RuntimeValueType.StructInstance &&
                       string.Equals(((StructInstanceValue)value).Definition.StructName, structType.StructName, StringComparison.Ordinal);
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
                        || string.Equals(target.Name, "i32", StringComparison.Ordinal)
                        || string.Equals(target.Name, "integer", StringComparison.Ordinal)
                        || string.Equals(target.Name, "long", StringComparison.Ordinal)
                        || string.Equals(target.Name, "i64", StringComparison.Ordinal)
                        || string.Equals(target.Name, "float", StringComparison.Ordinal)
                        || string.Equals(target.Name, "f32", StringComparison.Ordinal)
                        || string.Equals(target.Name, "double", StringComparison.Ordinal)
                        || string.Equals(target.Name, "f64", StringComparison.Ordinal)
                        || string.Equals(target.Name, "unsignedinteger", StringComparison.Ordinal)
                        || string.Equals(target.Name, "uint", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ui32", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ulong", StringComparison.Ordinal)
                        || string.Equals(target.Name, "unsignedlong", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ui64", StringComparison.Ordinal)
                        || string.Equals(target.Name, "i16", StringComparison.Ordinal)
                        || string.Equals(target.Name, "int16", StringComparison.Ordinal)
                        || string.Equals(target.Name, "short", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ushort", StringComparison.Ordinal)
                        || string.Equals(target.Name, "uint16", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ui16", StringComparison.Ordinal)
                        || string.Equals(target.Name, "unsignedshort", StringComparison.Ordinal)
                        || string.Equals(target.Name, "int128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "i128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "integer128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "uint128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "ui128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "unsignedinteger128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "decimal", StringComparison.Ordinal)
                        || string.Equals(target.Name, "f128", StringComparison.Ordinal)
                        || string.Equals(target.Name, "byte", StringComparison.Ordinal);
                case RuntimeValueType.String:
                    return string.Equals(target.Name, "string", StringComparison.Ordinal);
                case RuntimeValueType.Boolean:
                    return string.Equals(target.Name, "boolean", StringComparison.Ordinal) || string.Equals(target.Name, "bool", StringComparison.Ordinal);
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
                default:
                    return false;
            }
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
                case RuntimeValueType.Boolean: return new TypeDescriptor("boolean");
                case RuntimeValueType.Integer: return new TypeDescriptor("integer");
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
                case RuntimeValueType.ClassInstance:
                    return new TypeDescriptor(((ClassInstanceValue)val).Definition.ClassName);
                case RuntimeValueType.ClassType:
                    return new TypeDescriptor(((ClassTypeValue)val).ClassName);
                case RuntimeValueType.StructInstance:
                    return new TypeDescriptor(((StructInstanceValue)val).Definition.StructName);
                case RuntimeValueType.StructType:
                    return new TypeDescriptor(((StructTypeValue)val).StructName);
                case RuntimeValueType.Enum:
                    return new TypeDescriptor(((EnumValue)val).EnumName);
                case RuntimeValueType.EnumType:
                    return new TypeDescriptor(((EnumTypeValue)val).EnumName);
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
    }
}
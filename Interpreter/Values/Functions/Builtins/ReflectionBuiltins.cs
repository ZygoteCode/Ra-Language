using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class ReflectionBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("type_of", TypeOf);
            BuiltInRegistry.Register("type_name", TypeName);
            BuiltInRegistry.Register("type_kind", TypeKindFn);
            BuiltInRegistry.Register("is_null", v => v.Type == RuntimeValueType.Null);
            BuiltInRegistry.Register("is_bool", v => v.Type == RuntimeValueType.Boolean);
            BuiltInRegistry.Register("is_string", v => v.Type == RuntimeValueType.String);
            BuiltInRegistry.Register("is_number", IsNumber);
            BuiltInRegistry.Register("is_int", IsInt);
            BuiltInRegistry.Register("is_float", IsFloatLike);
            BuiltInRegistry.Register("is_list", v => v.Type == RuntimeValueType.List);
            BuiltInRegistry.Register("is_map", v => v.Type == RuntimeValueType.Map);
            BuiltInRegistry.Register("is_set", v => v.Type == RuntimeValueType.Set);
            BuiltInRegistry.Register("is_tuple", v => v.Type == RuntimeValueType.Tuple);
            BuiltInRegistry.Register("is_function", v => v.Type == RuntimeValueType.Function || v.Type == RuntimeValueType.BaseFunction);
            BuiltInRegistry.Register("is_class", v => v.Type == RuntimeValueType.ClassType);
            BuiltInRegistry.Register("is_class_instance", v => v.Type == RuntimeValueType.ClassInstance);
            BuiltInRegistry.Register("is_struct", v => v.Type == RuntimeValueType.StructType);
            BuiltInRegistry.Register("is_struct_instance", v => v.Type == RuntimeValueType.StructInstance);
            BuiltInRegistry.Register("is_enum", v => v.Type == RuntimeValueType.EnumType);
            BuiltInRegistry.Register("is_enum_value", v => v.Type == RuntimeValueType.Enum);
            BuiltInRegistry.Register("is_interface", v => v.Type == RuntimeValueType.InterfaceType);
            BuiltInRegistry.Register("is_trait", v => v.Type == RuntimeValueType.TraitType);
            BuiltInRegistry.Register("is_namespace", v => v.Type == RuntimeValueType.Namespace);
            BuiltInRegistry.Register("is_task", v => v.Type == RuntimeValueType.Task);
            BuiltInRegistry.Register("is_channel", v => v.Type == RuntimeValueType.Channel);
            BuiltInRegistry.Register("is_stream", v => v.Type == RuntimeValueType.Stream);
            BuiltInRegistry.Register("is_annotation", v => v.Type == RuntimeValueType.AnnotationInstance);
            BuiltInRegistry.Register("is_annotation_type", v => v.Type == RuntimeValueType.AnnotationType);
            BuiltInRegistry.Register("is_reference", v => v.Type == RuntimeValueType.Reference);
            BuiltInRegistry.Register("is_native_handle", v => v.Type == RuntimeValueType.NativeHandle);
            BuiltInRegistry.Register("is_copy_type", v => v.IsCopy);
            BuiltInRegistry.Register("is_truthy", v => v.IsTrue());
            BuiltInRegistry.Register("is_callable", IsCallable);

            BuiltInRegistry.Register("class_of", ClassOf);
            BuiltInRegistry.Register("struct_of", StructOf);
            BuiltInRegistry.Register("enum_of", EnumOf);
            BuiltInRegistry.Register("base_class", BaseClass);
            BuiltInRegistry.Register("super_classes", SuperClasses);
            BuiltInRegistry.Register("traits_of", TraitsOf);
            BuiltInRegistry.Register("is_subclass_of", IsSubclassOf);
            BuiltInRegistry.Register("implements", Implements);
            BuiltInRegistry.Register("interfaces_of", InterfacesOf);
            BuiltInRegistry.Register("fields_of", FieldsOf);
            BuiltInRegistry.Register("static_fields_of", StaticFieldsOf);
            BuiltInRegistry.Register("methods_of", MethodsOf);
            BuiltInRegistry.Register("static_methods_of", StaticMethodsOf);
            BuiltInRegistry.Register("members_of", MembersOf);
            BuiltInRegistry.Register("field_type", FieldType);
            BuiltInRegistry.Register("has_method", HasMethod);
            BuiltInRegistry.Register("is_abstract", IsAbstract);
            BuiltInRegistry.Register("is_class_public", IsClassPublic);
            BuiltInRegistry.Register("enum_members", EnumMembers);
            BuiltInRegistry.Register("enum_value_of", EnumValueOf);
            BuiltInRegistry.Register("enum_name_of", EnumNameOf);
            BuiltInRegistry.Register("enum_by_value", EnumByValue);
            BuiltInRegistry.Register("generics_of", GenericsOf);
            BuiltInRegistry.Register("function_arity", FunctionArity);
            BuiltInRegistry.Register("function_name", FunctionName);
            BuiltInRegistry.Register("function_params", FunctionParams);
            BuiltInRegistry.Register("function_return_type", FunctionReturnType);
            BuiltInRegistry.Register("function_is_async", FunctionIsAsync);
            BuiltInRegistry.Register("function_is_builtin", FunctionIsBuiltin);
            BuiltInRegistry.Register("function_has_varargs", FunctionHasVarargs);
            BuiltInRegistry.Register("interface_methods", InterfaceMethods);
            BuiltInRegistry.Register("trait_methods", TraitMethods);
            BuiltInRegistry.Register("annotation_keys", AnnotationKeys);
            BuiltInRegistry.Register("annotation_name", AnnotationName);
            BuiltInRegistry.Register("annotation_target", AnnotationTarget);
            BuiltInRegistry.Register("annotation_args", AnnotationArgs);
            BuiltInRegistry.Register("annotation_positional", AnnotationPositional);
        }

        private static void BuiltInRegister(string name, Func<RuntimeValue, bool> pred)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (args.Count != 1) return Fail(ctx, p1, p2, $"{name} expects 1 argument");
                return Ok(MakeBool(pred(args[0])), ctx, p1, p2);
            });
        }

        private static RuntimeResult TypeOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("type_of", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(BuiltinUtils.TypeName(args[0])), ctx, p1, p2);
        }

        private static RuntimeResult TypeName(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("type_name", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(BuiltinUtils.TypeName(args[0])), ctx, p1, p2);
        }

        private static RuntimeResult TypeKindFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("type_kind", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(BuiltinUtils.TypeKind(args[0])), ctx, p1, p2);
        }

        private static bool IsNumber(RuntimeValue v) => BuiltinUtils.TypeKind(v) == "number";

        private static bool IsInt(RuntimeValue v)
        {
            switch (v.Type)
            {
                case RuntimeValueType.Integer:
                case RuntimeValueType.Long:
                case RuntimeValueType.Short:
                case RuntimeValueType.Byte:
                case RuntimeValueType.UnsignedInteger:
                case RuntimeValueType.UnsignedLong:
                case RuntimeValueType.UnsignedShort:
                case RuntimeValueType.Int128:
                case RuntimeValueType.UnsignedInt128:
                    return true;
                default: return false;
            }
        }

        private static bool IsFloatLike(RuntimeValue v) =>
            v.Type == RuntimeValueType.Float ||
            v.Type == RuntimeValueType.Double ||
            v.Type == RuntimeValueType.Decimal ||
            v.Type == RuntimeValueType.Number;

        private static bool IsCallable(RuntimeValue v) =>
            v is BaseFunctionValue ||
            v.Type == RuntimeValueType.Function ||
            v.Type == RuntimeValueType.BaseFunction ||
            v.Type == RuntimeValueType.ClassType ||
            v.Type == RuntimeValueType.StructType;

        private static RuntimeResult ClassOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("class_of", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is ClassInstanceValue ci) return Ok(ci.Definition, ctx, p1, p2);
            if (args[0] is ClassTypeValue ct) return Ok(ct, ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult StructOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("struct_of", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is StructInstanceValue si) return Ok(si.Definition, ctx, p1, p2);
            if (args[0] is StructTypeValue st) return Ok(st, ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult EnumOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("enum_of", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is EnumValue ev)
            {
                var entry = ctx?.SymbolTable?.GetEntry(ev.EnumName);
                if (entry?.Value is EnumTypeValue et) return Ok(et, ctx, p1, p2);
            }
            if (args[0] is EnumTypeValue ett) return Ok(ett, ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult BaseClass(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("base_class", args, 1, ctx, p1, p2, out var err)) return err;
            var ct = AsClassType(args[0]);
            if (ct?.BaseClass == null) return OkNull(ctx, p1, p2);
            return Ok(ct.BaseClass, ctx, p1, p2);
        }

        private static RuntimeResult SuperClasses(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("super_classes", args, 1, ctx, p1, p2, out var err)) return err;
            var ct = AsClassType(args[0]);
            var list = new List<RuntimeValue>();
            var cur = ct?.BaseClass;
            while (cur != null)
            {
                list.Add(cur);
                cur = cur.BaseClass;
            }
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult TraitsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("traits_of", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (AsClassType(args[0]) is ClassTypeValue ct)
            {
                foreach (var t in ct.Traits) list.Add(t);
            }
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        // is_subclass_of(child, parent) — true if `child` (a class type or an
        // instance) inherits from `parent` (a class type). Walks the BaseClass
        // chain by reference equality. A class is NOT considered a subclass of
        // itself; callers needing reflexive comparison should `||` an explicit
        // identity check. (Matches the conventional "strict subclass" semantic
        // used by e.g. Python's issubclass — though Python's variant is
        // reflexive; we expose the strict form as the primitive and let users
        // build the reflexive form on top.)
        private static RuntimeResult IsSubclassOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_subclass_of", args, 2, ctx, p1, p2, out var err)) return err;
            var child = AsClassType(args[0]);
            var parent = AsClassType(args[1]);
            if (child == null || parent == null) return Ok(MakeBool(false), ctx, p1, p2);

            var cur = child.BaseClass;
            while (cur != null)
            {
                if (ReferenceEquals(cur, parent)) return Ok(MakeBool(true), ctx, p1, p2);
                cur = cur.BaseClass;
            }
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        // implements(type, iface_or_trait) — true if `type` (class type or
        // instance) satisfies the interface or trait `iface_or_trait`. For
        // interfaces this delegates to the structural-compatibility check in
        // ClassTypeValue.ImplementsInterface. For traits it checks by
        // reference equality against the class's declared Traits list.
        private static RuntimeResult Implements(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("implements", args, 2, ctx, p1, p2, out var err)) return err;
            var ct = AsClassType(args[0]);
            if (ct == null) return Ok(MakeBool(false), ctx, p1, p2);

            if (args[1] is InterfaceTypeValue iface)
            {
                return Ok(MakeBool(ct.ImplementsInterface(iface)), ctx, p1, p2);
            }

            if (args[1] is TraitTypeValue trait)
            {
                var cur = ct;
                while (cur != null)
                {
                    foreach (var t in cur.Traits)
                    {
                        if (ReferenceEquals(t, trait)) return Ok(MakeBool(true), ctx, p1, p2);
                    }
                    cur = cur.BaseClass;
                }
                return Ok(MakeBool(false), ctx, p1, p2);
            }

            return Ok(MakeBool(false), ctx, p1, p2);
        }

        // interfaces_of(type) — lists the interface types this class
        // structurally satisfies. Built by scanning the global symbol table
        // for interface declarations and probing each with the existing
        // structural check. (Ra has no explicit `implements` clause on
        // classes; conformance is checked at use sites. This builtin makes
        // that probe accessible at runtime for reflection.)
        private static RuntimeResult InterfacesOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("interfaces_of", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            var ct = AsClassType(args[0]);
            if (ct == null) return Ok(new ListValue(list), ctx, p1, p2);

            // Walk every reachable symbol scope and collect interfaces. Use a
            // set to avoid duplicates when interfaces shadow each other.
            var seen = new HashSet<InterfaceTypeValue>();
            var st = ctx?.SymbolTable;
            while (st != null)
            {
                foreach (var kv in st.LocalDict)
                {
                    if (kv.Value.Value is InterfaceTypeValue iv && seen.Add(iv) && ct.ImplementsInterface(iv))
                    {
                        list.Add(iv);
                    }
                }
                st = st.Parent;
            }
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult FieldsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("fields_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new List<RuntimeValue>();
            switch (args[0])
            {
                case ClassInstanceValue ci:
                    foreach (var k in ci.Fields.Keys) names.Add(new StringValue(k));
                    break;
                case ClassTypeValue ct:
                    foreach (var f in ct.Fields)
                        names.Add(new StringValue(f.NameTok.Value?.ToString() ?? ""));
                    break;
                case StructInstanceValue si:
                    foreach (var k in si.Fields.Keys) names.Add(new StringValue(k));
                    break;
                case StructTypeValue st:
                    foreach (var f in st.Fields)
                        names.Add(new StringValue(f.NameTok.Value?.ToString() ?? ""));
                    break;
                case InterfaceTypeValue iv:
                    foreach (var f in iv.Fields)
                        names.Add(new StringValue(f.NameTok.Value?.ToString() ?? ""));
                    break;
                case TraitTypeValue tv:
                    foreach (var f in tv.Fields)
                        names.Add(new StringValue(f.NameTok.Value?.ToString() ?? ""));
                    break;
            }
            return Ok(new ListValue(names), ctx, p1, p2);
        }

        private static RuntimeResult StaticFieldsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("static_fields_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new List<RuntimeValue>();
            var ct = AsClassType(args[0]);
            if (ct != null)
            {
                foreach (var k in ct.StaticFields.Keys) names.Add(new StringValue(k));
            }
            return Ok(new ListValue(names), ctx, p1, p2);
        }

        private static RuntimeResult MethodsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("methods_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new List<RuntimeValue>();
            if (AsClassType(args[0]) is ClassTypeValue ct)
            {
                foreach (var m in ct.Methods)
                {
                    var n = m.VarNameTok?.Value?.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(new StringValue(n));
                }
            }
            else if (AsStructType(args[0]) is StructTypeValue st)
            {
                foreach (var m in st.Methods)
                {
                    var n = m.NameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(new StringValue(n));
                }
            }
            return Ok(new ListValue(names), ctx, p1, p2);
        }

        private static RuntimeResult StaticMethodsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("static_methods_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new List<RuntimeValue>();
            if (AsClassType(args[0]) is ClassTypeValue ct)
            {
                foreach (var m in ct.Methods)
                {
                    if (!m.IsStatic) continue;
                    var n = m.VarNameTok?.Value?.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(new StringValue(n));
                }
            }
            return Ok(new ListValue(names), ctx, p1, p2);
        }

        private static RuntimeResult MembersOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("members_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new HashSet<string>(StringComparer.Ordinal);
            void AddCt(ClassTypeValue ct)
            {
                foreach (var f in ct.Fields) names.Add(f.NameTok.Value?.ToString() ?? "");
                foreach (var f in ct.StaticFields.Keys) names.Add(f);
                foreach (var m in ct.Methods)
                {
                    var n = m.VarNameTok?.Value?.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                if (ct.BaseClass != null) AddCt(ct.BaseClass);
            }
            switch (args[0])
            {
                case ClassInstanceValue ci: AddCt(ci.Definition); break;
                case ClassTypeValue ct: AddCt(ct); break;
                case StructInstanceValue si:
                    foreach (var f in si.Definition.Fields) names.Add(f.NameTok.Value?.ToString() ?? "");
                    foreach (var m in si.Definition.Methods)
                    {
                        var n = m.NameTok.Value?.ToString();
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    break;
                case StructTypeValue st:
                    foreach (var f in st.Fields) names.Add(f.NameTok.Value?.ToString() ?? "");
                    foreach (var m in st.Methods)
                    {
                        var n = m.NameTok.Value?.ToString();
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    break;
            }
            return Ok(new ListValue(BuiltinUtils.Strings(names)), ctx, p1, p2);
        }

        private static RuntimeResult FieldType(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("field_type", args, 2, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[1]);
            switch (args[0])
            {
                case ClassInstanceValue ci:
                    if (ci.FieldTypes.TryGetValue(name, out var t1) && t1 != null) return Ok(new StringValue(t1.ToString() ?? ""), ctx, p1, p2);
                    break;
                case ClassTypeValue ct:
                    foreach (var f in ct.Fields)
                        if (f.NameTok.Value?.ToString() == name && f.FieldType != null)
                            return Ok(new StringValue(f.FieldType.ToString() ?? ""), ctx, p1, p2);
                    if (ct.StaticFieldTypes.TryGetValue(name, out var sft) && sft != null)
                        return Ok(new StringValue(sft.ToString() ?? ""), ctx, p1, p2);
                    break;
                case StructInstanceValue si:
                    foreach (var f in si.Definition.Fields)
                        if (f.NameTok.Value?.ToString() == name && f.FieldType != null)
                            return Ok(new StringValue(f.FieldType.ToString() ?? ""), ctx, p1, p2);
                    break;
                case StructTypeValue st:
                    foreach (var f in st.Fields)
                        if (f.NameTok.Value?.ToString() == name && f.FieldType != null)
                            return Ok(new StringValue(f.FieldType.ToString() ?? ""), ctx, p1, p2);
                    break;
            }
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult HasMethod(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("has_method", args, 2, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[1]);
            if (AsClassType(args[0]) is ClassTypeValue ct)
            {
                var found = ct.Methods.Any(m => m.VarNameTok?.Value?.ToString() == name);
                if (!found && ct.BaseClass != null)
                {
                    var cur = ct.BaseClass;
                    while (cur != null && !found)
                    {
                        found = cur.Methods.Any(m => m.VarNameTok?.Value?.ToString() == name);
                        cur = cur.BaseClass;
                    }
                }
                return Ok(MakeBool(found), ctx, p1, p2);
            }
            if (AsStructType(args[0]) is StructTypeValue st)
            {
                return Ok(MakeBool(st.Methods.Any(m => m.NameTok.Value?.ToString() == name)), ctx, p1, p2);
            }
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult IsAbstract(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_abstract", args, 1, ctx, p1, p2, out var err)) return err;
            var ct = AsClassType(args[0]);
            return Ok(MakeBool(ct?.IsAbstract ?? false), ctx, p1, p2);
        }

        private static RuntimeResult IsClassPublic(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_class_public", args, 1, ctx, p1, p2, out var err)) return err;
            var ct = AsClassType(args[0]);
            return Ok(MakeBool(ct?.IsPublic ?? false), ctx, p1, p2);
        }

        private static RuntimeResult EnumMembers(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("enum_members", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is EnumTypeValue et)
                foreach (var k in et.VariantsByName.Keys) list.Add(new StringValue(k));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult EnumValueOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("enum_value_of", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not EnumTypeValue et) return OkNull(ctx, p1, p2);
            var name = AsString(args[1]);
            if (!et.VariantsByName.TryGetValue(name, out var info)) return OkNull(ctx, p1, p2);
            return Ok(new Int128Value(info.UnderlyingValue), ctx, p1, p2);
        }

        private static RuntimeResult EnumNameOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("enum_name_of", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is EnumValue ev) return Ok(new StringValue(ev.MemberName), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult EnumByValue(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("enum_by_value", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not EnumTypeValue et) return OkNull(ctx, p1, p2);
            var target = (System.Int128)AsLong(args[1]);
            foreach (var info in et.Variants)
            {
                if (!info.HasPayload && info.UnderlyingValue == target)
                    return Ok(et.GetMember(info.Name), ctx, p1, p2);
            }
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult GenericsOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("generics_of", args, 1, ctx, p1, p2, out var err)) return err;
            var names = new List<RuntimeValue>();
            switch (args[0])
            {
                case ClassTypeValue ct: foreach (var g in ct.GenericTypeParams) names.Add(new StringValue(g)); break;
                case StructTypeValue st: foreach (var g in st.GenericTypeParams) names.Add(new StringValue(g)); break;
                case InterfaceTypeValue iv: foreach (var g in iv.GenericTypeParams) names.Add(new StringValue(g)); break;
                case TraitTypeValue tv: foreach (var g in tv.GenericTypeParams) names.Add(new StringValue(g)); break;
                case EnumTypeValue et: foreach (var g in et.GenericTypeParams) names.Add(new StringValue(g)); break;
                case FunctionValue fv: foreach (var g in fv.GenericTypeParams) names.Add(new StringValue(g)); break;
                case ClassInstanceValue ci: foreach (var g in ci.Definition.GenericTypeParams) names.Add(new StringValue(g)); break;
                case StructInstanceValue si: foreach (var g in si.Definition.GenericTypeParams) names.Add(new StringValue(g)); break;
            }
            return Ok(new ListValue(names), ctx, p1, p2);
        }

        private static RuntimeResult FunctionArity(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_arity", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is FunctionValue fv) return Ok(new IntegerValue(fv.ArgNames.Count), ctx, p1, p2);
            if (args[0] is BuiltInFunctionValue) return Ok(new IntegerValue(-1), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult FunctionName(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_name", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is BaseFunctionValue bf) return Ok(new StringValue(bf.Name), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult FunctionParams(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_params", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is FunctionValue fv)
                foreach (var n in fv.ArgNames) list.Add(new StringValue(n));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult FunctionReturnType(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_return_type", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is FunctionValue fv && fv.ReturnType != null)
                return Ok(new StringValue(fv.ReturnType.ToString() ?? ""), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult FunctionIsAsync(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_is_async", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is FunctionValue fv) return Ok(MakeBool(fv.IsAsync || fv.IsAsyncStream), ctx, p1, p2);
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult FunctionIsBuiltin(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_is_builtin", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(args[0] is BuiltInFunctionValue), ctx, p1, p2);
        }

        private static RuntimeResult FunctionHasVarargs(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("function_has_varargs", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is FunctionValue fv) return Ok(MakeBool(fv.HasVarArgs), ctx, p1, p2);
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult InterfaceMethods(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("interface_methods", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is InterfaceTypeValue iv)
                foreach (var m in iv.Methods)
                    list.Add(new StringValue(m.NameTok.Value?.ToString() ?? ""));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult TraitMethods(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("trait_methods", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is TraitTypeValue tv)
                foreach (var m in tv.Methods)
                    list.Add(new StringValue(m.NameTok?.Value?.ToString() ?? ""));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult AnnotationKeys(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("annotation_keys", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is AnnotationInstanceValue ai)
                foreach (var k in ai.NamedArgs.Keys) list.Add(new StringValue(k));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult AnnotationName(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("annotation_name", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is AnnotationInstanceValue ai) return Ok(new StringValue(ai.DefinitionName), ctx, p1, p2);
            if (args[0] is AnnotationTypeValue at) return Ok(new StringValue(at.AnnotationName), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult AnnotationTarget(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("annotation_target", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is AnnotationInstanceValue ai) return Ok(new StringValue(ai.Target.Key ?? ""), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult AnnotationArgs(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("annotation_args", args, 1, ctx, p1, p2, out var err)) return err;
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            if (args[0] is AnnotationInstanceValue ai)
            {
                foreach (var kv in ai.NamedArgs)
                    pairs.Add((new StringValue(kv.Key), kv.Value));
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult AnnotationPositional(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("annotation_positional", args, 1, ctx, p1, p2, out var err)) return err;
            var list = new List<RuntimeValue>();
            if (args[0] is AnnotationInstanceValue ai)
                foreach (var v in ai.PositionalArgs) list.Add(v);
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static ClassTypeValue? AsClassType(RuntimeValue v)
        {
            if (v is ClassTypeValue ct) return ct;
            if (v is ClassInstanceValue ci) return ci.Definition;
            return null;
        }

        private static StructTypeValue? AsStructType(RuntimeValue v)
        {
            if (v is StructTypeValue st) return st;
            if (v is StructInstanceValue si) return si.Definition;
            return null;
        }
    }

    internal static class ReflectionRegistryHelper
    {
        public static void RegisterBool(string name, Func<RuntimeValue, bool> pred)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (args.Count != 1) return BuiltinUtils.Fail(ctx, p1, p2, $"{name} expects 1 argument");
                return BuiltinUtils.Ok(BuiltinUtils.MakeBool(pred(args[0])), ctx, p1, p2);
            });
        }
    }
}

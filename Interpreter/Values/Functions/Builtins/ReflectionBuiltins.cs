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
using RaLanguage.Interpreter.Values.Reflection;
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
            // Tier-2 type identity / defaults / signatures.
            BuiltInRegistry.Register("type_id", TypeId);
            BuiltInRegistry.Register("type_key", TypeKey);
            BuiltInRegistry.Register("signature_of", SignatureOf);
            BuiltInRegistry.Register("default_of", DefaultOf);
            BuiltInRegistry.Register("zero_of", DefaultOf);
            BuiltInRegistry.Register("qual_name_of", QualNameOf);
            BuiltInRegistry.Register("full_name_of", FullNameOf);
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
            BuiltInRegistry.Register("is_async_stream", v => v.Type == RuntimeValueType.AsyncStream);
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

            // Tier-3 handle-based reflection — first-class MethodInfo/FieldInfo.
            BuiltInRegistry.Register("method_handle", MethodHandle);
            BuiltInRegistry.Register("field_handle", FieldHandle);
            BuiltInRegistry.Register("member_handle", MemberHandleFn);
            BuiltInRegistry.Register("members_of_handles", MembersOfHandles);
            BuiltInRegistry.Register("member_name", MemberNameOf);
            BuiltInRegistry.Register("member_kind", MemberKindOf);
            BuiltInRegistry.Register("member_owner", MemberOwnerOf);
            BuiltInRegistry.Register("member_is_static", MemberIsStatic);
            BuiltInRegistry.Register("member_is_public", MemberIsPublic);
            BuiltInRegistry.Register("member_invoke", MemberInvoke);
            BuiltInRegistry.Register("member_get", MemberGet);
            BuiltInRegistry.Register("member_set", MemberSet);
            BuiltInRegistry.Register("is_member_handle", v => v.Type == RuntimeValueType.MemberHandle);
        }

        private static void BuiltInRegister(string name, Func<RuntimeValue, bool> pred)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (args.Count != 1) return Fail(ctx, p1, p2, $"{name} expects 1 argument");
                return Ok(MakeBool(pred(args[0])), ctx, p1, p2);
            });
        }

        // ---------------- Tier-3 handle-based reflection ----------------
        // Handles bundle (owner type, name, kind, flags) into one value. Query
        // ops read the bundled metadata; use ops (invoke/get/set) delegate to the
        // existing string-keyed runtime builtins so there is a single code path.

        // A type value directly, or an instance → its definition. null otherwise.
        private static RuntimeValue? OwnerTypeValue(RuntimeValue v) => v switch
        {
            ClassTypeValue => v,
            StructTypeValue => v,
            ClassInstanceValue ci => ci.Definition,
            StructInstanceValue si => si.Definition,
            _ => null
        };

        // Resolve member `name` on type value `tv`. Methods first (instance, then
        // static); fields only when `fieldsToo`. Flags are read off the resolved
        // declaration so a handle carries accurate accessibility/static metadata.
        private static (bool found, MemberKind kind, bool isStatic, bool isPublic) ResolveMemberInfo(
            RuntimeValue tv, string name, bool fieldsToo)
        {
            if (tv is ClassTypeValue ct)
            {
                var inst = ct.ResolveInstanceMethods(name);
                if (inst.Count > 0) return (true, MemberKind.Method, false, inst[0].IsPublic);
                var stat = ct.ResolveStaticMethods(name);
                if (stat.Count > 0) return (true, MemberKind.Method, true, stat[0].IsPublic);
                if (fieldsToo)
                {
                    if (ct.HasField(name)) return (true, MemberKind.Field, false, ct.IsFieldPublic(name));
                    if (ct.HasStaticField(name)) return (true, MemberKind.Field, true, ct.IsStaticFieldPublic(name));
                }
            }
            else if (tv is StructTypeValue st)
            {
                if (st.GetMethod(name) != null) return (true, MemberKind.Method, false, true);
                if (fieldsToo && st.HasField(name)) return (true, MemberKind.Field, false, st.IsFieldPublic(name));
            }
            return (false, MemberKind.Unknown, false, false);
        }

        private static RuntimeResult MethodHandle(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("method_handle", args, 2, ctx, p1, p2, out var err)) return err;
            var tv = OwnerTypeValue(args[0]);
            if (tv == null) return Fail(ctx, p1, p2, "method_handle: first argument must be a type or instance");
            var name = AsString(args[1]);
            var info = ResolveMemberInfo(tv, name, false);
            if (!info.found) return Ok(NullValue.Null, ctx, p1, p2);
            return Ok(new MemberHandleValue(tv, BuiltinUtils.TypeName(tv), name, MemberKind.Method, info.isStatic, info.isPublic), ctx, p1, p2);
        }

        private static RuntimeResult FieldHandle(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("field_handle", args, 2, ctx, p1, p2, out var err)) return err;
            var tv = OwnerTypeValue(args[0]);
            if (tv == null) return Fail(ctx, p1, p2, "field_handle: first argument must be a type or instance");
            var name = AsString(args[1]);
            var info = ResolveMemberInfo(tv, name, true);
            if (!info.found || info.kind != MemberKind.Field) return Ok(NullValue.Null, ctx, p1, p2);
            return Ok(new MemberHandleValue(tv, BuiltinUtils.TypeName(tv), name, MemberKind.Field, info.isStatic, info.isPublic), ctx, p1, p2);
        }

        private static RuntimeResult MemberHandleFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_handle", args, 2, ctx, p1, p2, out var err)) return err;
            var tv = OwnerTypeValue(args[0]);
            if (tv == null) return Fail(ctx, p1, p2, "member_handle: first argument must be a type or instance");
            var name = AsString(args[1]);
            var info = ResolveMemberInfo(tv, name, true);
            if (!info.found) return Ok(NullValue.Null, ctx, p1, p2);
            return Ok(new MemberHandleValue(tv, BuiltinUtils.TypeName(tv), name, info.kind, info.isStatic, info.isPublic), ctx, p1, p2);
        }

        private static RuntimeResult MembersOfHandles(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("members_of_handles", args, 1, ctx, p1, p2, out var err)) return err;
            var tv = OwnerTypeValue(args[0]);
            if (tv == null) return Fail(ctx, p1, p2, "members_of_handles: argument must be a type or instance");
            var ownerName = BuiltinUtils.TypeName(tv);
            var handles = new List<RuntimeValue>();
            AppendHandles(ctx, p1, p2, "methods_of", tv, ownerName, handles);
            AppendHandles(ctx, p1, p2, "fields_of", tv, ownerName, handles);
            return Ok(new ListValue(handles), ctx, p1, p2);
        }

        private static void AppendHandles(Context ctx, Position p1, Position p2, string lister,
            RuntimeValue tv, string ownerName, List<RuntimeValue> outList)
        {
            var r = BuiltInRegistry.Invoke(lister, ctx, new List<RuntimeValue> { tv }, p1, p2);
            if (r.Error != null || r.Value is not ListValue names) return;
            foreach (var n in names.Elements)
            {
                var name = AsString(n);
                var info = ResolveMemberInfo(tv, name, true);
                if (!info.found) continue;
                outList.Add(new MemberHandleValue(tv, ownerName, name, info.kind, info.isStatic, info.isPublic));
            }
        }

        private static RuntimeResult MemberNameOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_name", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_name: argument must be a member handle");
            return Ok(new StringValue(h.MemberName), ctx, p1, p2);
        }

        private static RuntimeResult MemberKindOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_kind", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_kind: argument must be a member handle");
            return Ok(new StringValue(h.Kind.ToString().ToLowerInvariant()), ctx, p1, p2);
        }

        private static RuntimeResult MemberOwnerOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_owner", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_owner: argument must be a member handle");
            return Ok(h.Owner, ctx, p1, p2);
        }

        private static RuntimeResult MemberIsStatic(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_is_static", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_is_static: argument must be a member handle");
            return Ok(MakeBool(h.IsStatic), ctx, p1, p2);
        }

        private static RuntimeResult MemberIsPublic(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_is_public", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_is_public: argument must be a member handle");
            return Ok(MakeBool(h.IsPublic), ctx, p1, p2);
        }

        private static RuntimeResult MemberInvoke(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("member_invoke", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_invoke: first argument must be a member handle");
            if (h.Kind != MemberKind.Method) return Fail(ctx, p1, p2, $"member_invoke: '{h.MemberName}' is not a method handle");
            var fwd = new List<RuntimeValue> { args[1], new StringValue(h.MemberName) };
            for (int i = 2; i < args.Count; i++) fwd.Add(args[i]);
            return BuiltInRegistry.Invoke("invoke_method", ctx, fwd, p1, p2);
        }

        private static RuntimeResult MemberGet(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_get", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_get: first argument must be a member handle");
            if (h.Kind != MemberKind.Field) return Fail(ctx, p1, p2, $"member_get: '{h.MemberName}' is not a field handle");
            return BuiltInRegistry.Invoke("get_field", ctx, new List<RuntimeValue> { args[1], new StringValue(h.MemberName) }, p1, p2);
        }

        private static RuntimeResult MemberSet(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("member_set", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MemberHandleValue h) return Fail(ctx, p1, p2, "member_set: first argument must be a member handle");
            if (h.Kind != MemberKind.Field) return Fail(ctx, p1, p2, $"member_set: '{h.MemberName}' is not a field handle");
            return BuiltInRegistry.Invoke("set_field", ctx, new List<RuntimeValue> { args[1], new StringValue(h.MemberName), args[2] }, p1, p2);
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

        // Process-wide canonical-name → stable int intern table backing
        // `type_id`. GetOrAdd may run the factory more than once under a race
        // (wasting an id), but the returned id per name stays unique + stable —
        // exactly the identity contract. AOT-safe (plain dictionary, no codegen).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_typeIds =
            new(StringComparer.Ordinal);
        private static int s_nextTypeId;

        // type_id(x) — a stable, compact integer identity for x's type (or for x
        // itself when x is a type value). Same type ⇒ same id ⇒ O(1) int equality
        // and dense map keys. A type and its instances share an id (both render
        // to the same canonical name).
        private static RuntimeResult TypeId(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("type_id", args, 1, ctx, p1, p2, out var err)) return err;
            var key = BuiltinUtils.TypeName(args[0]);
            int id = s_typeIds.GetOrAdd(key, _ => System.Threading.Interlocked.Increment(ref s_nextTypeId));
            return Ok(new IntegerValue(id), ctx, p1, p2);
        }

        // type_key(x) — the canonical type-name string (interned for fast
        // equality), stable across runs and usable directly as a map key.
        private static RuntimeResult TypeKey(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("type_key", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(string.Intern(BuiltinUtils.TypeName(args[0]))), ctx, p1, p2);
        }

        // signature_of(fn) — the structural signature string `fn(P…) -> R`,
        // composed from the callable's declared parameter / return types
        // (untyped slots render as `any`). Mirrors the callable-diagnostics
        // renderer so signatures read identically everywhere.
        private static RuntimeResult SignatureOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("signature_of", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is FunctionValue fv)
            {
                var ps = new List<string>(fv.ArgTypes.Count);
                for (int i = 0; i < fv.ArgTypes.Count; i++) ps.Add(fv.ArgTypes[i]?.ToString() ?? "any");
                if (fv.HasVarArgs) ps.Add("..." + (fv.VarArgType?.ToString() ?? "any"));
                var ret = fv.ReturnType?.ToString() ?? "any";
                return Ok(new StringValue($"fn({string.Join(", ", ps)}) -> {ret}"), ctx, p1, p2);
            }
            if (args[0] is BaseFunctionValue) return Ok(new StringValue("fn(...)"), ctx, p1, p2);
            return Fail(ctx, p1, p2, "signature_of: argument must be a function");
        }

        // default_of(T) / zero_of(T) — the default value for a type, given a
        // type-name string (matching the native_sizeof("i32") convention) or a
        // type value. Numeric → 0, bool → false, string → "" ; every reference
        // / composite / unknown type → null (construct structs explicitly).
        private static RuntimeResult DefaultOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("default_of", args, 1, ctx, p1, p2, out var err)) return err;
            string tn = (args[0] is StringValue sv ? sv.Value : BuiltinUtils.TypeName(args[0])).ToLowerInvariant();
            switch (tn)
            {
                case "int": case "long": case "short": case "byte":
                case "uint": case "ulong": case "ushort":
                case "int128": case "uint128": case "number":
                    return Ok(new IntegerValue(0), ctx, p1, p2);
                case "float":
                    return Ok(new FloatValue(0f), ctx, p1, p2);
                case "double":
                    return Ok(new DoubleValue(0.0), ctx, p1, p2);
                case "bool": case "boolean":
                    return Ok(MakeBool(false), ctx, p1, p2);
                case "string":
                    return Ok(new StringValue(""), ctx, p1, p2);
                default:
                    return OkNull(ctx, p1, p2);
            }
        }

        // Resolve an instance to its declaring TYPE value (so a class/struct
        // instance reports the same namespace + declaration site as its type);
        // anything else passes through unchanged.
        private static RuntimeValue TypeValueOf(RuntimeValue v) => v switch
        {
            ClassInstanceValue ci => ci.Definition,
            StructInstanceValue si => si.Definition,
            _ => v
        };

        private static NamespaceValue? NamespaceOf(RuntimeValue typeVal) => typeVal switch
        {
            ClassTypeValue ct => ct.DeclaringNamespace,
            StructTypeValue st => st.DeclaringNamespace,
            EnumTypeValue et => et.DeclaringNamespace,
            _ => null
        };

        // qual_name_of(x) — the namespace-qualified type name: "A.B.Foo" for a
        // type declared in `namespace A.B`, else the bare canonical name "Foo".
        // Accepts a type value or an instance (resolves to its type).
        private static RuntimeResult QualNameOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("qual_name_of", args, 1, ctx, p1, p2, out var err)) return err;
            var tv = TypeValueOf(args[0]);
            var name = BuiltinUtils.TypeName(args[0]);
            var ns = NamespaceOf(tv);
            var qual = ns != null && !ns.IsRoot ? ns.QualifiedName + "." + name : name;
            return Ok(new StringValue(qual), ctx, p1, p2);
        }

        // full_name_of(x) — the qualified name prefixed with the declaring
        // module (source file, sans extension): "mymod::A.B.Foo". Falls back to
        // the qualified name when the declaring file is unknown.
        private static RuntimeResult FullNameOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("full_name_of", args, 1, ctx, p1, p2, out var err)) return err;
            var tv = TypeValueOf(args[0]);
            var name = BuiltinUtils.TypeName(args[0]);
            var ns = NamespaceOf(tv);
            var qual = ns != null && !ns.IsRoot ? ns.QualifiedName + "." + name : name;
            var file = tv.PositionStart.Fn;
            if (string.IsNullOrEmpty(file)) return Ok(new StringValue(qual), ctx, p1, p2);
            var module = System.IO.Path.GetFileNameWithoutExtension(file);
            return Ok(new StringValue(string.IsNullOrEmpty(module) ? qual : module + "::" + qual), ctx, p1, p2);
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
                case RuntimeValueType.Number:
                    // The default numeric kind for literals such as `5` is
                    // Number. Treat it as integral when there is no decimal
                    // scale so that `is_int(5)` returns true.
                    var n = (NumberValue)v;
                    return n.Value.Scale.IsZero;
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

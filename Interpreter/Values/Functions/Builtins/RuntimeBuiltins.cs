using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class RuntimeBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("clone", CloneShallow);
            BuiltInRegistry.Register("deep_clone", DeepClone);
            BuiltInRegistry.Register("equals", EqualsOp);
            BuiltInRegistry.Register("strict_equals", StrictEqualsOp);
            BuiltInRegistry.Register("compare", CompareOp);
            BuiltInRegistry.Register("identity_hash", IdentityHash);
            BuiltInRegistry.Register("hash", Hash);

            BuiltInRegistry.Register("lookup", Lookup);
            BuiltInRegistry.Register("lookup_global", LookupGlobal);
            BuiltInRegistry.Register("define", Define);
            BuiltInRegistry.Register("scope_keys", ScopeKeys);
            BuiltInRegistry.Register("global_keys", GlobalKeys);
            BuiltInRegistry.Register("current_function", CurrentFunction);
            BuiltInRegistry.Register("call_stack", CallStack);
            BuiltInRegistry.Register("current_file", CurrentFile);

            BuiltInRegistry.Register("invoke", Invoke);
            BuiltInRegistry.Register("invoke_method", InvokeMethod);
            BuiltInRegistry.Register("invoke_static", InvokeStatic);
            BuiltInRegistry.Register("new_instance", NewInstance);

            BuiltInRegistry.Register("get_field", GetField);
            BuiltInRegistry.Register("set_field", SetField);
            BuiltInRegistry.Register("get_static_field", GetStaticField);
            BuiltInRegistry.Register("set_static_field", SetStaticField);

            BuiltInRegistry.Register("throw_error", ThrowError);
            BuiltInRegistry.Register("error_message", ErrorMessage);
        }

        private static RuntimeResult CloneShallow(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("clone", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(args[0].Copy(), ctx, p1, p2);
        }

        private static RuntimeResult DeepClone(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("deep_clone", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(DeepCopy(args[0]), ctx, p1, p2);
        }

        private static RuntimeValue DeepCopy(RuntimeValue v)
        {
            switch (v)
            {
                case ListValue lv:
                    var l = new List<RuntimeValue>(lv.Elements.Count);
                    foreach (var e in lv.Elements) l.Add(DeepCopy(e));
                    return new ListValue(l).SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                case MapValue mv:
                    var pairs = new List<(RuntimeValue, RuntimeValue)>(mv.Pairs.Count);
                    foreach (var (k, val) in mv.Pairs) pairs.Add((DeepCopy(k), DeepCopy(val)));
                    return new MapValue(pairs).SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                case SetValue sv:
                    var hs = new HashSet<RuntimeValue>();
                    foreach (var e in sv.Elements) hs.Add(DeepCopy(e));
                    return new SetValue(hs).SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                case TupleValue tv:
                    var tl = new List<RuntimeValue>(tv.Elements.Count);
                    foreach (var e in tv.Elements) tl.Add(DeepCopy(e));
                    return new TupleValue(tl).SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                case ClassInstanceValue ci:
                    var copy = new ClassInstanceValue(ci.Definition);
                    foreach (var kv in ci.Fields)
                    {
                        bool pub = ci.IsFieldPublic(kv.Key);
                        copy.SetField(kv.Key, DeepCopy(kv.Value), pub, ci.GetFieldType(kv.Key), ci.GetFieldDeclarationType(kv.Key));
                    }
                    return copy.SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                case StructInstanceValue si:
                    var sc = new StructInstanceValue(si.Definition);
                    foreach (var kv in si.Fields)
                        sc.SetField(kv.Key, DeepCopy(kv.Value), si.IsFieldPublic(kv.Key), si.GetFieldDeclarationType(kv.Key));
                    return sc.SetContext(v.Context).SetPos(v.PositionStart, v.PositionEnd);
                default:
                    return v.Copy();
            }
        }

        private static RuntimeResult EqualsOp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("equals", args, 2, ctx, p1, p2, out var err)) return err;
            var (r, e) = args[0].GetComparisonEq(args[1]);
            if (e != null) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(r != null && r.IsTrue()), ctx, p1, p2);
        }

        private static RuntimeResult StrictEqualsOp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("strict_equals", args, 2, ctx, p1, p2, out var err)) return err;
            var (r, e) = args[0].GetComparisonStrictEq(args[1]);
            if (e != null) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(r != null && r.IsTrue()), ctx, p1, p2);
        }

        private static RuntimeResult CompareOp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("compare", args, 2, ctx, p1, p2, out var err)) return err;
            var a = args[0]; var b = args[1];
            var (eq, _) = a.GetComparisonEq(b);
            if (eq != null && eq.IsTrue()) return Ok(new IntegerValue(0), ctx, p1, p2);
            var (lt, _) = a.GetComparisonLt(b);
            if (lt != null && lt.IsTrue()) return Ok(new IntegerValue(-1), ctx, p1, p2);
            var (gt, _) = a.GetComparisonGt(b);
            if (gt != null && gt.IsTrue()) return Ok(new IntegerValue(1), ctx, p1, p2);
            return Ok(new IntegerValue(string.CompareOrdinal(a.ToString(), b.ToString())), ctx, p1, p2);
        }

        private static RuntimeResult IdentityHash(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("identity_hash", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new IntegerValue(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(args[0])), ctx, p1, p2);
        }

        private static RuntimeResult Hash(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("hash", args, 1, ctx, p1, p2, out var err)) return err;
            int h;
            try { h = args[0]?.ToString()?.GetHashCode() ?? 0; }
            catch { h = 0; }
            return Ok(new IntegerValue(h), ctx, p1, p2);
        }

        private static RuntimeResult Lookup(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("lookup", args, 1, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[0]);
            var v = ctx?.SymbolTable?.Get(name);
            return v == null ? OkNull(ctx, p1, p2) : Ok(v, ctx, p1, p2);
        }

        private static RuntimeResult LookupGlobal(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("lookup_global", args, 1, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[0]);
            var v = RaLanguage.Program.GlobalSymbolTable.Get(name);
            return v == null ? OkNull(ctx, p1, p2) : Ok(v, ctx, p1, p2);
        }

        private static RuntimeResult Define(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("define", args, 2, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[0]);
            ctx?.SymbolTable?.Set(name, args[1]);
            return Ok(args[1], ctx, p1, p2);
        }

        private static RuntimeResult ScopeKeys(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 0) return Fail(ctx, p1, p2, "scope_keys takes no arguments");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var st = ctx?.SymbolTable;
            while (st != null)
            {
                foreach (var k in st.GetLocalKeys()) seen.Add(k);
                st = st.Parent;
            }
            return Ok(new ListValue(Strings(seen)), ctx, p1, p2);
        }

        private static RuntimeResult GlobalKeys(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 0) return Fail(ctx, p1, p2, "global_keys takes no arguments");
            var keys = new List<string>();
            foreach (var k in RaLanguage.Program.GlobalSymbolTable.GetLocalKeys()) keys.Add(k);
            return Ok(new ListValue(Strings(keys)), ctx, p1, p2);
        }

        private static RuntimeResult CurrentFunction(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 0) return Fail(ctx, p1, p2, "current_function takes no arguments");
            return Ok(new StringValue(ctx?.DisplayName ?? "<global>"), ctx, p1, p2);
        }

        private static RuntimeResult CallStack(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 0) return Fail(ctx, p1, p2, "call_stack takes no arguments");
            var frames = new List<RuntimeValue>();
            var cur = ctx;
            while (cur != null)
            {
                frames.Add(new StringValue(cur.DisplayName ?? "<global>"));
                cur = cur.Parent;
            }
            return Ok(new ListValue(frames), ctx, p1, p2);
        }

        private static RuntimeResult CurrentFile(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count != 0) return Fail(ctx, p1, p2, "current_file takes no arguments");
            var fn = p1.Fn ?? "<unknown>";
            return Ok(new StringValue(fn), ctx, p1, p2);
        }

        private static RuntimeResult Invoke(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("invoke", args, 1, ctx, p1, p2, out var err)) return err;
            var target = args[0];
            var callArgs = new List<RuntimeValue>();
            if (args.Count == 2 && args[1] is ListValue lv) callArgs.AddRange(lv.Elements);
            else for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);

            if (target is BaseFunctionValue bf)
            {
                var result = bf.Execute(callArgs);
                if (result.Error != null) return new RuntimeResult().Failure(result.Error);
                return Ok(result.Value ?? new NullValue(), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, $"invoke: '{TypeKind(target)}' is not callable");
        }

        private static RuntimeResult InvokeMethod(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("invoke_method", args, 2, ctx, p1, p2, out var err)) return err;
            var receiver = args[0];
            var name = AsString(args[1]);
            var callArgs = new List<RuntimeValue>();
            if (args.Count == 3 && args[2] is ListValue lv) callArgs.AddRange(lv.Elements);
            else for (int i = 2; i < args.Count; i++) callArgs.Add(args[i]);

            if (receiver is ClassInstanceValue ci)
            {
                var methods = ci.Definition.ResolveInstanceMethods(name);
                if (methods.Count == 0) return Fail(ctx, p1, p2, $"invoke_method: no method '{name}' on class '{ci.Definition.ClassName}'");
                var bound = new BoundClassMethodValue(ci.Definition, ci, methods[0], false)
                    .SetContext(ctx).SetPos(p1, p2);
                var r = ((BaseFunctionValue)bound).Execute(callArgs);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                return Ok(r.Value ?? new NullValue(), ctx, p1, p2);
            }
            if (receiver is StructInstanceValue si)
            {
                var m = si.Definition.GetMethod(name);
                if (m == null) return Fail(ctx, p1, p2, $"invoke_method: no method '{name}' on struct '{si.Definition.StructName}'");
                var bound = new BoundStructMethodValue(si.Definition, si, m).SetContext(ctx).SetPos(p1, p2);
                var r = ((BaseFunctionValue)bound).Execute(callArgs);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                return Ok(r.Value ?? new NullValue(), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, $"invoke_method: receiver of type '{TypeKind(receiver)}' has no methods");
        }

        private static RuntimeResult InvokeStatic(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("invoke_static", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ClassTypeValue ct) return Fail(ctx, p1, p2, "invoke_static: first argument must be a class");
            var name = AsString(args[1]);
            var callArgs = new List<RuntimeValue>();
            if (args.Count == 3 && args[2] is ListValue lv) callArgs.AddRange(lv.Elements);
            else for (int i = 2; i < args.Count; i++) callArgs.Add(args[i]);
            if (!ct.TryGetStaticMethodOwner(name, out var owner, out var method) || method == null)
                return Fail(ctx, p1, p2, $"invoke_static: no static method '{name}' on '{ct.ClassName}'");
            var bound = new BoundClassMethodValue(owner, null, method, true).SetContext(ctx).SetPos(p1, p2);
            var r = ((BaseFunctionValue)bound).Execute(callArgs);
            if (r.Error != null) return new RuntimeResult().Failure(r.Error);
            return Ok(r.Value ?? new NullValue(), ctx, p1, p2);
        }

        private static RuntimeResult NewInstance(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("new_instance", args, 1, ctx, p1, p2, out var err)) return err;
            var target = args[0];
            var callArgs = new List<RuntimeValue>();
            if (args.Count == 2 && args[1] is ListValue lv) callArgs.AddRange(lv.Elements);
            else for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);

            if (target is ClassTypeValue ct)
            {
                var r = ((BaseFunctionValue)ct).Execute(callArgs);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                return Ok(r.Value ?? new NullValue(), ctx, p1, p2);
            }
            if (target is StructTypeValue st)
            {
                var r = st.Execute(callArgs);
                if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                return Ok(r.Value ?? new NullValue(), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, $"new_instance: '{TypeKind(target)}' is not a constructible type");
        }

        private static RuntimeResult GetField(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("get_field", args, 2, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[1]);
            if (args[0] is ClassInstanceValue ci)
            {
                if (!ci.HasField(name)) return Fail(ctx, p1, p2, $"get_field: '{name}' not on class instance");
                return Ok(ci.GetField(name), ctx, p1, p2);
            }
            if (args[0] is StructInstanceValue si)
            {
                if (!si.HasField(name)) return Fail(ctx, p1, p2, $"get_field: '{name}' not on struct instance");
                return Ok(si.GetField(name), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, $"get_field: receiver must be an instance");
        }

        private static RuntimeResult SetField(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_field", args, 3, ctx, p1, p2, out var err)) return err;
            var name = AsString(args[1]);
            var value = args[2];
            if (args[0] is ClassInstanceValue ci)
            {
                if (!ci.HasField(name)) return Fail(ctx, p1, p2, $"set_field: '{name}' not on class instance");
                ci.SetMember(name, value);
                return Ok(value, ctx, p1, p2);
            }
            if (args[0] is StructInstanceValue si)
            {
                if (!si.HasField(name)) return Fail(ctx, p1, p2, $"set_field: '{name}' not on struct instance");
                si.SetMember(name, value);
                return Ok(value, ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, "set_field: receiver must be an instance");
        }

        private static RuntimeResult GetStaticField(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("get_static_field", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ClassTypeValue ct) return Fail(ctx, p1, p2, "get_static_field: first arg must be a class");
            var name = AsString(args[1]);
            if (!ct.TryGetStaticFieldOwner(name, out var owner)) return OkNull(ctx, p1, p2);
            return Ok(owner.StaticFields[name], ctx, p1, p2);
        }

        private static RuntimeResult SetStaticField(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_static_field", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ClassTypeValue ct) return Fail(ctx, p1, p2, "set_static_field: first arg must be a class");
            var name = AsString(args[1]);
            if (!ct.TryGetStaticFieldOwner(name, out var owner)) return Fail(ctx, p1, p2, $"set_static_field: '{name}' not defined on '{ct.ClassName}'");
            bool pub = owner.IsStaticFieldPublic(name);
            var ftype = owner.GetStaticFieldType(name);
            owner.SetStaticField(name, args[2], pub, ftype);
            return Ok(args[2], ctx, p1, p2);
        }

        private static RuntimeResult ThrowError(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var msg = args.Count >= 1 ? AsString(args[0]) : "error";
            return Fail(ctx, p1, p2, msg);
        }

        private static RuntimeResult ErrorMessage(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("error_message", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(args[0]?.ToString() ?? ""), ctx, p1, p2);
        }
    }
}

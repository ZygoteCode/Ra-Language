using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class CollectionBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("len", Len);
            BuiltInRegistry.Register("size", Len);
            BuiltInRegistry.Register("empty", Empty);
            BuiltInRegistry.Register("is_not_empty", NotEmpty);

            BuiltInRegistry.Register("keys", Keys);
            BuiltInRegistry.Register("values", Values);
            BuiltInRegistry.Register("entries", Entries);

            BuiltInRegistry.Register("list_push", ListPush);
            BuiltInRegistry.Register("list_pop", ListPop);
            BuiltInRegistry.Register("list_insert", ListInsert);
            BuiltInRegistry.Register("list_remove_at", ListRemoveAt);
            BuiltInRegistry.Register("list_remove_value", ListRemoveValue);
            BuiltInRegistry.Register("list_index_of", ListIndexOf);
            BuiltInRegistry.Register("list_last_index_of", ListLastIndexOf);
            BuiltInRegistry.Register("list_contains", ListContains);
            BuiltInRegistry.Register("list_reverse", ListReverse);
            BuiltInRegistry.Register("list_sort", ListSort);
            BuiltInRegistry.Register("list_sort_desc", ListSortDesc);
            BuiltInRegistry.Register("list_unique", ListUnique);
            BuiltInRegistry.Register("list_concat", ListConcat);
            BuiltInRegistry.Register("list_slice", ListSlice);
            BuiltInRegistry.Register("list_first", ListFirst);
            BuiltInRegistry.Register("list_last", ListLast);
            BuiltInRegistry.Register("list_take", ListTake);
            BuiltInRegistry.Register("list_drop", ListDrop);
            BuiltInRegistry.Register("list_chunk", ListChunk);
            BuiltInRegistry.Register("list_flatten", ListFlatten);
            BuiltInRegistry.Register("list_zip", ListZip);
            BuiltInRegistry.Register("list_count", ListCount);
            BuiltInRegistry.Register("list_range", ListRange);
            BuiltInRegistry.Register("list_fill", ListFill);
            BuiltInRegistry.Register("list_repeat", ListRepeat);
            BuiltInRegistry.Register("list_sum", ListSum);
            BuiltInRegistry.Register("list_product", ListProduct);
            BuiltInRegistry.Register("list_min", ListMin);
            BuiltInRegistry.Register("list_max", ListMax);
            BuiltInRegistry.Register("list_shuffle", ListShuffle);
            BuiltInRegistry.Register("list_clear", ListClear);

            BuiltInRegistry.Register("map_get", MapGet);
            BuiltInRegistry.Register("map_get_or", MapGetOr);
            BuiltInRegistry.Register("map_set", MapSet);
            BuiltInRegistry.Register("map_remove", MapRemove);
            BuiltInRegistry.Register("map_has", MapHas);
            BuiltInRegistry.Register("map_keys", MapKeys);
            BuiltInRegistry.Register("map_values", MapValuesFn);
            BuiltInRegistry.Register("map_entries", MapEntries);
            BuiltInRegistry.Register("map_size", MapSize);
            BuiltInRegistry.Register("map_clear", MapClear);
            BuiltInRegistry.Register("map_merge", MapMerge);
            BuiltInRegistry.Register("map_from_pairs", MapFromPairs);

            BuiltInRegistry.Register("set_add", SetAdd);
            BuiltInRegistry.Register("set_remove", SetRemove);
            BuiltInRegistry.Register("set_has", SetHas);
            BuiltInRegistry.Register("set_size", SetSize);
            BuiltInRegistry.Register("set_to_list", SetToList);
            BuiltInRegistry.Register("set_union", SetUnion);
            BuiltInRegistry.Register("set_intersect", SetIntersect);
            BuiltInRegistry.Register("set_diff", SetDiff);
            BuiltInRegistry.Register("set_clear", SetClear);

            BuiltInRegistry.Register("tuple_at", TupleAt);
            BuiltInRegistry.Register("tuple_size", TupleSize);
            BuiltInRegistry.Register("tuple_to_list", TupleToList);
            BuiltInRegistry.Register("tuple_first", TupleFirst);
            BuiltInRegistry.Register("tuple_second", TupleSecond);

            BuiltInRegistry.Register("make_tuple", MakeTupleFn);
            BuiltInRegistry.Register("make_list", MakeListFn);
            BuiltInRegistry.Register("make_set", MakeSetFn);
            BuiltInRegistry.Register("make_map", MakeMapFn);
        }

        private static RuntimeResult Len(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("len", args, 1, ctx, p1, p2, out var err)) return err;
            switch (args[0])
            {
                case StringValue sv: return Ok(new IntegerValue(System.Globalization.StringInfo.ParseCombiningCharacters(sv.Value).Length), ctx, p1, p2);
                case ListValue lv: return Ok(new IntegerValue(lv.Elements.Count), ctx, p1, p2);
                case MapValue mv: return Ok(new IntegerValue(mv.Pairs.Count), ctx, p1, p2);
                case SetValue setv: return Ok(new IntegerValue(setv.Elements.Count), ctx, p1, p2);
                case TupleValue tv: return Ok(new IntegerValue(tv.Elements.Count), ctx, p1, p2);
                case NullValue: return Ok(new IntegerValue(0), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, $"len: cannot get length of '{TypeKind(args[0])}'");
        }

        private static RuntimeResult Empty(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("empty", args, 1, ctx, p1, p2, out var err)) return err;
            switch (args[0])
            {
                case StringValue sv: return Ok(MakeBool(sv.Value.Length == 0), ctx, p1, p2);
                case ListValue lv: return Ok(MakeBool(lv.Elements.Count == 0), ctx, p1, p2);
                case MapValue mv: return Ok(MakeBool(mv.Pairs.Count == 0), ctx, p1, p2);
                case SetValue setv: return Ok(MakeBool(setv.Elements.Count == 0), ctx, p1, p2);
                case TupleValue tv: return Ok(MakeBool(tv.Elements.Count == 0), ctx, p1, p2);
                case NullValue: return Ok(MakeBool(true), ctx, p1, p2);
            }
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult NotEmpty(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var r = Empty(ctx, args, p1, p2);
            if (r.Error != null) return r;
            if (r.Value is BooleanValue bv) return Ok(MakeBool(!bv.Value), ctx, p1, p2);
            return r;
        }

        private static RuntimeResult Keys(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("keys", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is MapValue mv)
            {
                var list = new List<RuntimeValue>();
                foreach (var (k, _) in mv.Pairs) list.Add(k.Copy());
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            if (args[0] is SetValue setv)
            {
                var list = new List<RuntimeValue>();
                foreach (var e in setv.Elements) list.Add(e.Copy());
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, "keys: argument must be a map or set");
        }

        private static RuntimeResult Values(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("values", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is MapValue mv)
            {
                var list = new List<RuntimeValue>();
                foreach (var (_, v) in mv.Pairs) list.Add(v.Copy());
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, "values: argument must be a map");
        }

        private static RuntimeResult Entries(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("entries", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is MapValue mv)
            {
                var list = new List<RuntimeValue>();
                foreach (var (k, v) in mv.Pairs)
                    list.Add(new TupleValue(new List<RuntimeValue> { k.Copy(), v.Copy() }));
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            return Fail(ctx, p1, p2, "entries: argument must be a map");
        }

        private static List<RuntimeValue>? ListOf(RuntimeValue v) => v is ListValue lv ? lv.Elements : null;

        private static RuntimeResult ListPush(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("list_push", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_push: first arg must be a list");
            for (int i = 1; i < args.Count; i++) list.Add(args[i]);
            return Ok(args[0], ctx, p1, p2);
        }

        private static RuntimeResult ListPop(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_pop", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_pop: argument must be a list");
            if (list.Count == 0) return OkNull(ctx, p1, p2);
            var last = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return Ok(last, ctx, p1, p2);
        }

        private static RuntimeResult ListInsert(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_insert", args, 3, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_insert: first arg must be a list");
            int idx = AsInt(args[1]);
            if (idx < 0) idx = list.Count + idx;
            if (idx < 0 || idx > list.Count) return Fail(ctx, p1, p2, $"list_insert: index {idx} out of range");
            list.Insert(idx, args[2]);
            return Ok(args[0], ctx, p1, p2);
        }

        private static RuntimeResult ListRemoveAt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_remove_at", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_remove_at: first arg must be a list");
            int idx = AsInt(args[1]);
            if (idx < 0) idx = list.Count + idx;
            if (idx < 0 || idx >= list.Count) return Fail(ctx, p1, p2, $"list_remove_at: index {idx} out of range");
            var removed = list[idx];
            list.RemoveAt(idx);
            return Ok(removed, ctx, p1, p2);
        }

        private static RuntimeResult ListRemoveValue(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_remove_value", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_remove_value: first arg must be a list");
            for (int i = 0; i < list.Count; i++)
            {
                var (eq, _) = list[i].GetComparisonEq(args[1]);
                if (eq != null && eq.IsTrue()) { list.RemoveAt(i); return Ok(MakeBool(true), ctx, p1, p2); }
            }
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult ListIndexOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_index_of", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_index_of: first arg must be a list");
            for (int i = 0; i < list.Count; i++)
            {
                var (eq, _) = list[i].GetComparisonEq(args[1]);
                if (eq != null && eq.IsTrue()) return Ok(new IntegerValue(i), ctx, p1, p2);
            }
            return Ok(new IntegerValue(-1), ctx, p1, p2);
        }

        private static RuntimeResult ListLastIndexOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_last_index_of", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_last_index_of: first arg must be a list");
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var (eq, _) = list[i].GetComparisonEq(args[1]);
                if (eq != null && eq.IsTrue()) return Ok(new IntegerValue(i), ctx, p1, p2);
            }
            return Ok(new IntegerValue(-1), ctx, p1, p2);
        }

        private static RuntimeResult ListContains(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var r = ListIndexOf(ctx, args, p1, p2);
            if (r.Error != null) return r;
            if (r.Value is IntegerValue iv) return Ok(MakeBool(iv.Value >= 0), ctx, p1, p2);
            return r;
        }

        private static RuntimeResult ListReverse(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_reverse", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_reverse: arg must be a list");
            var rev = new List<RuntimeValue>(list);
            rev.Reverse();
            return Ok(new ListValue(rev), ctx, p1, p2);
        }

        private static RuntimeResult ListSort(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_sort", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_sort: arg must be a list");
            var copy = new List<RuntimeValue>(list);
            copy.Sort((a, b) =>
            {
                var (eq, _) = a.GetComparisonEq(b);
                if (eq != null && eq.IsTrue()) return 0;
                var (lt, _) = a.GetComparisonLt(b);
                if (lt != null && lt.IsTrue()) return -1;
                return 1;
            });
            return Ok(new ListValue(copy), ctx, p1, p2);
        }

        private static RuntimeResult ListSortDesc(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var r = ListSort(ctx, args, p1, p2);
            if (r.Error != null) return r;
            if (r.Value is ListValue lv) { lv.Elements.Reverse(); return Ok(lv, ctx, p1, p2); }
            return r;
        }

        private static RuntimeResult ListUnique(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_unique", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_unique: arg must be a list");
            var seen = new List<RuntimeValue>();
            foreach (var e in list)
            {
                bool found = false;
                foreach (var s in seen)
                {
                    var (eq, _) = s.GetComparisonEq(e);
                    if (eq != null && eq.IsTrue()) { found = true; break; }
                }
                if (!found) seen.Add(e);
            }
            return Ok(new ListValue(seen), ctx, p1, p2);
        }

        private static RuntimeResult ListConcat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var result = new List<RuntimeValue>();
            foreach (var a in args)
            {
                if (a is ListValue lv) result.AddRange(lv.Elements);
                else result.Add(a);
            }
            return Ok(new ListValue(result), ctx, p1, p2);
        }

        private static RuntimeResult ListSlice(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("list_slice", args, 2, 3, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_slice: first arg must be a list");
            int start = AsInt(args[1]);
            int end = args.Count == 3 ? AsInt(args[2]) : list.Count;
            if (start < 0) start = list.Count + start;
            if (end < 0) end = list.Count + end;
            start = Math.Clamp(start, 0, list.Count);
            end = Math.Clamp(end, 0, list.Count);
            if (end < start) end = start;
            var sub = new List<RuntimeValue>(end - start);
            for (int i = start; i < end; i++) sub.Add(list[i]);
            return Ok(new ListValue(sub), ctx, p1, p2);
        }

        private static RuntimeResult ListFirst(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_first", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_first: arg must be a list");
            return list.Count == 0 ? OkNull(ctx, p1, p2) : Ok(list[0], ctx, p1, p2);
        }

        private static RuntimeResult ListLast(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_last", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_last: arg must be a list");
            return list.Count == 0 ? OkNull(ctx, p1, p2) : Ok(list[list.Count - 1], ctx, p1, p2);
        }

        private static RuntimeResult ListTake(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_take", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_take: first arg must be a list");
            int n = Math.Max(0, AsInt(args[1]));
            n = Math.Min(n, list.Count);
            var sub = new List<RuntimeValue>(n);
            for (int i = 0; i < n; i++) sub.Add(list[i]);
            return Ok(new ListValue(sub), ctx, p1, p2);
        }

        private static RuntimeResult ListDrop(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_drop", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_drop: first arg must be a list");
            int n = Math.Max(0, AsInt(args[1]));
            n = Math.Min(n, list.Count);
            var sub = new List<RuntimeValue>(list.Count - n);
            for (int i = n; i < list.Count; i++) sub.Add(list[i]);
            return Ok(new ListValue(sub), ctx, p1, p2);
        }

        private static RuntimeResult ListChunk(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_chunk", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_chunk: first arg must be a list");
            int chunkSize = Math.Max(1, AsInt(args[1]));
            var outList = new List<RuntimeValue>();
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var chunk = new List<RuntimeValue>();
                for (int j = i; j < Math.Min(i + chunkSize, list.Count); j++) chunk.Add(list[j]);
                outList.Add(new ListValue(chunk));
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListFlatten(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_flatten", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_flatten: arg must be a list");
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                if (e is ListValue lv) outList.AddRange(lv.Elements);
                else outList.Add(e);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListZip(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("list_zip", args, 2, ctx, p1, p2, out var err)) return err;
            var lists = new List<List<RuntimeValue>>();
            foreach (var a in args)
            {
                if (a is not ListValue lv) return Fail(ctx, p1, p2, "list_zip: all args must be lists");
                lists.Add(lv.Elements);
            }
            int min = lists.Min(l => l.Count);
            var outList = new List<RuntimeValue>(min);
            for (int i = 0; i < min; i++)
            {
                var tup = new List<RuntimeValue>(lists.Count);
                foreach (var l in lists) tup.Add(l[i]);
                outList.Add(new TupleValue(tup));
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListCount(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_count", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_count: first arg must be a list");
            int c = 0;
            foreach (var e in list)
            {
                var (eq, _) = e.GetComparisonEq(args[1]);
                if (eq != null && eq.IsTrue()) c++;
            }
            return Ok(new IntegerValue(c), ctx, p1, p2);
        }

        private static RuntimeResult ListRange(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("list_range", args, 1, 3, ctx, p1, p2, out var err)) return err;
            long start, end, step;
            if (args.Count == 1) { start = 0; end = AsLong(args[0]); step = 1; }
            else if (args.Count == 2) { start = AsLong(args[0]); end = AsLong(args[1]); step = 1; }
            else { start = AsLong(args[0]); end = AsLong(args[1]); step = AsLong(args[2]); }
            if (step == 0) return Fail(ctx, p1, p2, "list_range: step cannot be 0");
            var outList = new List<RuntimeValue>();
            if (step > 0) for (long i = start; i < end; i += step) outList.Add(NumberFor(i));
            else for (long i = start; i > end; i += step) outList.Add(NumberFor(i));
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListFill(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_fill", args, 2, ctx, p1, p2, out var err)) return err;
            int n = Math.Max(0, AsInt(args[0]));
            var list = new List<RuntimeValue>(n);
            for (int i = 0; i < n; i++) list.Add(args[1]);
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult ListRepeat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_repeat", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_repeat: first arg must be a list");
            int n = Math.Max(0, AsInt(args[1]));
            var outList = new List<RuntimeValue>(list.Count * n);
            for (int i = 0; i < n; i++) outList.AddRange(list);
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListSum(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_sum", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_sum: arg must be a list");
            double sum = 0; bool anyFloat = false;
            foreach (var e in list)
            {
                if (e.Type == RuntimeValueType.Float || e.Type == RuntimeValueType.Double || e.Type == RuntimeValueType.Decimal) anyFloat = true;
                sum += AsDouble(e);
            }
            return Ok(anyFloat ? (RuntimeValue)new DoubleValue(sum) : NumberFor((long)sum), ctx, p1, p2);
        }

        private static RuntimeResult ListProduct(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_product", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_product: arg must be a list");
            double prod = 1; bool anyFloat = false;
            foreach (var e in list)
            {
                if (e.Type == RuntimeValueType.Float || e.Type == RuntimeValueType.Double || e.Type == RuntimeValueType.Decimal) anyFloat = true;
                prod *= AsDouble(e);
            }
            return Ok(anyFloat ? (RuntimeValue)new DoubleValue(prod) : NumberFor((long)prod), ctx, p1, p2);
        }

        private static RuntimeResult ListMin(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_min", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_min: arg must be a list");
            if (list.Count == 0) return OkNull(ctx, p1, p2);
            var min = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                var (lt, _) = list[i].GetComparisonLt(min);
                if (lt != null && lt.IsTrue()) min = list[i];
            }
            return Ok(min, ctx, p1, p2);
        }

        private static RuntimeResult ListMax(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_max", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_max: arg must be a list");
            if (list.Count == 0) return OkNull(ctx, p1, p2);
            var max = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                var (gt, _) = list[i].GetComparisonGt(max);
                if (gt != null && gt.IsTrue()) max = list[i];
            }
            return Ok(max, ctx, p1, p2);
        }

        private static readonly Random _rng = new Random();

        private static RuntimeResult ListShuffle(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_shuffle", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_shuffle: arg must be a list");
            var copy = new List<RuntimeValue>(list);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }
            return Ok(new ListValue(copy), ctx, p1, p2);
        }

        private static RuntimeResult ListClear(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_clear", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_clear: arg must be a list");
            list.Clear();
            return Ok(args[0], ctx, p1, p2);
        }

        private static int FindPair(MapValue mv, RuntimeValue key)
        {
            for (int i = 0; i < mv.Pairs.Count; i++)
            {
                var (k, _) = mv.Pairs[i];
                var (eq, _) = k.GetComparisonStrictEq(key);
                if (eq != null && eq.IsTrue()) return i;
                if (k.Equals(key)) return i;
            }
            return -1;
        }

        private static RuntimeResult MapGet(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_get", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_get: first arg must be a map");
            int idx = FindPair(mv, args[1]);
            return idx < 0 ? OkNull(ctx, p1, p2) : Ok(mv.Pairs[idx].Value, ctx, p1, p2);
        }

        private static RuntimeResult MapGetOr(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_get_or", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_get_or: first arg must be a map");
            int idx = FindPair(mv, args[1]);
            return idx < 0 ? Ok(args[2], ctx, p1, p2) : Ok(mv.Pairs[idx].Value, ctx, p1, p2);
        }

        private static RuntimeResult MapSet(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_set", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_set: first arg must be a map");
            int idx = FindPair(mv, args[1]);
            if (idx >= 0) mv.Pairs[idx] = (mv.Pairs[idx].Key, args[2]);
            else mv.Pairs.Add((args[1], args[2]));
            return Ok(args[2], ctx, p1, p2);
        }

        private static RuntimeResult MapRemove(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_remove", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_remove: first arg must be a map");
            int idx = FindPair(mv, args[1]);
            if (idx < 0) return Ok(MakeBool(false), ctx, p1, p2);
            mv.Pairs.RemoveAt(idx);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult MapHas(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_has", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_has: first arg must be a map");
            return Ok(MakeBool(FindPair(mv, args[1]) >= 0), ctx, p1, p2);
        }

        private static RuntimeResult MapKeys(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Keys(ctx, args, p1, p2);
        private static RuntimeResult MapValuesFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Values(ctx, args, p1, p2);
        private static RuntimeResult MapEntries(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Entries(ctx, args, p1, p2);

        private static RuntimeResult MapSize(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_size", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_size: arg must be a map");
            return Ok(new IntegerValue(mv.Pairs.Count), ctx, p1, p2);
        }

        private static RuntimeResult MapClear(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_clear", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "map_clear: arg must be a map");
            mv.Pairs.Clear();
            return Ok(args[0], ctx, p1, p2);
        }

        private static RuntimeResult MapMerge(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("map_merge", args, 1, ctx, p1, p2, out var err)) return err;
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var a in args)
            {
                if (a is not MapValue mv) return Fail(ctx, p1, p2, "map_merge: all args must be maps");
                foreach (var (k, v) in mv.Pairs)
                {
                    int idx = -1;
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        var (eq, _) = pairs[i].Item1.GetComparisonStrictEq(k);
                        if (eq != null && eq.IsTrue()) { idx = i; break; }
                    }
                    if (idx >= 0) pairs[idx] = (pairs[idx].Item1, v);
                    else pairs.Add((k, v));
                }
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult MapFromPairs(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_from_pairs", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "map_from_pairs: arg must be a list");
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var e in lv.Elements)
            {
                if (e is TupleValue tv && tv.Elements.Count >= 2) pairs.Add((tv.Elements[0], tv.Elements[1]));
                else if (e is ListValue il && il.Elements.Count >= 2) pairs.Add((il.Elements[0], il.Elements[1]));
                else return Fail(ctx, p1, p2, "map_from_pairs: each entry must be a 2-element list or tuple");
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult SetAdd(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("set_add", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_add: first arg must be a set");
            for (int i = 1; i < args.Count; i++)
            {
                bool exists = false;
                foreach (var e in sv.Elements) { if (e.Equals(args[i])) { exists = true; break; } }
                if (!exists) sv.Elements.Add(args[i]);
            }
            return Ok(args[0], ctx, p1, p2);
        }

        private static RuntimeResult SetRemove(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_remove", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_remove: first arg must be a set");
            RuntimeValue? target = null;
            foreach (var e in sv.Elements) { if (e.Equals(args[1])) { target = e; break; } }
            if (target == null) return Ok(MakeBool(false), ctx, p1, p2);
            sv.Elements.Remove(target);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult SetHas(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_has", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_has: first arg must be a set");
            foreach (var e in sv.Elements) if (e.Equals(args[1])) return Ok(MakeBool(true), ctx, p1, p2);
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult SetSize(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_size", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_size: arg must be a set");
            return Ok(new IntegerValue(sv.Elements.Count), ctx, p1, p2);
        }

        private static RuntimeResult SetToList(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_to_list", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_to_list: arg must be a set");
            return Ok(new ListValue(sv.Elements.ToList()), ctx, p1, p2);
        }

        private static bool ContainsByValueEq(IEnumerable<RuntimeValue> coll, RuntimeValue v)
        {
            foreach (var e in coll)
            {
                var (eq, _) = e.GetComparisonEq(v);
                if (eq != null && eq.IsTrue()) return true;
            }
            return false;
        }

        private static RuntimeResult SetUnion(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_union", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_union: args must be sets");
            var outList = new List<RuntimeValue>();
            foreach (var e in a.Elements) if (!ContainsByValueEq(outList, e)) outList.Add(e);
            foreach (var e in b.Elements) if (!ContainsByValueEq(outList, e)) outList.Add(e);
            var hs = new HashSet<RuntimeValue>(outList);
            return Ok(new SetValue(hs), ctx, p1, p2);
        }

        private static RuntimeResult SetIntersect(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_intersect", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_intersect: args must be sets");
            var hs = new HashSet<RuntimeValue>();
            foreach (var e in a.Elements) if (ContainsByValueEq(b.Elements, e)) hs.Add(e);
            return Ok(new SetValue(hs), ctx, p1, p2);
        }

        private static RuntimeResult SetDiff(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_diff", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_diff: args must be sets");
            var hs = new HashSet<RuntimeValue>();
            foreach (var e in a.Elements) if (!ContainsByValueEq(b.Elements, e)) hs.Add(e);
            return Ok(new SetValue(hs), ctx, p1, p2);
        }

        private static RuntimeResult SetClear(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_clear", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue sv) return Fail(ctx, p1, p2, "set_clear: arg must be a set");
            sv.Elements.Clear();
            return Ok(args[0], ctx, p1, p2);
        }

        private static RuntimeResult TupleAt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("tuple_at", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not TupleValue tv) return Fail(ctx, p1, p2, "tuple_at: first arg must be a tuple");
            int idx = AsInt(args[1]);
            if (idx < 0) idx = tv.Elements.Count + idx;
            if (idx < 0 || idx >= tv.Elements.Count) return Fail(ctx, p1, p2, $"tuple_at: index {idx} out of range");
            return Ok(tv.Elements[idx], ctx, p1, p2);
        }

        private static RuntimeResult TupleSize(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("tuple_size", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not TupleValue tv) return Fail(ctx, p1, p2, "tuple_size: arg must be a tuple");
            return Ok(new IntegerValue(tv.Elements.Count), ctx, p1, p2);
        }

        private static RuntimeResult TupleToList(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("tuple_to_list", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not TupleValue tv) return Fail(ctx, p1, p2, "tuple_to_list: arg must be a tuple");
            return Ok(new ListValue(new List<RuntimeValue>(tv.Elements)), ctx, p1, p2);
        }

        private static RuntimeResult TupleFirst(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("tuple_first", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not TupleValue tv) return Fail(ctx, p1, p2, "tuple_first: arg must be a tuple");
            if (tv.Elements.Count == 0) return OkNull(ctx, p1, p2);
            return Ok(tv.Elements[0], ctx, p1, p2);
        }

        private static RuntimeResult TupleSecond(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("tuple_second", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not TupleValue tv) return Fail(ctx, p1, p2, "tuple_second: arg must be a tuple");
            if (tv.Elements.Count < 2) return OkNull(ctx, p1, p2);
            return Ok(tv.Elements[1], ctx, p1, p2);
        }

        private static RuntimeResult MakeTupleFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => Ok(new TupleValue(new List<RuntimeValue>(args)), ctx, p1, p2);

        private static RuntimeResult MakeListFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => Ok(new ListValue(new List<RuntimeValue>(args)), ctx, p1, p2);

        private static RuntimeResult MakeSetFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var hs = new HashSet<RuntimeValue>();
            foreach (var a in args)
            {
                bool exists = false;
                foreach (var e in hs) { if (e.Equals(a)) { exists = true; break; } }
                if (!exists) hs.Add(a);
            }
            return Ok(new SetValue(hs), ctx, p1, p2);
        }

        private static RuntimeResult MakeMapFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (args.Count % 2 != 0) return Fail(ctx, p1, p2, "make_map: needs even number of arguments (key, value, key, value, ...)");
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            for (int i = 0; i < args.Count; i += 2) pairs.Add((args[i], args[i + 1]));
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }
    }
}

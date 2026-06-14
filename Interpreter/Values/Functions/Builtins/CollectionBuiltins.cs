using System.Threading.Tasks;
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

            // Predicate-driven higher-order list functions. Each accepts ANY
            // callable as the test — a `pred`, a lambda, or a plain
            // `fn(T) -> bool` (what flows in is what is called). Bare,
            // prose-like names because predicates are meant to read aloud:
            // `filter(xs, even)`, `any(users, is_admin)`. Short-circuit where
            // the semantics allow (any/all/find/take_while/drop_while).
            BuiltInRegistry.Register("filter", Filter);
            BuiltInRegistry.Register("reject", Reject);
            BuiltInRegistry.Register("find", Find);
            BuiltInRegistry.Register("find_index", FindIndex);
            BuiltInRegistry.Register("any", AnyMatch);
            BuiltInRegistry.Register("all", AllMatch);
            BuiltInRegistry.Register("none", NoneMatch);
            BuiltInRegistry.Register("count", CountMatch);
            BuiltInRegistry.Register("partition", Partition);
            BuiltInRegistry.Register("take_while", TakeWhile);
            BuiltInRegistry.Register("drop_while", DropWhile);

            // Transform / aggregate HOFs — same "the callable may be a `pred`,
            // a lambda, or a plain `fn`" contract, but producing new values
            // rather than booleans. Fills the list-side gap the stream HOFs
            // (stream_map / stream_reduce / …) already cover.
            BuiltInRegistry.Register("map", MapFn);
            BuiltInRegistry.Register("flat_map", FlatMap);
            BuiltInRegistry.Register("for_each", ForEach);
            BuiltInRegistry.Register("reduce", Reduce);
            BuiltInRegistry.Register("fold", Reduce);
            BuiltInRegistry.Register("sort_with", SortWith);
            BuiltInRegistry.Register("sort_by", SortBy);
            BuiltInRegistry.Register("group_by", GroupBy);
            BuiltInRegistry.Register("zip_with", ZipWith);
            BuiltInRegistry.Register("min_by", MinBy);
            BuiltInRegistry.Register("max_by", MaxBy);
            BuiltInRegistry.Register("sum_by", SumBy);

            // Structural + key-driven list helpers that round out the
            // functional surface. `scan` is `fold` that keeps every running
            // accumulator; `find_last` is `find` from the tail; `count_by` /
            // `distinct_by` are the frequency / de-dup counterparts of
            // `group_by`. `list_windows` / `intersperse` / `list_rotate` are
            // pure (no callable) reshapers.
            BuiltInRegistry.Register("list_windows", ListWindows);
            BuiltInRegistry.Register("intersperse", Intersperse);
            BuiltInRegistry.Register("list_rotate", ListRotate);
            BuiltInRegistry.Register("find_last", FindLast);
            BuiltInRegistry.Register("count_by", CountBy);
            BuiltInRegistry.Register("distinct_by", DistinctBy);
            BuiltInRegistry.Register("scan", Scan);

            // More list/map/set breadth: ends-slicing, splitting, transposition,
            // frequency, sortedness, and the map/set algebra the first cut left
            // out.
            BuiltInRegistry.Register("list_take_last", ListTakeLast);
            BuiltInRegistry.Register("list_drop_last", ListDropLast);
            BuiltInRegistry.Register("list_split_at", ListSplitAt);
            BuiltInRegistry.Register("list_find_indices", ListFindIndices);
            BuiltInRegistry.Register("list_count_value", ListCountValue);
            BuiltInRegistry.Register("list_frequencies", ListFrequencies);
            BuiltInRegistry.Register("list_average", ListAverage);
            BuiltInRegistry.Register("list_is_sorted", ListIsSorted);
            BuiltInRegistry.Register("list_transpose", ListTranspose);
            BuiltInRegistry.Register("list_dedup", ListDedup);
            BuiltInRegistry.Register("list_zip3", ListZip3);
            BuiltInRegistry.Register("map_map_values", MapMapValues);
            BuiltInRegistry.Register("map_filter", MapFilter);
            BuiltInRegistry.Register("map_invert", MapInvert);
            BuiltInRegistry.Register("set_symmetric_diff", SetSymmetricDiff);
            BuiltInRegistry.Register("set_is_subset", SetIsSubset);
            BuiltInRegistry.Register("set_is_superset", SetIsSuperset);
            BuiltInRegistry.Register("set_is_disjoint", SetIsDisjoint);

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

        // ---- predicate higher-order functions -----------------------------
        //
        // Synchronously apply a predicate (any callable) to a single element
        // and read its truthiness. Mirrors the stream HOFs' calling convention
        // (`FuncReturnValue ?? Value`, then `IsTrue()`) and the sync bridge
        // used by `invoke` / the delegate combinators. Returns the matched
        // flag plus any propagated error so callers can short-circuit on the
        // first failure rather than swallowing it.
        private static (bool match, Error? err) TestPredicate(BaseFunctionValue f, RuntimeValue element)
        {
            var r = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(
                f.Execute(new List<RuntimeValue> { element }));
            if (r.Error != null) return (false, r.Error);
            var v = r.FuncReturnValue ?? r.Value;
            return (v != null && v.IsTrue(), null);
        }

        // Validate the universal `(list, predicate)` shape shared by every HOF
        // below. Accepts any BaseFunctionValue as the predicate so a plain
        // `fn(T) -> bool` works exactly like a first-class `pred`.
        private static bool ExpectListPredicate(string name, List<RuntimeValue> args, Context ctx, Position p1, Position p2,
            out List<RuntimeValue> list, out BaseFunctionValue pred, out RuntimeResult err)
        {
            list = null!;
            pred = null!;
            if (!ExpectArgs(name, args, 2, ctx, p1, p2, out err)) return false;
            if (args[0] is not ListValue lv)
            {
                err = Fail(ctx, p1, p2, $"{name}: first argument must be a list");
                return false;
            }
            if (args[1] is not BaseFunctionValue f)
            {
                err = Fail(ctx, p1, p2, $"{name}: second argument must be a predicate or `fn(T) -> bool`");
                return false;
            }
            list = lv.Elements;
            pred = f;
            return true;
        }

        private static RuntimeResult ListWindows(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_windows", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_windows: first argument must be a list");
            int size = AsInt(args[1]);
            if (size <= 0) return Fail(ctx, p1, p2, "list_windows: window size must be positive");
            var outList = new List<RuntimeValue>();
            for (int i = 0; i + size <= list.Count; i++)
                outList.Add(new ListValue(list.GetRange(i, size)));
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult Intersperse(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("intersperse", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "intersperse: first argument must be a list");
            var sep = args[1];
            var outList = new List<RuntimeValue>(list.Count == 0 ? 0 : list.Count * 2 - 1);
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) outList.Add(sep);
                outList.Add(list[i]);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListRotate(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_rotate", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_rotate: first argument must be a list");
            int n = list.Count;
            if (n == 0) return Ok(new ListValue(new List<RuntimeValue>()), ctx, p1, p2);
            // Rotate LEFT by k (negative shifts right); element at index k leads.
            int k = ((AsInt(args[1]) % n) + n) % n;
            var outList = new List<RuntimeValue>(n);
            outList.AddRange(list.GetRange(k, n - k));
            outList.AddRange(list.GetRange(0, k));
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult FindLast(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("find_last", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var (match, perr) = TestPredicate(pred, list[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) return Ok(list[i], ctx, p1, p2);
            }
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult CountBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("count_by", args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            var pairs = new List<(RuntimeValue, RuntimeValue)>();   // key -> count
            foreach (var e in list)
            {
                var (k, perr) = Apply1(keyFn, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                int idx = -1;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var (eq, _) = pairs[i].Item1.GetComparisonEq(k!);
                    if (eq != null && eq.IsTrue()) { idx = i; break; }
                }
                if (idx < 0) pairs.Add((k!, new IntegerValue(1)));
                else pairs[idx] = (pairs[idx].Item1, new IntegerValue(((IntegerValue)pairs[idx].Item2).Value + 1));
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult DistinctBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("distinct_by", args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            var seen = new List<RuntimeValue>();
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (k, perr) = Apply1(keyFn, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                bool dup = false;
                foreach (var sk in seen)
                {
                    var (eq, _) = sk.GetComparisonEq(k!);
                    if (eq != null && eq.IsTrue()) { dup = true; break; }
                }
                if (!dup) { seen.Add(k!); outList.Add(e); }
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult Scan(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("scan", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "scan: first argument must be a list");
            if (args[2] is not BaseFunctionValue f) return Fail(ctx, p1, p2, "scan: third argument must be a function (acc, elem) -> acc");
            var acc = args[1];
            var outList = new List<RuntimeValue>(lv.Elements.Count + 1) { acc };   // include the seed
            foreach (var e in lv.Elements)
            {
                var (v, perr) = Apply2(f, acc, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                acc = v!;
                outList.Add(acc);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListTakeLast(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_take_last", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_take_last: first argument must be a list");
            int n = Math.Clamp(AsInt(args[1]), 0, list.Count);
            return Ok(new ListValue(list.GetRange(list.Count - n, n)), ctx, p1, p2);
        }

        private static RuntimeResult ListDropLast(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_drop_last", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_drop_last: first argument must be a list");
            int n = Math.Clamp(AsInt(args[1]), 0, list.Count);
            return Ok(new ListValue(list.GetRange(0, list.Count - n)), ctx, p1, p2);
        }

        private static RuntimeResult ListSplitAt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_split_at", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_split_at: first argument must be a list");
            int i = AsInt(args[1]);
            if (i < 0) i += list.Count;
            i = Math.Clamp(i, 0, list.Count);
            return Ok(new TupleValue(new List<RuntimeValue> { new ListValue(list.GetRange(0, i)), new ListValue(list.GetRange(i, list.Count - i)) }), ctx, p1, p2);
        }

        private static RuntimeResult ListFindIndices(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("list_find_indices", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            var outList = new List<RuntimeValue>();
            for (int i = 0; i < list.Count; i++)
            {
                var (match, perr) = TestPredicate(pred, list[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) outList.Add(new IntegerValue(i));
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListCountValue(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_count_value", args, 2, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_count_value: first argument must be a list");
            int c = 0;
            foreach (var e in list) { var (eq, _) = e.GetComparisonEq(args[1]); if (eq != null && eq.IsTrue()) c++; }
            return Ok(new IntegerValue(c), ctx, p1, p2);
        }

        private static RuntimeResult ListFrequencies(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_frequencies", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_frequencies: argument must be a list");
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var e in list)
            {
                int idx = -1;
                for (int i = 0; i < pairs.Count; i++) { var (eq, _) = pairs[i].Item1.GetComparisonEq(e); if (eq != null && eq.IsTrue()) { idx = i; break; } }
                if (idx < 0) pairs.Add((e, new IntegerValue(1)));
                else pairs[idx] = (pairs[idx].Item1, new IntegerValue(((IntegerValue)pairs[idx].Item2).Value + 1));
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult ListAverage(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_average", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_average: argument must be a list");
            if (list.Count == 0) return Fail(ctx, p1, p2, "list_average: list is empty");
            double sum = 0; foreach (var e in list) sum += AsDouble(e);
            return Ok(new DoubleValue(sum / list.Count), ctx, p1, p2);
        }

        private static RuntimeResult ListIsSorted(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_is_sorted", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_is_sorted: argument must be a list");
            for (int i = 1; i < list.Count; i++)
            {
                var (gt, _) = list[i - 1].GetComparisonGt(list[i]);
                if (gt != null && gt.IsTrue()) return Ok(MakeBool(false), ctx, p1, p2);
            }
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult ListTranspose(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_transpose", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } rows) return Fail(ctx, p1, p2, "list_transpose: argument must be a list of lists");
            if (rows.Count == 0) return Ok(new ListValue(new List<RuntimeValue>()), ctx, p1, p2);
            int cols = int.MaxValue;
            foreach (var r in rows) { if (r is not ListValue rl) return Fail(ctx, p1, p2, "list_transpose: every element must be a list"); cols = Math.Min(cols, rl.Elements.Count); }
            var outRows = new List<RuntimeValue>(cols);
            for (int c = 0; c < cols; c++)
            {
                var col = new List<RuntimeValue>(rows.Count);
                foreach (var r in rows) col.Add(((ListValue)r).Elements[c]);
                outRows.Add(new ListValue(col));
            }
            return Ok(new ListValue(outRows), ctx, p1, p2);
        }

        private static RuntimeResult ListDedup(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_dedup", args, 1, ctx, p1, p2, out var err)) return err;
            if (ListOf(args[0]) is not { } list) return Fail(ctx, p1, p2, "list_dedup: argument must be a list");
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                if (outList.Count > 0) { var (eq, _) = outList[outList.Count - 1].GetComparisonEq(e); if (eq != null && eq.IsTrue()) continue; }
                outList.Add(e);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ListZip3(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("list_zip3", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue a || args[1] is not ListValue b || args[2] is not ListValue c)
                return Fail(ctx, p1, p2, "list_zip3: all three arguments must be lists");
            int n = Math.Min(a.Elements.Count, Math.Min(b.Elements.Count, c.Elements.Count));
            var outList = new List<RuntimeValue>(n);
            for (int i = 0; i < n; i++)
                outList.Add(new TupleValue(new List<RuntimeValue> { a.Elements[i], b.Elements[i], c.Elements[i] }));
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult MapMapValues(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_map_values", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue m) return Fail(ctx, p1, p2, "map_map_values: first argument must be a map");
            if (args[1] is not BaseFunctionValue f) return Fail(ctx, p1, p2, "map_map_values: second argument must be a function");
            var pairs = new List<(RuntimeValue, RuntimeValue)>(m.Pairs.Count);
            foreach (var (k, v) in m.Pairs)
            {
                var (nv, perr) = Apply1(f, v);
                if (perr != null) return new RuntimeResult().Failure(perr);
                pairs.Add((k, nv!));
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult MapFilter(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_filter", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue m) return Fail(ctx, p1, p2, "map_filter: first argument must be a map");
            if (args[1] is not BaseFunctionValue f) return Fail(ctx, p1, p2, "map_filter: second argument must be a predicate over values");
            var pairs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var (k, v) in m.Pairs)
            {
                var (match, perr) = TestPredicate(f, v);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) pairs.Add((k, v));
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult MapInvert(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("map_invert", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not MapValue m) return Fail(ctx, p1, p2, "map_invert: argument must be a map");
            var pairs = new List<(RuntimeValue, RuntimeValue)>(m.Pairs.Count);
            foreach (var (k, v) in m.Pairs) pairs.Add((v, k));
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult SetSymmetricDiff(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_symmetric_diff", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_symmetric_diff: args must be sets");
            var hs = new HashSet<RuntimeValue>();
            foreach (var e in a.Elements) if (!ContainsByValueEq(b.Elements, e)) hs.Add(e);
            foreach (var e in b.Elements) if (!ContainsByValueEq(a.Elements, e)) hs.Add(e);
            return Ok(new SetValue(hs), ctx, p1, p2);
        }

        private static RuntimeResult SetIsSubset(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_is_subset", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_is_subset: args must be sets");
            foreach (var e in a.Elements) if (!ContainsByValueEq(b.Elements, e)) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult SetIsSuperset(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_is_superset", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_is_superset: args must be sets");
            foreach (var e in b.Elements) if (!ContainsByValueEq(a.Elements, e)) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult SetIsDisjoint(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("set_is_disjoint", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not SetValue a || args[1] is not SetValue b) return Fail(ctx, p1, p2, "set_is_disjoint: args must be sets");
            foreach (var e in a.Elements) if (ContainsByValueEq(b.Elements, e)) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult Filter(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("filter", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) outList.Add(e);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult Reject(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("reject", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (!match) outList.Add(e);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult Find(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("find", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) return Ok(e, ctx, p1, p2);
            }
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult FindIndex(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("find_index", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            for (int i = 0; i < list.Count; i++)
            {
                var (match, perr) = TestPredicate(pred, list[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) return Ok(new IntegerValue(i), ctx, p1, p2);
            }
            return Ok(new IntegerValue(-1), ctx, p1, p2);
        }

        private static RuntimeResult AnyMatch(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("any", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) return Ok(MakeBool(true), ctx, p1, p2);     // ∃ — short-circuit
            }
            return Ok(MakeBool(false), ctx, p1, p2);
        }

        private static RuntimeResult AllMatch(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("all", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (!match) return Ok(MakeBool(false), ctx, p1, p2);   // ∀ — short-circuit
            }
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult NoneMatch(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("none", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) return Ok(MakeBool(false), ctx, p1, p2);    // ¬∃ — short-circuit
            }
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult CountMatch(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("count", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            int c = 0;
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (match) c++;
            }
            return Ok(new IntegerValue(c), ctx, p1, p2);
        }

        private static RuntimeResult Partition(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("partition", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            var yes = new List<RuntimeValue>();
            var no = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                (match ? yes : no).Add(e);
            }
            return Ok(new TupleValue(new List<RuntimeValue> { new ListValue(yes), new ListValue(no) }), ctx, p1, p2);
        }

        private static RuntimeResult TakeWhile(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("take_while", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (match, perr) = TestPredicate(pred, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (!match) break;
                outList.Add(e);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult DropWhile(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListPredicate("drop_while", args, ctx, p1, p2, out var list, out var pred, out var err)) return err;
            int i = 0;
            for (; i < list.Count; i++)
            {
                var (match, perr) = TestPredicate(pred, list[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (!match) break;
            }
            var outList = new List<RuntimeValue>(list.Count - i);
            for (; i < list.Count; i++) outList.Add(list[i]);
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        // ---- transform / aggregate higher-order functions -----------------
        //
        // Call a callable for its VALUE (not its truthiness) — the producing
        // counterpart of TestPredicate. Used by map / reduce / sort_by / etc.
        private static (RuntimeValue? val, Error? err) Apply1(BaseFunctionValue f, RuntimeValue a)
        {
            var r = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(
                f.Execute(new List<RuntimeValue> { a }));
            if (r.Error != null) return (null, r.Error);
            return (r.FuncReturnValue ?? r.Value ?? NullValue.Null, null);
        }

        private static (RuntimeValue? val, Error? err) Apply2(BaseFunctionValue f, RuntimeValue a, RuntimeValue b)
        {
            var r = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(
                f.Execute(new List<RuntimeValue> { a, b }));
            if (r.Error != null) return (null, r.Error);
            return (r.FuncReturnValue ?? r.Value ?? NullValue.Null, null);
        }

        // The `(list, callable)` shape for transform HOFs (any callable, not
        // only a predicate — mirrors ExpectListPredicate with fn wording).
        private static bool ExpectListFn(string name, List<RuntimeValue> args, Context ctx, Position p1, Position p2,
            out List<RuntimeValue> list, out BaseFunctionValue fn, out RuntimeResult err)
        {
            list = null!;
            fn = null!;
            if (!ExpectArgs(name, args, 2, ctx, p1, p2, out err)) return false;
            if (args[0] is not ListValue lv) { err = Fail(ctx, p1, p2, $"{name}: first argument must be a list"); return false; }
            if (args[1] is not BaseFunctionValue f) { err = Fail(ctx, p1, p2, $"{name}: second argument must be a function"); return false; }
            list = lv.Elements;
            fn = f;
            return true;
        }

        // Carries a RuntimeError out of a List.Sort comparison callback so the
        // user callable's failure propagates cleanly instead of corrupting the
        // sort. Caught locally; never escapes the builtin.
        private sealed class CallbackError : System.Exception
        {
            public readonly Error Err;
            public CallbackError(Error err) { Err = err; }
        }

        private static RuntimeResult MapFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("map", args, ctx, p1, p2, out var list, out var f, out var err)) return err;
            var outList = new List<RuntimeValue>(list.Count);
            foreach (var e in list)
            {
                var (v, perr) = Apply1(f, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                outList.Add(v!);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult FlatMap(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("flat_map", args, ctx, p1, p2, out var list, out var f, out var err)) return err;
            var outList = new List<RuntimeValue>();
            foreach (var e in list)
            {
                var (v, perr) = Apply1(f, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (v is ListValue inner) outList.AddRange(inner.Elements);
                else outList.Add(v!);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult ForEach(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("for_each", args, ctx, p1, p2, out var list, out var f, out var err)) return err;
            foreach (var e in list)
            {
                var (_, perr) = Apply1(f, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
            }
            return Ok(args[0], ctx, p1, p2);   // fluent: returns the source list
        }

        private static RuntimeResult Reduce(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("reduce", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "reduce: first argument must be a list");
            if (args[2] is not BaseFunctionValue f) return Fail(ctx, p1, p2, "reduce: third argument must be a function (acc, elem) -> acc");
            var acc = args[1];
            foreach (var e in lv.Elements)
            {
                var (v, perr) = Apply2(f, acc, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                acc = v!;
            }
            return Ok(acc, ctx, p1, p2);
        }

        private static RuntimeResult SortWith(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("sort_with", args, ctx, p1, p2, out var list, out var cmp, out var err)) return err;
            var copy = new List<RuntimeValue>(list);
            try
            {
                // cmp(a, b) truthy means "a comes before b".
                copy.Sort((a, b) =>
                {
                    var (ab, e1) = Apply2(cmp, a, b);
                    if (e1 != null) throw new CallbackError(e1);
                    if (ab != null && ab.IsTrue()) return -1;
                    var (ba, e2) = Apply2(cmp, b, a);
                    if (e2 != null) throw new CallbackError(e2);
                    return (ba != null && ba.IsTrue()) ? 1 : 0;
                });
            }
            catch (CallbackError ce) { return new RuntimeResult().Failure(ce.Err); }
            return Ok(new ListValue(copy), ctx, p1, p2);
        }

        private static RuntimeResult SortBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("sort_by", args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            // Precompute keys once (Schwartzian transform) so the user callable
            // runs O(n) times, not O(n log n), and errors propagate before sorting.
            var keyed = new List<(RuntimeValue Key, RuntimeValue Val)>(list.Count);
            foreach (var e in list)
            {
                var (k, perr) = Apply1(keyFn, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                keyed.Add((k!, e));
            }
            keyed.Sort((x, y) =>
            {
                var (eq, _) = x.Key.GetComparisonEq(y.Key);
                if (eq != null && eq.IsTrue()) return 0;
                var (lt, _) = x.Key.GetComparisonLt(y.Key);
                return (lt != null && lt.IsTrue()) ? -1 : 1;
            });
            var outList = new List<RuntimeValue>(keyed.Count);
            foreach (var kv in keyed) outList.Add(kv.Val);
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        private static RuntimeResult GroupBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("group_by", args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            var pairs = new List<(RuntimeValue, RuntimeValue)>();   // key -> ListValue
            foreach (var e in list)
            {
                var (k, perr) = Apply1(keyFn, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                int idx = -1;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var (eq, _) = pairs[i].Item1.GetComparisonEq(k!);
                    if (eq != null && eq.IsTrue()) { idx = i; break; }
                }
                if (idx < 0) pairs.Add((k!, new ListValue(new List<RuntimeValue> { e })));
                else ((ListValue)pairs[idx].Item2).Elements.Add(e);
            }
            return Ok(new MapValue(pairs), ctx, p1, p2);
        }

        private static RuntimeResult ZipWith(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("zip_with", args, 3, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue a) return Fail(ctx, p1, p2, "zip_with: first argument must be a list");
            if (args[1] is not ListValue b) return Fail(ctx, p1, p2, "zip_with: second argument must be a list");
            if (args[2] is not BaseFunctionValue f) return Fail(ctx, p1, p2, "zip_with: third argument must be a function (a, b) -> c");
            int n = Math.Min(a.Elements.Count, b.Elements.Count);
            var outList = new List<RuntimeValue>(n);
            for (int i = 0; i < n; i++)
            {
                var (v, perr) = Apply2(f, a.Elements[i], b.Elements[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                outList.Add(v!);
            }
            return Ok(new ListValue(outList), ctx, p1, p2);
        }

        // element of `list` whose key (via keyFn) is the smallest / largest.
        private static RuntimeResult MinMaxBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string name, bool wantMax)
        {
            if (!ExpectListFn(name, args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            if (list.Count == 0) return OkNull(ctx, p1, p2);
            var (bestKey, e0) = Apply1(keyFn, list[0]);
            if (e0 != null) return new RuntimeResult().Failure(e0);
            var best = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                var (k, perr) = Apply1(keyFn, list[i]);
                if (perr != null) return new RuntimeResult().Failure(perr);
                var (cmp, _) = wantMax ? k!.GetComparisonGt(bestKey!) : k!.GetComparisonLt(bestKey!);
                if (cmp != null && cmp.IsTrue()) { best = list[i]; bestKey = k; }
            }
            return Ok(best, ctx, p1, p2);
        }

        private static RuntimeResult MinBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => MinMaxBy(ctx, args, p1, p2, "min_by", false);
        private static RuntimeResult MaxBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => MinMaxBy(ctx, args, p1, p2, "max_by", true);

        private static RuntimeResult SumBy(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectListFn("sum_by", args, ctx, p1, p2, out var list, out var keyFn, out var err)) return err;
            double sum = 0; bool anyFloat = false;
            foreach (var e in list)
            {
                var (k, perr) = Apply1(keyFn, e);
                if (perr != null) return new RuntimeResult().Failure(perr);
                if (k!.Type == RuntimeValueType.Float || k.Type == RuntimeValueType.Double || k.Type == RuntimeValueType.Decimal) anyFloat = true;
                sum += AsDouble(k);
            }
            return Ok(anyFloat ? (RuntimeValue)new DoubleValue(sum) : NumberFor((long)sum), ctx, p1, p2);
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

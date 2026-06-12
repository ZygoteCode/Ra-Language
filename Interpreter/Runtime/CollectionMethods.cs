using System.Collections.Generic;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    // Always-on fluent method surface on LISTS — `xs.map(f).filter(p).sort_by(k)`,
    // Dart/C#/JS-style. Maps a method name to the free-function built-in it
    // sugars; member access binds a BoundCollectionMethodValue that prepends the
    // receiver and dispatches to that built-in, so the method form and the free
    // form share one implementation. Lists only (the wrapped built-ins are
    // list-typed). Resolved AFTER user `extend` methods — never shadows them.
    //
    // The names include the snake_case canonical form plus the common
    // Dart/JS/LINQ aliases (`where`, `some`, `every`, `each`, `distinct`, …) so
    // the surface reads naturally to users coming from those languages.
    internal static class CollectionMethods
    {
        private static readonly Dictionary<string, string> s_list = new(System.StringComparer.Ordinal)
        {
            // --- callable higher-order functions (method -> built-in) ---
            { "map", "map" }, { "flat_map", "flat_map" }, { "flatMap", "flat_map" },
            { "for_each", "for_each" }, { "forEach", "for_each" }, { "each", "for_each" },
            { "filter", "filter" }, { "where", "filter" },
            { "reject", "reject" }, { "where_not", "reject" }, { "whereNot", "reject" },
            { "find", "find" }, { "first_where", "find" }, { "firstWhere", "find" },
            { "find_index", "find_index" }, { "findIndex", "find_index" }, { "index_where", "find_index" },
            { "any", "any" }, { "some", "any" },
            { "all", "all" }, { "every", "all" },
            { "none", "none" },
            { "count", "count" }, { "count_where", "count" }, { "countWhere", "count" },
            { "partition", "partition" },
            { "take_while", "take_while" }, { "takeWhile", "take_while" },
            { "drop_while", "drop_while" }, { "dropWhile", "drop_while" },
            { "reduce", "reduce" }, { "fold", "fold" },
            { "sort_with", "sort_with" }, { "sortWith", "sort_with" },
            { "sort_by", "sort_by" }, { "sortBy", "sort_by" },
            { "group_by", "group_by" }, { "groupBy", "group_by" },
            { "zip_with", "zip_with" }, { "zipWith", "zip_with" },
            { "min_by", "min_by" }, { "minBy", "min_by" },
            { "max_by", "max_by" }, { "maxBy", "max_by" },
            { "sum_by", "sum_by" }, { "sumBy", "sum_by" },

            // --- plain operations (no callable) ---
            { "len", "len" }, { "size", "size" }, { "length", "len" },
            { "is_empty", "empty" }, { "isEmpty", "empty" },
            { "is_not_empty", "is_not_empty" }, { "isNotEmpty", "is_not_empty" },
            { "reverse", "list_reverse" }, { "reversed", "list_reverse" },
            { "sort", "list_sort" }, { "sorted", "list_sort" }, { "sort_desc", "list_sort_desc" }, { "sortDesc", "list_sort_desc" },
            { "unique", "list_unique" }, { "distinct", "list_unique" },
            { "slice", "list_slice" }, { "sublist", "list_slice" },
            { "take", "list_take" }, { "drop", "list_drop" }, { "chunk", "list_chunk" },
            { "flatten", "list_flatten" },
            { "sum", "list_sum" }, { "product", "list_product" }, { "min", "list_min" }, { "max", "list_max" },
            { "contains", "list_contains" }, { "index_of", "list_index_of" }, { "indexOf", "list_index_of" },
            { "last_index_of", "list_last_index_of" }, { "lastIndexOf", "list_last_index_of" },
            { "first", "list_first" }, { "last", "list_last" },
            { "push", "list_push" }, { "add", "list_push" }, { "pop", "list_pop" },
            { "concat", "list_concat" }, { "insert", "list_insert" },
            { "remove_at", "list_remove_at" }, { "removeAt", "list_remove_at" },
            { "remove_value", "list_remove_value" }, { "remove", "list_remove_value" },
            { "repeat", "list_repeat" }, { "shuffle", "list_shuffle" }, { "clear", "list_clear" }, { "zip", "list_zip" },
        };

        public static bool TryBind(RuntimeValue target, string member, Context ctx, Position p1, Position p2, out RuntimeResult result)
        {
            result = default!;
            if (target.Type != RuntimeValueType.List) return false;
            if (!s_list.TryGetValue(member, out var builtin)) return false;
            if (!BuiltInRegistry.Contains(builtin)) return false;
            var bound = new BoundCollectionMethodValue(target, member, builtin).SetContext(ctx).SetPos(p1, p2);
            result = new RuntimeResult().Success(bound);
            return true;
        }
    }
}

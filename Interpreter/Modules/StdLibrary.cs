using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Functions.Builtins;

namespace RaLanguage.Interpreter.Modules
{
    // Canonical taxonomy that places every built-in function under a
    // std-library module path: std.prelude.* for the ergonomic prelude
    // surface, std.sys.* for the low-level / unsafe one. It is the single
    // source of truth the module resolver consults to synthesise the
    // virtual std modules and packages that `import std.prelude.io`,
    // `import std.prelude.*`, `import std.sys.ffi`, etc. resolve to.
    //
    // The map is DERIVED, not hand-maintained name-by-name:
    //   1. registry built-ins inherit their category from BuiltInRegistry
    //      group tags (captured at registration time, zero per-call churn);
    //   2. async / stream built-ins map by their public Names arrays;
    //   3. a small, stable override table places the switch-dispatched
    //      "direct" built-ins and refines the two mixed-category files
    //      (DebugBuiltins -> io/errors, RuntimeBuiltins -> reflect/errors).
    //
    // StdLibrary.Audit() proves the map covers EXACTLY the live built-in
    // function set, so a newly-added built-in that nobody categorised fails
    // loudly (surfaced by the `--selftest-stdlib` CLI and a .ra test). This
    // is what keeps "no orphan built-ins" an enforced invariant rather than
    // a hope, and what makes adding a future stdlib module a one-line edit.
    public static class StdLibrary
    {
        public const string Root = "std";
        public const string Prelude = "std.prelude";
        public const string Sys = "std.sys";

        private static readonly object _lock = new();
        private static volatile bool _built;

        // built-in name -> dotted module path  (e.g. "print" -> "std.prelude.io")
        private static Dictionary<string, string> _nameToModule =
            new(StringComparer.Ordinal);
        // dotted module path -> distinct, Ordinal-sorted member names
        private static Dictionary<string, List<string>> _moduleMembers =
            new(StringComparer.Ordinal);

        private static string GroupToModule(string group) => group switch
        {
            // The unsafe / advanced surface lives outside the prelude so a
            // blanket `import std.prelude.*` never drags it in implicitly.
            "asm" => Sys + ".asm",
            "ffi" => Sys + ".ffi",
            _ => Prelude + "." + group,
        };

        public static void EnsureBuilt()
        {
            if (_built) return;
            lock (_lock)
            {
                if (_built) return;
                BuiltInRegistry.EnsureInitialized();

                var map = new Dictionary<string, string>(StringComparer.Ordinal);

                // 1. Registry built-ins inherit their category group.
                foreach (var kv in BuiltInRegistry.Groups)
                    map[kv.Key] = GroupToModule(kv.Value);

                // 2. Async + stream built-ins (dispatched outside the registry).
                foreach (var n in AsyncBuiltins.Names) map[n] = Prelude + ".async";
                foreach (var n in StreamBuiltins.Names) map[n] = Prelude + ".stream";
                foreach (var n in AsyncStreamBuiltins.Names) map[n] = Prelude + ".stream";

                // 3. Switch-dispatched "direct" built-ins (the non-async part
                //    of Program._builtInFunctions; the async part is in step 2).
                Assign(map, Prelude + ".io", "print", "print_ret");
                Assign(map, Prelude + ".reflect",
                    "exists", "field_exists", "is_public", "is_field_public",
                    "is_field_static", "annotations_of", "has_annotation",
                    "annotation_arg", "annotation_targets");
                Assign(map, Prelude + ".runtime", "drop");
                Assign(map, Prelude + ".validate",
                    "validate", "validate_target", "validate_deferred", "coerce_value");
                Assign(map, Prelude + ".test", "run_tests");

                // 4. Refine the two registry files that mix concerns.
                //    DebugBuiltins -> console I/O + assertion helpers.
                Assign(map, Prelude + ".io",
                    "println", "print_no_newline", "eprint", "eprintln",
                    "read_line", "clear_console");
                Assign(map, Prelude + ".errors",
                    "assert", "assert_eq", "assert_ne", "assert_true", "assert_false",
                    "assert_approx", "panic", "todo", "unreachable", "warn");
                //    RuntimeBuiltins -> reflective access + error helpers
                //    (the value-semantics helpers such as clone/hash/equals
                //    stay under std.prelude.runtime via the group tag).
                Assign(map, Prelude + ".reflect",
                    "lookup", "lookup_global", "define", "scope_keys", "global_keys",
                    "invoke_method", "invoke_static", "new_instance",
                    "get_field", "set_field", "get_static_field", "set_static_field");
                Assign(map, Prelude + ".errors", "throw_error", "error_message");

                var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var kv in map)
                {
                    if (!members.TryGetValue(kv.Value, out var list))
                        members[kv.Value] = list = new List<string>();
                    list.Add(kv.Key);
                }
                foreach (var list in members.Values) list.Sort(StringComparer.Ordinal);

                _nameToModule = map;
                _moduleMembers = members;
                _built = true;
            }
        }

        private static void Assign(Dictionary<string, string> map, string module, params string[] names)
        {
            foreach (var n in names) map[n] = module;
        }

        // ----- query surface consulted by ModuleManager -------------------

        // True when `dottedPath` names a virtual module (a leaf category).
        public static bool IsModule(string dottedPath)
        {
            EnsureBuilt();
            return _moduleMembers.ContainsKey(dottedPath);
        }

        public static IReadOnlyList<string>? ModuleMembers(string dottedPath)
        {
            EnsureBuilt();
            return _moduleMembers.TryGetValue(dottedPath, out var list) ? list : null;
        }

        // True when at least one virtual module is a strict descendant of
        // `dottedPath` — i.e. dottedPath names a virtual package such as
        // "std", "std.prelude" or "std.sys".
        public static bool HasDescendants(string dottedPath)
        {
            EnsureBuilt();
            string prefix = dottedPath + ".";
            foreach (var key in _moduleMembers.Keys)
                if (key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }

        // Union of the members of `dottedPath` (if it is itself a module)
        // and of every virtual module beneath it — the symbol set a package
        // / wildcard import exposes.
        public static List<string> PackageMembers(string dottedPath)
        {
            EnsureBuilt();
            var result = new List<string>();
            string prefix = dottedPath + ".";
            foreach (var kv in _moduleMembers)
            {
                bool self = string.Equals(kv.Key, dottedPath, StringComparison.Ordinal);
                bool descendant = kv.Key.StartsWith(prefix, StringComparison.Ordinal);
                if (self || descendant) result.AddRange(kv.Value);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public static IReadOnlyCollection<string> AllModulePaths
        {
            get { EnsureBuilt(); return _moduleMembers.Keys; }
        }

        public static IReadOnlyCollection<string> AllCategorizedNames
        {
            get { EnsureBuilt(); return _nameToModule.Keys; }
        }

        public static bool TryGetModuleForName(string name, out string modulePath)
        {
            EnsureBuilt();
            return _nameToModule.TryGetValue(name, out modulePath!);
        }

        // True when a dotted std path names a virtual module or package (the
        // categorised built-ins / their packages), as opposed to a physical
        // std/*.ra file. The .rac packager uses this to SKIP bundling: virtual
        // modules carry no source — the runtime synthesises them from the
        // built-in store — so they must not be walked as file dependencies.
        public static bool IsVirtualStdPath(string dottedPath)
        {
            EnsureBuilt();
            if (string.Equals(dottedPath, Root, StringComparison.Ordinal)) return true; // bare "std"
            return _moduleMembers.ContainsKey(dottedPath) || HasDescendants(dottedPath);
        }

        // Sorted list of every known virtual std module path — used to make
        // "no such std module" diagnostics actionable.
        public static List<string> SortedModulePaths()
        {
            EnsureBuilt();
            var paths = new List<string>(_moduleMembers.Keys);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        // Coverage audit. `uncategorized` = live built-in names with no
        // module (a new built-in nobody placed). `phantom` = manifest names
        // that are not actually live (a typo / stale entry). Both empty ==
        // the taxonomy is complete and exact.
        public static void Audit(
            IEnumerable<string> liveBuiltinNames,
            out List<string> uncategorized,
            out List<string> phantom)
        {
            EnsureBuilt();
            var live = new HashSet<string>(liveBuiltinNames, StringComparer.Ordinal);
            uncategorized = new List<string>();
            phantom = new List<string>();
            foreach (var n in live)
                if (!_nameToModule.ContainsKey(n)) uncategorized.Add(n);
            foreach (var n in _nameToModule.Keys)
                if (!live.Contains(n)) phantom.Add(n);
            uncategorized.Sort(StringComparer.Ordinal);
            phantom.Sort(StringComparer.Ordinal);
        }
    }
}

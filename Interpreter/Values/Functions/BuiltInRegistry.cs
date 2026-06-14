using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions.Builtins;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Functions
{
    public delegate RuntimeResult BuiltInHandler(Context callerCtx, List<RuntimeValue> args, Position posStart, Position posEnd);

    public static class BuiltInRegistry
    {
        private static readonly Dictionary<string, BuiltInHandler> _registry =
            new(StringComparer.Ordinal);
        private static readonly object _lock = new();
        private static bool _initialized = false;

        // Category tag per registered built-in, captured at registration
        // time. Populated by RegisterGrouped() wrapping each category's
        // Register() call in EnsureInitialized — so the std-library module
        // taxonomy (see StdLibrary) is derived from the SAME source of
        // truth the runtime dispatches against, with zero churn to the
        // ~170 individual Register(...) call sites. The group string is the
        // lowercase category name (e.g. "collections", "fs", "math").
        private static readonly Dictionary<string, string> _groups =
            new(StringComparer.Ordinal);
        private static string? _currentGroup;

        public static IEnumerable<string> AllNames => _registry.Keys;
        public static int Count => _registry.Count;

        // Category of a registered built-in, or false when the name was
        // never registered through a grouped category (e.g. an ad-hoc
        // Register outside EnsureInitialized).
        public static bool TryGetGroup(string name, out string group)
            => _groups.TryGetValue(name, out group!);

        public static IReadOnlyDictionary<string, string> Groups => _groups;

        public static void Register(string name, BuiltInHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Built-in name required");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _registry[name] = handler;
            if (_currentGroup != null) _groups[name] = _currentGroup;
        }

        public static void Register(string name, Func<RuntimeValue, bool> predicate)
        {
            Register(name, (ctx, args, p1, p2) =>
            {
                if (args.Count != 1)
                {
                    return new RuntimeResult().Failure(new RuntimeError(p1, p2, $"{name} expects 1 argument", ctx));
                }
                return new RuntimeResult().Success(BooleanValue.Of(predicate(args[0])).SetContext(ctx).SetPos(p1, p2));
            });
        }

        public static bool TryGet(string name, out BuiltInHandler handler) =>
            _registry.TryGetValue(name, out handler!);

        public static bool Contains(string name) => _registry.ContainsKey(name);

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                // Each category registers under a group tag (captured in
                // _groups) so StdLibrary can map every built-in to a
                // std.prelude.* / std.sys.* module without a hand-kept list.
                RegisterGrouped("reflect", ReflectionBuiltins.Register);
                RegisterGrouped("runtime", RuntimeBuiltins.Register);
                RegisterGrouped("collections", CollectionBuiltins.Register);
                RegisterGrouped("text", StringBuiltins.Register);
                RegisterGrouped("math", MathBuiltins.Register);
                RegisterGrouped("convert", ConversionBuiltins.Register);
                RegisterGrouped("os", OsBuiltins.Register);
                RegisterGrouped("time", TimeBuiltins.Register);
                RegisterGrouped("fs", FsBuiltins.Register);
                RegisterGrouped("process", ProcessBuiltins.Register);
                RegisterGrouped("ffi", InteropBuiltins.Register);
                RegisterGrouped("debug", DebugBuiltins.Register);
                RegisterGrouped("asm", AsmBuiltins.Register);
                RegisterGrouped("regex", RegexBuiltins.Register);
                RegisterGrouped("func", DelegateBuiltins.Register);
                RegisterGrouped("func", PredicateBuiltins.Register);
                RegisterGrouped("encoding", EncodingBuiltins.Register);
                RegisterGrouped("bytes", BytesBuiltins.Register);
                RegisterGrouped("crypto", CryptoBuiltins.Register);
                RegisterGrouped("random", RandomBuiltins.Register);
                RegisterGrouped("serialize", SerializeBuiltins.Register);
                RegisterGrouped("serialize", TomlBuiltins.Register);
                RegisterGrouped("net", NetBuiltins.Register);
                RegisterGrouped("net", NetLiveBuiltins.Register);
                _initialized = true;
            }
        }

        // Runs a category's Register() with _currentGroup set so every
        // Register(...) it performs is tagged with `group`. Always called
        // under _lock from EnsureInitialized.
        private static void RegisterGrouped(string group, Action register)
        {
            _currentGroup = group;
            try { register(); }
            finally { _currentGroup = null; }
        }

        public static RuntimeResult Invoke(string name, Context callerCtx, List<RuntimeValue> args, Position posStart, Position posEnd)
        {
            if (!_registry.TryGetValue(name, out var handler))
            {
                return new RuntimeResult().Failure(new RuntimeError(posStart, posEnd, $"Unknown built-in '{name}'", callerCtx));
            }
            try
            {
                return handler(callerCtx, args, posStart, posEnd);
            }
            catch (RuntimeBuiltinException rbex)
            {
                return new RuntimeResult().Failure(new RuntimeError(posStart, posEnd, rbex.Message, callerCtx));
            }
            catch (Exception ex)
            {
                return new RuntimeResult().Failure(new RuntimeError(posStart, posEnd, $"{name}: {ex.GetType().Name}: {ex.Message}", callerCtx));
            }
        }
    }

    public sealed class RuntimeBuiltinException : Exception
    {
        public RuntimeBuiltinException(string message) : base(message) { }
    }
}

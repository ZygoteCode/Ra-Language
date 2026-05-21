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

        public static IEnumerable<string> AllNames => _registry.Keys;
        public static int Count => _registry.Count;

        public static void Register(string name, BuiltInHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Built-in name required");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _registry[name] = handler;
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
                ReflectionBuiltins.Register();
                RuntimeBuiltins.Register();
                CollectionBuiltins.Register();
                StringBuiltins.Register();
                MathBuiltins.Register();
                ConversionBuiltins.Register();
                OsBuiltins.Register();
                TimeBuiltins.Register();
                FsBuiltins.Register();
                ProcessBuiltins.Register();
                InteropBuiltins.Register();
                DebugBuiltins.Register();
                AsmBuiltins.Register();
                RegexBuiltins.Register();
                _initialized = true;
            }
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

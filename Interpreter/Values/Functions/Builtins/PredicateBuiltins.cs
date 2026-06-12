using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions.Predicates;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // Predicate-algebra combinators as free functions. They complement the
    // operator surface (`&` / `|` / `!`) and the method surface
    // (`.negate/.xor/.implies/.iff`) with point-free, variadic builders that
    // read like prose: `let ok = pred_all(adult, verified, !banned)`.
    //
    // The names mirror the quantifier HOFs (`all` / `any` / `none`) so the
    // vocabulary is learned once — `pred_all` builds the predicate the HOF
    // `all` would test with. (The bare `all_of` / `any_of` / `none_of`
    // spellings are reserved by the built-in `@all_of` / `@any_of` validator
    // ANNOTATIONS, so the combinators carry the unambiguous `pred_` prefix.)
    //
    // Every argument may be ANY callable — a first-class `pred`, a lambda, or
    // a plain `fn(T) -> bool` (auto-lifted via PredicateValue.Lift). The result
    // is always a composed PredicateValue, so it short-circuits at call time
    // and folds against the constant predicates (always_true / always_false).
    //
    // Registered under the "func" group → std.prelude.func.
    internal static class PredicateBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("pred_all", PredAll);
            BuiltInRegistry.Register("pred_any", PredAny);
            BuiltInRegistry.Register("pred_none", PredNone);
            BuiltInRegistry.Register("negate", Negate);
            // The constant predicates: callable on any element, always
            // true / false. As bare predicates they read naturally in HOFs
            // (`filter(xs, always_true)`) and compose like any other.
            BuiltInRegistry.Register("always_true", (RuntimeValue _) => true);
            BuiltInRegistry.Register("always_false", (RuntimeValue _) => false);
        }

        // Fold a varargs run of callables into one predicate with `&` (AND) or
        // `|` (OR). The empty fold returns the operator's identity element —
        // `pred_all()` = always_true, `pred_any()` = always_false — so the
        // result is always a usable predicate and the algebra stays total.
        private static RuntimeResult Fold(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string name, bool isAnd)
        {
            PredicateValue? acc = null;
            for (int i = 0; i < args.Count; i++)
            {
                var lifted = PredicateValue.Lift(args[i]);
                if (lifted == null)
                    return Fail(ctx, p1, p2, $"{name}: argument {i + 1} is not a predicate or `fn(T) -> bool`");
                acc = acc == null ? lifted : (isAnd ? acc.And(lifted) : acc.Or(lifted));
            }
            acc ??= PredicateValue.Constant(isAnd);
            return Ok(acc, ctx, p1, p2);
        }

        // pred_all(p1, …) — holds when EVERY argument holds (logical AND).
        private static RuntimeResult PredAll(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => Fold(ctx, args, p1, p2, "pred_all", isAnd: true);

        // pred_any(p1, …) — holds when ANY argument holds (logical OR).
        private static RuntimeResult PredAny(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => Fold(ctx, args, p1, p2, "pred_any", isAnd: false);

        // pred_none(p1, …) ≡ !pred_any(p1, …) — holds when NO argument holds.
        private static RuntimeResult PredNone(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var r = Fold(ctx, args, p1, p2, "pred_none", isAnd: false);
            if (r.Error != null) return r;
            return Ok(((PredicateValue)r.Value!).Not(), ctx, p1, p2);
        }

        private static RuntimeResult Negate(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("negate", args, 1, ctx, p1, p2, out var err)) return err;
            var lifted = PredicateValue.Lift(args[0]);
            if (lifted == null)
                return Fail(ctx, p1, p2, "negate: argument is not a predicate or `fn(T) -> bool`");
            return Ok(lifted.Not(), ctx, p1, p2);
        }
    }
}

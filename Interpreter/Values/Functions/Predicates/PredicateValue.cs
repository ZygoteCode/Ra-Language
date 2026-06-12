using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions.Predicates
{
    public enum PredicateKind : byte
    {
        Leaf,   // wraps a real FunctionValue / lambda
        Not,    // !Left
        And,    // Left && Right (short-circuit)
        Or,     // Left || Right (short-circuit)
        Xor,    // Left ^ Right  (no short-circuit; both evaluated)
        Const   // ignores its argument; always ConstValue (∧/∨ identity element)
    }

    // A first-class boolean function. A `pred` declaration or `pred(...)`
    // literal lowers to a normal FunctionValue (bool-returning) which
    // FunctionDefinitionHelper.Apply wraps in a *leaf* PredicateValue.
    // Composition operators (`&`, `|`, `!`) on predicates build composite
    // PredicateValues that short-circuit at call time — `(p & q)(x)` runs
    // `p(x)` and only evaluates `q(x)` when `p(x)` was true.
    //
    // A PredicateValue IS-A BaseFunctionValue, so it flows through the single
    // call chokepoint (FunctionCallExecutor.Invoke) and is usable anywhere an
    // `fn(T) -> bool` is wanted: passed to filter/any/all, stored in a slot,
    // returned from a function. The result of calling a predicate is ALWAYS a
    // BooleanValue — predicates are strictly boolean, with zero ambiguity.
    //
    // Mirrors ComposedFunctionValue / MulticastDelegateValue: a thin
    // BaseFunctionValue subclass, no new opcodes, AOT-safe (no reflection).
    public sealed class PredicateValue : BaseFunctionValue
    {
        public PredicateKind Kind { get; }

        // Leaf only: the underlying callable. Null for composites.
        public BaseFunctionValue? Inner { get; }

        // Composite operands. Right is null for Not / Leaf.
        public PredicateValue? Left { get; }
        public PredicateValue? Right { get; }

        // Const only: the fixed truth value (`always_true` / `always_false`).
        public bool ConstValue { get; }
        public bool IsConst => Kind == PredicateKind.Const;

        // Narrowing (user-defined type guard) metadata, set on leaves whose
        // body is exactly `param is T` / `param is not T`. Consumed by the
        // NarrowingAnalyzer to flow-type a call `p(v)` like an inline test.
        // Null on composites and non-guard leaves.
        public string? NarrowsParam { get; }
        public TypeDescriptor? NarrowsTo { get; }
        public bool NarrowsNegated { get; }

        public override RuntimeValueType Type => RuntimeValueType.Predicate;
        public override bool IsCopy => false;

        private static readonly Dictionary<string, RuntimeValue> s_noNamed =
            new Dictionary<string, RuntimeValue>(0);

        private PredicateValue(
            PredicateKind kind,
            string name,
            BaseFunctionValue? inner,
            PredicateValue? left,
            PredicateValue? right,
            string? narrowsParam = null,
            TypeDescriptor? narrowsTo = null,
            bool narrowsNegated = false,
            bool constValue = false)
            : base(name)
        {
            Kind = kind;
            Inner = inner;
            Left = left;
            Right = right;
            NarrowsParam = narrowsParam;
            NarrowsTo = narrowsTo;
            NarrowsNegated = narrowsNegated;
            ConstValue = constValue;
        }

        // ---- Construction -------------------------------------------------

        public static PredicateValue Leaf(
            BaseFunctionValue inner,
            string? narrowsParam = null,
            TypeDescriptor? narrowsTo = null,
            bool narrowsNegated = false)
        {
            var p = new PredicateValue(PredicateKind.Leaf, inner.Name, inner, null, null,
                narrowsParam, narrowsTo, narrowsNegated);
            p.SetContext(inner.Context).SetPos(inner.PositionStart, inner.PositionEnd);
            return p;
        }

        // Lift any callable into a predicate (idempotent for predicates).
        public static PredicateValue? Lift(RuntimeValue value)
        {
            if (value is PredicateValue pv) return pv;
            if (value is BaseFunctionValue fn) return Leaf(fn);
            return null;
        }

        // The constant predicates — `always_true` / `always_false`. These are
        // the identity elements of the composition algebra (∧ identity is
        // `true`, ∨ identity is `false`) and let the folds below collapse
        // `p & always_true → p`, `p | always_false → p`, `!always_true →
        // always_false` at compose time, with zero call-time overhead.
        public static PredicateValue Constant(bool value) =>
            new PredicateValue(PredicateKind.Const, value ? "always_true" : "always_false",
                null, null, null, constValue: value);

        private PredicateValue MakeComposite(PredicateKind kind, PredicateValue right, string sym)
        {
            var name = kind == PredicateKind.Not ? $"!{Name}" : $"({Name} {sym} {right.Name})";
            var p = new PredicateValue(kind, name, null, this, right);
            p.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return p;
        }

        private PredicateValue Negate()
        {
            // !always_true == always_false (and vice-versa).
            if (Kind == PredicateKind.Const) return Constant(!ConstValue);
            // !!p  ==  p — collapse double negation at compose time.
            if (Kind == PredicateKind.Not && Left != null) return Left;
            var p = new PredicateValue(PredicateKind.Not, $"!{Name}", null, this, null);
            p.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return p;
        }

        // ---- Composition operators ---------------------------------------
        // `&`/`|` dispatch here from BinaryOperationNodeVisitor (AST walk) and
        // from VmExecutor's BAnd/BOr generic path (IR) — one override covers
        // both. The right operand may be any callable; a plain fn(T)->bool is
        // auto-lifted so only the left side needs to be a predicate to anchor
        // a composition chain.

        public override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            var r = Lift(other);
            if (r == null) return (null, ComposeError("&", other));
            return (And(r), null);
        }

        public override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            var r = Lift(other);
            if (r == null) return (null, ComposeError("|", other));
            return (Or(r), null);
        }

        public override ValueResult Notted() => (Negate(), null);

        // NOTE: `p and q` / `p or q` are deliberately NOT overloaded to compose.
        // `and` / `or` are boolean control-flow operators the IR lowers to
        // short-circuit truthiness jumps (OP_AND_JZ / OP_OR_JNZ); overloading
        // the AST-level AndedBy/OredBy would silently diverge from that. Predicate
        // composition has ONE spelling — the operators `&` / `|` / `!` — keeping
        // the surface unambiguous.

        // Public composition surface — the method spellings (`p.negate()`,
        // `p.xor(q)`, `p.implies(q)`, `p.iff(q)`) reachable through member
        // access. `and` / `or` / `not` are Ra keywords and cannot be member
        // names, so those three live as the operators `&` / `|` / `!`.
        // Identity / annihilator folds that PRESERVE short-circuit side
        // effects: an operand is dropped only when the runtime would skip its
        // evaluation anyway (a constant in the unreachable position) or when
        // it is a no-op constant. `p & always_false` and `p | always_true`
        // are deliberately NOT folded — `p` still runs first.
        public PredicateValue And(PredicateValue other)
        {
            if (Kind == PredicateKind.Const) return ConstValue ? other : this;        // true & q → q ; false & q → false
            if (other.Kind == PredicateKind.Const && other.ConstValue) return this;    // p & true → p
            return MakeComposite(PredicateKind.And, other, "&");
        }

        public PredicateValue Or(PredicateValue other)
        {
            if (Kind == PredicateKind.Const) return ConstValue ? this : other;         // true | q → true ; false | q → q
            if (other.Kind == PredicateKind.Const && !other.ConstValue) return this;   // p | false → p
            return MakeComposite(PredicateKind.Or, other, "|");
        }

        public PredicateValue Not() => Negate();

        // Programmatic XOR (there is no `^` predicate operator — `^` is pow).
        public PredicateValue Xor(PredicateValue other)
        {
            var p = new PredicateValue(PredicateKind.Xor, $"({Name} ^ {other.Name})", null, this, other);
            p.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return p;
        }

        // `a implies b`  ≡  `!a | b`. Short-circuits: if `a` is false the
        // result is true without evaluating `b`.
        public PredicateValue Implies(PredicateValue other) => Negate().Or(other);

        // `a iff b`  ≡  `(a & b) | (!a & !b)` — boolean biconditional.
        public PredicateValue Iff(PredicateValue other) =>
            And(other).Or(Negate().And(other.Negate()));

        private Error ComposeError(string op, RuntimeValue other) =>
            new RuntimeError(PositionStart, PositionEnd,
                $"cannot combine a predicate with '{op}' and a value of type '{other.Type}'",
                Context!,
                code: DiagnosticCode.RuntimePredicateCompose,
                primaryLabel: $"right operand of predicate '{op}' is not callable",
                help: "predicate composition (`p & q`, `p | q`) needs a predicate or an `fn(T) -> bool` on both sides");

        // ---- Evaluation (short-circuit) ----------------------------------

        public override ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, s_noNamed, null);

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            var res = new RuntimeResult();

            switch (Kind)
            {
                case PredicateKind.Leaf:
                {
                    var r = await Inner!.ExecuteWithNamedArgs(positionalArgs, namedArgs, explicitTypeArgs);
                    if (r.Error != null) return res.Failure(r.Error);
                    return res.Success(BoolOf(r));
                }

                case PredicateKind.Not:
                {
                    var (t, e) = await EvalTruth(Left!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (e != null) return res.Failure(e);
                    return res.Success(BooleanValue.Of(!t));
                }

                case PredicateKind.And:
                {
                    var (lt, le) = await EvalTruth(Left!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (le != null) return res.Failure(le);
                    if (!lt) return res.Success(BooleanValue.False);          // short-circuit
                    var (rt, re) = await EvalTruth(Right!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (re != null) return res.Failure(re);
                    return res.Success(BooleanValue.Of(rt));
                }

                case PredicateKind.Or:
                {
                    var (lt, le) = await EvalTruth(Left!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (le != null) return res.Failure(le);
                    if (lt) return res.Success(BooleanValue.True);            // short-circuit
                    var (rt, re) = await EvalTruth(Right!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (re != null) return res.Failure(re);
                    return res.Success(BooleanValue.Of(rt));
                }

                case PredicateKind.Xor:
                {
                    var (lt, le) = await EvalTruth(Left!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (le != null) return res.Failure(le);
                    var (rt, re) = await EvalTruth(Right!, positionalArgs, namedArgs, explicitTypeArgs);
                    if (re != null) return res.Failure(re);
                    return res.Success(BooleanValue.Of(lt ^ rt));
                }

                case PredicateKind.Const:
                    return res.Success(BooleanValue.Of(ConstValue));
            }

            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                "unreachable predicate kind", Context!));
        }

        private static async ValueTask<(bool truth, Error? err)> EvalTruth(
            PredicateValue p,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            var r = await p.ExecuteWithNamedArgs(positionalArgs, namedArgs, explicitTypeArgs);
            if (r.Error != null) return (false, r.Error);
            var v = r.FuncReturnValue ?? r.Value;
            return (v != null && v.IsTrue(), null);
        }

        private static BooleanValue BoolOf(RuntimeResult r)
        {
            var v = r.FuncReturnValue ?? r.Value;
            return BooleanValue.Of(v != null && v.IsTrue());
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<pred {Name}>";
    }
}

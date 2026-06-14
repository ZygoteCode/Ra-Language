using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class MathBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("abs", Abs);
            BuiltInRegistry.Register("sign", Sign);
            BuiltInRegistry.Register("neg", Neg);
            BuiltInRegistry.Register("math_min", MinFn);
            BuiltInRegistry.Register("math_max", MaxFn);
            BuiltInRegistry.Register("clamp", Clamp);
            BuiltInRegistry.Register("floor", Floor);
            BuiltInRegistry.Register("ceil", Ceil);
            BuiltInRegistry.Register("round", Round);
            BuiltInRegistry.Register("trunc", Trunc);
            BuiltInRegistry.Register("sqrt", Sqrt);
            BuiltInRegistry.Register("cbrt", Cbrt);
            BuiltInRegistry.Register("pow", Pow);
            BuiltInRegistry.Register("exp", Exp);
            BuiltInRegistry.Register("log", Log);
            BuiltInRegistry.Register("log2", Log2);
            BuiltInRegistry.Register("log10", Log10);
            BuiltInRegistry.Register("sin", Sin);
            BuiltInRegistry.Register("cos", Cos);
            BuiltInRegistry.Register("tan", Tan);
            BuiltInRegistry.Register("asin", Asin);
            BuiltInRegistry.Register("acos", Acos);
            BuiltInRegistry.Register("atan", Atan);
            BuiltInRegistry.Register("atan2", Atan2);
            BuiltInRegistry.Register("sinh", Sinh);
            BuiltInRegistry.Register("cosh", Cosh);
            BuiltInRegistry.Register("tanh", Tanh);
            BuiltInRegistry.Register("deg_to_rad", DegToRad);
            BuiltInRegistry.Register("rad_to_deg", RadToDeg);
            BuiltInRegistry.Register("pi", Pi);
            BuiltInRegistry.Register("e", E);
            BuiltInRegistry.Register("tau", Tau);
            BuiltInRegistry.Register("inf", Inf);
            BuiltInRegistry.Register("nan", Nan);
            BuiltInRegistry.Register("is_nan", IsNan);
            BuiltInRegistry.Register("is_inf", IsInf);
            BuiltInRegistry.Register("is_finite", IsFinite);
            BuiltInRegistry.Register("gcd", Gcd);
            BuiltInRegistry.Register("lcm", Lcm);
            BuiltInRegistry.Register("factorial", FactorialFn);
            // random / random_int / random_float / random_seed moved to
            // std.prelude.random (RandomBuiltins) — single seedable PRNG.
            BuiltInRegistry.Register("bit_count", BitCountFn);
            BuiltInRegistry.Register("popcount", BitCountFn);
            BuiltInRegistry.Register("leading_zeros", LeadingZerosFn);
            BuiltInRegistry.Register("trailing_zeros", TrailingZerosFn);
            BuiltInRegistry.Register("bit_length", BitLengthFn);
            BuiltInRegistry.Register("rotl", RotateLeftFn);
            BuiltInRegistry.Register("rotr", RotateRightFn);
            BuiltInRegistry.Register("hypot", Hypot);

            // Interpolation / remapping — the staples of graphics, animation
            // and signal code. `lerp`/`inv_lerp` are inverses; `remap` chains
            // them to move a value between two ranges; `clamp01` is the unit
            // clamp that pairs with `lerp(a, b, clamp01(t))`.
            BuiltInRegistry.Register("lerp", Lerp);
            BuiltInRegistry.Register("inv_lerp", InvLerp);
            BuiltInRegistry.Register("remap", Remap);
            BuiltInRegistry.Register("clamp01", Clamp01);
            BuiltInRegistry.Register("copysign", CopySign);
            BuiltInRegistry.Register("fmod", FMod);
            BuiltInRegistry.Register("round_to", RoundTo);
            BuiltInRegistry.Register("is_even", IsEven);
            BuiltInRegistry.Register("is_odd", IsOdd);
            BuiltInRegistry.Register("next_pow2", NextPow2);
            // Descriptive statistics over a list (or a variadic number run,
            // exactly like math_min/math_max). Population variance / stddev.
            BuiltInRegistry.Register("mean", Mean);
            BuiltInRegistry.Register("median", Median);
            BuiltInRegistry.Register("variance", Variance);
            BuiltInRegistry.Register("stddev", StdDev);

            // Shaping, integer division, combinatorics and the extra
            // transcendentals the BCL exposes but the first cut skipped.
            BuiltInRegistry.Register("smoothstep", SmoothStep);
            BuiltInRegistry.Register("unit_step", StepFn);
            BuiltInRegistry.Register("wrap", Wrap);
            BuiltInRegistry.Register("ceil_div", CeilDiv);
            BuiltInRegistry.Register("floor_div", FloorDiv);
            BuiltInRegistry.Register("divmod", DivMod);
            BuiltInRegistry.Register("is_prime", IsPrime);
            BuiltInRegistry.Register("combinations", Combinations);
            BuiltInRegistry.Register("permutations", Permutations);
            BuiltInRegistry.Register("fibonacci", Fibonacci);
            BuiltInRegistry.Register("sigmoid", Sigmoid);
            BuiltInRegistry.Register("log_base", LogBase);
            BuiltInRegistry.Register("nth_root", NthRoot);
            BuiltInRegistry.Register("midpoint", Midpoint);
            BuiltInRegistry.Register("hypot3", Hypot3);
            BuiltInRegistry.Register("asinh", Asinh);
            BuiltInRegistry.Register("acosh", Acosh);
            BuiltInRegistry.Register("atanh", Atanh);
            BuiltInRegistry.Register("expm1", Expm1);
            BuiltInRegistry.Register("log1p", Log1p);
        }

        private static RuntimeResult D1(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string name, Func<double, double> f)
        {
            if (!ExpectArgs(name, args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new DoubleValue(f(AsDouble(args[0]))), ctx, p1, p2);
        }

        private static RuntimeResult Abs(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("abs", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is IntegerValue iv) return Ok(new IntegerValue(Math.Abs(iv.Value)), ctx, p1, p2);
            if (args[0] is LongValue lv) return Ok(new LongValue(Math.Abs(lv.Value)), ctx, p1, p2);
            return Ok(new DoubleValue(Math.Abs(AsDouble(args[0]))), ctx, p1, p2);
        }

        private static RuntimeResult Sign(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("sign", args, 1, ctx, p1, p2, out var err)) return err;
            double d = AsDouble(args[0]);
            return Ok(new IntegerValue(Math.Sign(d)), ctx, p1, p2);
        }

        private static RuntimeResult Neg(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("neg", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is IntegerValue iv) return Ok(new IntegerValue(-iv.Value), ctx, p1, p2);
            if (args[0] is LongValue lv) return Ok(new LongValue(-lv.Value), ctx, p1, p2);
            return Ok(new DoubleValue(-AsDouble(args[0])), ctx, p1, p2);
        }

        private static RuntimeResult MinFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("min", args, 1, ctx, p1, p2, out var err)) return err;
            if (args.Count == 1 && args[0] is ListValue lv) args = lv.Elements;
            if (args.Count == 0) return OkNull(ctx, p1, p2);
            var min = args[0];
            for (int i = 1; i < args.Count; i++)
            {
                var (lt, _) = args[i].GetComparisonLt(min);
                if (lt != null && lt.IsTrue()) min = args[i];
            }
            return Ok(min, ctx, p1, p2);
        }

        private static RuntimeResult MaxFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("max", args, 1, ctx, p1, p2, out var err)) return err;
            if (args.Count == 1 && args[0] is ListValue lv) args = lv.Elements;
            if (args.Count == 0) return OkNull(ctx, p1, p2);
            var max = args[0];
            for (int i = 1; i < args.Count; i++)
            {
                var (gt, _) = args[i].GetComparisonGt(max);
                if (gt != null && gt.IsTrue()) max = args[i];
            }
            return Ok(max, ctx, p1, p2);
        }

        private static RuntimeResult Clamp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("clamp", args, 3, ctx, p1, p2, out var err)) return err;
            double v = AsDouble(args[0]), lo = AsDouble(args[1]), hi = AsDouble(args[2]);
            return Ok(new DoubleValue(Math.Clamp(v, lo, hi)), ctx, p1, p2);
        }

        private static RuntimeResult Floor(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "floor", Math.Floor);
        private static RuntimeResult Ceil(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "ceil", Math.Ceiling);
        private static RuntimeResult Round(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "round", Math.Round);
        private static RuntimeResult Trunc(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "trunc", Math.Truncate);
        private static RuntimeResult Sqrt(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "sqrt", Math.Sqrt);
        private static RuntimeResult Cbrt(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "cbrt", Math.Cbrt);
        private static RuntimeResult Exp(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "exp", Math.Exp);
        private static RuntimeResult Log(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "log", Math.Log);
        private static RuntimeResult Log2(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "log2", Math.Log2);
        private static RuntimeResult Log10(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "log10", Math.Log10);
        private static RuntimeResult Sin(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "sin", Math.Sin);
        private static RuntimeResult Cos(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "cos", Math.Cos);
        private static RuntimeResult Tan(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "tan", Math.Tan);
        private static RuntimeResult Asin(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "asin", Math.Asin);
        private static RuntimeResult Acos(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "acos", Math.Acos);
        private static RuntimeResult Atan(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "atan", Math.Atan);
        private static RuntimeResult Sinh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "sinh", Math.Sinh);
        private static RuntimeResult Cosh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "cosh", Math.Cosh);
        private static RuntimeResult Tanh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "tanh", Math.Tanh);
        private static RuntimeResult DegToRad(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "deg_to_rad", x => x * (Math.PI / 180.0));
        private static RuntimeResult RadToDeg(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "rad_to_deg", x => x * (180.0 / Math.PI));

        private static RuntimeResult Pow(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("pow", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new DoubleValue(Math.Pow(AsDouble(args[0]), AsDouble(args[1]))), ctx, p1, p2);
        }

        private static RuntimeResult Atan2(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("atan2", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new DoubleValue(Math.Atan2(AsDouble(args[0]), AsDouble(args[1]))), ctx, p1, p2);
        }

        private static RuntimeResult Hypot(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("hypot", args, 2, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]);
            return Ok(new DoubleValue(Math.Sqrt(a * a + b * b)), ctx, p1, p2);
        }

        private static RuntimeResult Pi(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Ok(new DoubleValue(Math.PI), ctx, p1, p2);
        private static RuntimeResult E(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Ok(new DoubleValue(Math.E), ctx, p1, p2);
        private static RuntimeResult Tau(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Ok(new DoubleValue(Math.Tau), ctx, p1, p2);
        private static RuntimeResult Inf(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Ok(new DoubleValue(double.PositiveInfinity), ctx, p1, p2);
        private static RuntimeResult Nan(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => Ok(new DoubleValue(double.NaN), ctx, p1, p2);

        private static RuntimeResult IsNan(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_nan", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(double.IsNaN(AsDouble(args[0]))), ctx, p1, p2);
        }

        private static RuntimeResult IsInf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_inf", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(double.IsInfinity(AsDouble(args[0]))), ctx, p1, p2);
        }

        private static RuntimeResult IsFinite(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_finite", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(double.IsFinite(AsDouble(args[0]))), ctx, p1, p2);
        }

        private static RuntimeResult Gcd(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("gcd", args, 2, ctx, p1, p2, out var err)) return err;
            long a = Math.Abs(AsLong(args[0])), b = Math.Abs(AsLong(args[1]));
            while (b != 0) { (a, b) = (b, a % b); }
            return Ok(new LongValue(a), ctx, p1, p2);
        }

        private static RuntimeResult Lcm(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("lcm", args, 2, ctx, p1, p2, out var err)) return err;
            long a = AsLong(args[0]), b = AsLong(args[1]);
            if (a == 0 || b == 0) return Ok(new LongValue(0), ctx, p1, p2);
            long aa = Math.Abs(a), bb = Math.Abs(b);
            long g = aa; long bbb = bb;
            while (bbb != 0) { (g, bbb) = (bbb, g % bbb); }
            return Ok(new LongValue(aa / g * bb), ctx, p1, p2);
        }

        private static RuntimeResult FactorialFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("factorial", args, 1, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]);
            if (n < 0) return Fail(ctx, p1, p2, "factorial: argument must be non-negative");
            if (n > 20) return Fail(ctx, p1, p2, "factorial: argument too large for int64 (max 20)");
            long r = 1;
            for (long i = 2; i <= n; i++) r *= i;
            return Ok(new LongValue(r), ctx, p1, p2);
        }

        private static RuntimeResult BitCountFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("bit_count", args, 1, ctx, p1, p2, out var err)) return err;
            ulong v = (ulong)AsLong(args[0]);
            int count = System.Numerics.BitOperations.PopCount(v);
            return Ok(new IntegerValue(count), ctx, p1, p2);
        }

        private static RuntimeResult LeadingZerosFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("leading_zeros", args, 1, ctx, p1, p2, out var err)) return err;
            ulong v = (ulong)AsLong(args[0]);
            int count = System.Numerics.BitOperations.LeadingZeroCount(v);
            return Ok(new IntegerValue(count), ctx, p1, p2);
        }

        private static RuntimeResult TrailingZerosFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("trailing_zeros", args, 1, ctx, p1, p2, out var err)) return err;
            ulong v = (ulong)AsLong(args[0]);
            int count = v == 0 ? 64 : System.Numerics.BitOperations.TrailingZeroCount(v);
            return Ok(new IntegerValue(count), ctx, p1, p2);
        }

        private static RuntimeResult BitLengthFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("bit_length", args, 1, ctx, p1, p2, out var err)) return err;
            long v = Math.Abs(AsLong(args[0]));
            int len = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)v);
            return Ok(new IntegerValue(len), ctx, p1, p2);
        }

        private static RuntimeResult RotateLeftFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("rotl", args, 2, ctx, p1, p2, out var err)) return err;
            ulong v = (ulong)AsLong(args[0]);
            int k = AsInt(args[1]) & 63;
            return Ok(new LongValue((long)System.Numerics.BitOperations.RotateLeft(v, k)), ctx, p1, p2);
        }

        private static RuntimeResult RotateRightFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("rotr", args, 2, ctx, p1, p2, out var err)) return err;
            ulong v = (ulong)AsLong(args[0]);
            int k = AsInt(args[1]) & 63;
            return Ok(new LongValue((long)System.Numerics.BitOperations.RotateRight(v, k)), ctx, p1, p2);
        }

        private static RuntimeResult Lerp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("lerp", args, 3, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]), t = AsDouble(args[2]);
            return Ok(new DoubleValue(a + (b - a) * t), ctx, p1, p2);
        }

        private static RuntimeResult InvLerp(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("inv_lerp", args, 3, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]), v = AsDouble(args[2]);
            if (a == b) return Ok(new DoubleValue(0.0), ctx, p1, p2);
            return Ok(new DoubleValue((v - a) / (b - a)), ctx, p1, p2);
        }

        private static RuntimeResult Remap(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("remap", args, 5, ctx, p1, p2, out var err)) return err;
            double v = AsDouble(args[0]);
            double inLo = AsDouble(args[1]), inHi = AsDouble(args[2]);
            double outLo = AsDouble(args[3]), outHi = AsDouble(args[4]);
            if (inLo == inHi) return Ok(new DoubleValue(outLo), ctx, p1, p2);
            double t = (v - inLo) / (inHi - inLo);
            return Ok(new DoubleValue(outLo + (outHi - outLo) * t), ctx, p1, p2);
        }

        private static RuntimeResult Clamp01(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => D1(ctx, args, p1, p2, "clamp01", x => Math.Clamp(x, 0.0, 1.0));

        private static RuntimeResult CopySign(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("copysign", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new DoubleValue(Math.CopySign(AsDouble(args[0]), AsDouble(args[1]))), ctx, p1, p2);
        }

        private static RuntimeResult FMod(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("fmod", args, 2, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]);
            return Ok(new DoubleValue(b == 0.0 ? double.NaN : a % b), ctx, p1, p2);
        }

        private static RuntimeResult RoundTo(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("round_to", args, 2, ctx, p1, p2, out var err)) return err;
            int digits = AsInt(args[1]);
            if (digits < 0 || digits > 15) return Fail(ctx, p1, p2, "round_to: digits must be in [0, 15]");
            return Ok(new DoubleValue(Math.Round(AsDouble(args[0]), digits, MidpointRounding.AwayFromZero)), ctx, p1, p2);
        }

        private static RuntimeResult IsEven(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_even", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool((AsLong(args[0]) & 1L) == 0L), ctx, p1, p2);
        }

        private static RuntimeResult IsOdd(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_odd", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool((AsLong(args[0]) & 1L) != 0L), ctx, p1, p2);
        }

        private static RuntimeResult NextPow2(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("next_pow2", args, 1, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]);
            if (n <= 1) return Ok(new LongValue(1), ctx, p1, p2);
            ulong v = (ulong)(n - 1);
            int shift = 64 - System.Numerics.BitOperations.LeadingZeroCount(v);
            if (shift >= 63) return Fail(ctx, p1, p2, "next_pow2: result overflows int64");
            return Ok(new LongValue(1L << shift), ctx, p1, p2);
        }

        // Gather the numeric sample: either a single list argument or a
        // variadic run of numbers (mirrors math_min / math_max). Returns null
        // when the sample is empty so the caller can report it.
        private static List<double>? Sample(List<RuntimeValue> args)
        {
            IEnumerable<RuntimeValue> src = (args.Count == 1 && args[0] is ListValue lv) ? lv.Elements : args;
            var nums = new List<double>();
            foreach (var v in src) nums.Add(AsDouble(v));
            return nums.Count == 0 ? null : nums;
        }

        private static RuntimeResult Mean(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("mean", args, 1, ctx, p1, p2, out var err)) return err;
            if (Sample(args) is not { } s) return Fail(ctx, p1, p2, "mean: sample is empty");
            double sum = 0; foreach (var x in s) sum += x;
            return Ok(new DoubleValue(sum / s.Count), ctx, p1, p2);
        }

        private static RuntimeResult Median(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("median", args, 1, ctx, p1, p2, out var err)) return err;
            if (Sample(args) is not { } s) return Fail(ctx, p1, p2, "median: sample is empty");
            s.Sort();
            int n = s.Count;
            double m = (n & 1) == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
            return Ok(new DoubleValue(m), ctx, p1, p2);
        }

        // Population variance (divide by N). stddev is its square root.
        private static double VarianceOf(List<double> s)
        {
            double mean = 0; foreach (var x in s) mean += x; mean /= s.Count;
            double acc = 0; foreach (var x in s) { double d = x - mean; acc += d * d; }
            return acc / s.Count;
        }

        private static RuntimeResult Variance(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("variance", args, 1, ctx, p1, p2, out var err)) return err;
            if (Sample(args) is not { } s) return Fail(ctx, p1, p2, "variance: sample is empty");
            return Ok(new DoubleValue(VarianceOf(s)), ctx, p1, p2);
        }

        private static RuntimeResult StdDev(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("stddev", args, 1, ctx, p1, p2, out var err)) return err;
            if (Sample(args) is not { } s) return Fail(ctx, p1, p2, "stddev: sample is empty");
            return Ok(new DoubleValue(Math.Sqrt(VarianceOf(s))), ctx, p1, p2);
        }

        private static RuntimeResult SmoothStep(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("smoothstep", args, 3, ctx, p1, p2, out var err)) return err;
            double e0 = AsDouble(args[0]), e1 = AsDouble(args[1]), x = AsDouble(args[2]);
            double t = e0 == e1 ? (x < e0 ? 0.0 : 1.0) : Math.Clamp((x - e0) / (e1 - e0), 0.0, 1.0);
            return Ok(new DoubleValue(t * t * (3.0 - 2.0 * t)), ctx, p1, p2);
        }

        private static RuntimeResult StepFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("step", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new IntegerValue(AsDouble(args[1]) < AsDouble(args[0]) ? 0 : 1), ctx, p1, p2);
        }

        private static RuntimeResult Wrap(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("wrap", args, 3, ctx, p1, p2, out var err)) return err;
            double x = AsDouble(args[0]), lo = AsDouble(args[1]), hi = AsDouble(args[2]);
            double range = hi - lo;
            if (range == 0) return Ok(new DoubleValue(lo), ctx, p1, p2);
            double m = (x - lo) % range;
            if (m < 0) m += range;
            return Ok(new DoubleValue(lo + m), ctx, p1, p2);
        }

        private static long FloorDivL(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        private static RuntimeResult CeilDiv(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("ceil_div", args, 2, ctx, p1, p2, out var err)) return err;
            long b = AsLong(args[1]);
            if (b == 0) return Fail(ctx, p1, p2, "ceil_div: division by zero");
            return Ok(NumberFor(-FloorDivL(-AsLong(args[0]), b)), ctx, p1, p2);
        }

        private static RuntimeResult FloorDiv(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("floor_div", args, 2, ctx, p1, p2, out var err)) return err;
            long b = AsLong(args[1]);
            if (b == 0) return Fail(ctx, p1, p2, "floor_div: division by zero");
            return Ok(NumberFor(FloorDivL(AsLong(args[0]), b)), ctx, p1, p2);
        }

        private static RuntimeResult DivMod(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("divmod", args, 2, ctx, p1, p2, out var err)) return err;
            long a = AsLong(args[0]), b = AsLong(args[1]);
            if (b == 0) return Fail(ctx, p1, p2, "divmod: division by zero");
            long q = FloorDivL(a, b), r = a - q * b;
            return Ok(new TupleValue(new List<RuntimeValue> { NumberFor(q), NumberFor(r) }), ctx, p1, p2);
        }

        private static RuntimeResult IsPrime(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("is_prime", args, 1, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]);
            if (n < 2) return Ok(MakeBool(false), ctx, p1, p2);
            if (n < 4) return Ok(MakeBool(true), ctx, p1, p2);
            if ((n & 1) == 0 || n % 3 == 0) return Ok(MakeBool(false), ctx, p1, p2);
            for (long i = 5; i * i <= n; i += 6)
                if (n % i == 0 || n % (i + 2) == 0) return Ok(MakeBool(false), ctx, p1, p2);
            return Ok(MakeBool(true), ctx, p1, p2);
        }

        private static RuntimeResult Combinations(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("combinations", args, 2, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]), k = AsLong(args[1]);
            if (k < 0 || k > n || n < 0) return Ok(new LongValue(0), ctx, p1, p2);
            if (k > n - k) k = n - k;
            try
            {
                long r = 1;
                checked { for (long i = 0; i < k; i++) r = r * (n - i) / (i + 1); }
                return Ok(NumberFor(r), ctx, p1, p2);
            }
            catch (OverflowException) { return Fail(ctx, p1, p2, "combinations: result overflows int64"); }
        }

        private static RuntimeResult Permutations(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("permutations", args, 2, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]), k = AsLong(args[1]);
            if (k < 0 || k > n || n < 0) return Ok(new LongValue(0), ctx, p1, p2);
            try
            {
                long r = 1;
                checked { for (long i = 0; i < k; i++) r *= (n - i); }
                return Ok(NumberFor(r), ctx, p1, p2);
            }
            catch (OverflowException) { return Fail(ctx, p1, p2, "permutations: result overflows int64"); }
        }

        private static RuntimeResult Fibonacci(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("fibonacci", args, 1, ctx, p1, p2, out var err)) return err;
            long n = AsLong(args[0]);
            if (n < 0) return Fail(ctx, p1, p2, "fibonacci: argument must be non-negative");
            if (n > 92) return Fail(ctx, p1, p2, "fibonacci: argument too large for int64 (max 92)");
            long a = 0, b = 1;
            for (long i = 0; i < n; i++) (a, b) = (b, a + b);
            return Ok(new LongValue(a), ctx, p1, p2);
        }

        private static RuntimeResult Sigmoid(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
            => D1(ctx, args, p1, p2, "sigmoid", x => 1.0 / (1.0 + Math.Exp(-x)));

        private static RuntimeResult LogBase(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("log_base", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new DoubleValue(Math.Log(AsDouble(args[0]), AsDouble(args[1]))), ctx, p1, p2);
        }

        private static RuntimeResult NthRoot(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("nth_root", args, 2, ctx, p1, p2, out var err)) return err;
            double x = AsDouble(args[0]), n = AsDouble(args[1]);
            if (x < 0 && Math.Abs(n % 2 - 1) < 1e-9) return Ok(new DoubleValue(-Math.Pow(-x, 1.0 / n)), ctx, p1, p2);
            return Ok(new DoubleValue(Math.Pow(x, 1.0 / n)), ctx, p1, p2);
        }

        private static RuntimeResult Midpoint(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("midpoint", args, 2, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]);
            return Ok(new DoubleValue(a + (b - a) / 2.0), ctx, p1, p2);
        }

        private static RuntimeResult Hypot3(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("hypot3", args, 3, ctx, p1, p2, out var err)) return err;
            double a = AsDouble(args[0]), b = AsDouble(args[1]), c = AsDouble(args[2]);
            return Ok(new DoubleValue(Math.Sqrt(a * a + b * b + c * c)), ctx, p1, p2);
        }

        private static RuntimeResult Asinh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "asinh", Math.Asinh);
        private static RuntimeResult Acosh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "acosh", Math.Acosh);
        private static RuntimeResult Atanh(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "atanh", Math.Atanh);
        private static RuntimeResult Expm1(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "expm1", x => Math.Exp(x) - 1.0);
        private static RuntimeResult Log1p(Context ctx, List<RuntimeValue> args, Position p1, Position p2) => D1(ctx, args, p1, p2, "log1p", x => Math.Log(1.0 + x));
    }
}

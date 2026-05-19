using System;
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
        private static readonly Random _rng = new Random();

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
            BuiltInRegistry.Register("random", RandomFn);
            BuiltInRegistry.Register("random_int", RandomIntFn);
            BuiltInRegistry.Register("random_float", RandomFn);
            BuiltInRegistry.Register("random_seed", RandomSeedFn);
            BuiltInRegistry.Register("bit_count", BitCountFn);
            BuiltInRegistry.Register("popcount", BitCountFn);
            BuiltInRegistry.Register("leading_zeros", LeadingZerosFn);
            BuiltInRegistry.Register("trailing_zeros", TrailingZerosFn);
            BuiltInRegistry.Register("bit_length", BitLengthFn);
            BuiltInRegistry.Register("rotl", RotateLeftFn);
            BuiltInRegistry.Register("rotr", RotateRightFn);
            BuiltInRegistry.Register("hypot", Hypot);
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

        private static RuntimeResult RandomFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            return Ok(new DoubleValue(_rng.NextDouble()), ctx, p1, p2);
        }

        private static RuntimeResult RandomIntFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("random_int", args, 2, ctx, p1, p2, out var err)) return err;
            int lo = AsInt(args[0]), hi = AsInt(args[1]);
            if (hi < lo) (lo, hi) = (hi, lo);
            return Ok(new IntegerValue(_rng.Next(lo, hi + 1)), ctx, p1, p2);
        }

        private static RuntimeResult RandomSeedFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            return Ok(new NullValue(), ctx, p1, p2);
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
    }
}

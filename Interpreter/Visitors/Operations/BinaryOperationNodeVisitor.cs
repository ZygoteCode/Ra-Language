using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class BinaryOperationNodeVisitor : NodeVisitor<BinaryOperationNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(BinaryOperationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var left = res.Register(await interpreter.Visit(node.LeftNode, context));
            if (res.ShouldReturn()) return res;

            // Short-circuit `and` / `or` before evaluating the right-hand side.
            // The operands are coerced to booleans through IsTrue() so non-bool
            // values follow the same falsy/truthy rules as in `if` conditions.
            if (node.OpTok.Type == TokenType.KEYWORD)
            {
                var kw = (Keyword)node.OpTok.Value!;
                if (kw == Keyword.And)
                {
                    bool lt = left!.IsTrue();
                    if (!lt)
                    {
                        return res.Success(BooleanValue.Of(false).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                    }
                    var rightSc = res.Register(await interpreter.Visit(node.RightNode, context));
                    if (res.ShouldReturn()) return res;
                    return res.Success(BooleanValue.Of(rightSc!.IsTrue()).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                if (kw == Keyword.Or)
                {
                    bool lt = left!.IsTrue();
                    if (lt)
                    {
                        return res.Success(BooleanValue.Of(true).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                    }
                    var rightSc = res.Register(await interpreter.Visit(node.RightNode, context));
                    if (res.ShouldReturn()) return res;
                    return res.Success(BooleanValue.Of(rightSc!.IsTrue()).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            var right = res.Register(await interpreter.Visit(node.RightNode, context));
            if (res.ShouldReturn()) return res;

            var op = node.OpTok.Type;

            // Inline JIT-style fast path: same-typed primitive arithmetic / comparison.
            // Skips two virtual calls (Type getter is sealed so the JIT already
            // devirtualizes those, but AddedTo/SubbedBy/... are virtual on RuntimeValue
            // and dominate the hot loop). Falls through to the canonical operator
            // dispatch on any unsupported (op, types) combination, so semantics stay
            // identical — overflow checks, divide-by-zero, NaN/Inf trapping all match.
            if (left!.Type == right!.Type)
            {
                var fast = TryFastBinary(left, right, op, node, context);
                if (fast.HasValue)
                {
                    var (fr, fe) = fast.Value;
                    if (fe != null) return res.Failure(fe);
                    if (fr != null) return res.Success(fr.SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            (RuntimeValue? result, Error? error) = (null, null);

            switch (op)
            {
                case TokenType.PLUS: (result, error) = left.AddedTo(right); break;
                case TokenType.MINUS: (result, error) = left.SubbedBy(right); break;
                case TokenType.MUL: (result, error) = left.MultedBy(right); break;
                case TokenType.DIV: (result, error) = left.DivedBy(right); break;
                case TokenType.POW: (result, error) = left.PowedBy(right); break;
                case TokenType.EE: (result, error) = left.GetComparisonEq(right); break;
                case TokenType.NE: (result, error) = left.GetComparisonNe(right); break;
                case TokenType.LT: (result, error) = left.GetComparisonLt(right); break;
                case TokenType.GT: (result, error) = left.GetComparisonGt(right); break;
                case TokenType.LTE: (result, error) = left.GetComparisonLte(right); break;
                case TokenType.GTE: (result, error) = left.GetComparisonGte(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.And: (result, error) = left.AndedBy(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.Or: (result, error) = left.OredBy(right); break;
                case TokenType.BITWISE_LEFT_SHIFT: (result, error) = left.BitwiseLeftShiftedBy(right); break;
                case TokenType.BITWISE_RIGHT_SHIFT: (result, error) = left.BitwiseRightShiftedBy(right); break;
                case TokenType.MODULO: (result, error) = left.ModuledBy(right); break;
                case TokenType.BITWISE_AND: (result, error) = left.BitwiseAndedBy(right); break;
                case TokenType.BITWISE_OR: (result, error) = left.BitwiseOredBy(right); break;
                case TokenType.STRICT_EE: (result, error) = left.GetComparisonStrictEq(right); break;
                case TokenType.STRICT_NE: (result, error) = left.GetComparisonStrictNe(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.In: (result, error) = left.InCollection(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.NotIn:
                    (result, error) = left.InCollection(right);
                    if (error != null) return res;
                    result = result?.Notted().Item1!;
                    break;
            }

            if (error != null) return res.Failure(error);

            if (result == null)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    $"Binary operator '{node.OpTok.Value}' returned null result",
                    context));
            }

            return res.Success(result.SetPos(node.PositionStart, node.PositionEnd));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (RuntimeValue? result, Error? error)? TryFastBinary(
            RuntimeValue left, RuntimeValue right, TokenType op, BinaryOperationNode node, Context context)
        {
            var t = left.Type;

            if (t == RuntimeValueType.Integer)
            {
                int l = ((IntegerValue)left).Value;
                int r = ((IntegerValue)right).Value;
                switch (op)
                {
                    case TokenType.PLUS:
                        try { checked { return (IntegerValue.Of(l + r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Integer overflow", context)); }
                    case TokenType.MINUS:
                        try { checked { return (IntegerValue.Of(l - r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Integer overflow", context)); }
                    case TokenType.MUL:
                        try { checked { return (IntegerValue.Of(l * r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Integer overflow", context)); }
                    case TokenType.DIV:
                        if (r == 0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Division by zero", context));
                        return (IntegerValue.Of(l / r), null);
                    case TokenType.MODULO:
                        if (r == 0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Modulo by zero", context));
                        return (IntegerValue.Of(l % r), null);
                    case TokenType.EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.NE: return (BooleanValue.Of(l != r), null);
                    case TokenType.LT: return (BooleanValue.Of(l < r), null);
                    case TokenType.GT: return (BooleanValue.Of(l > r), null);
                    case TokenType.LTE: return (BooleanValue.Of(l <= r), null);
                    case TokenType.GTE: return (BooleanValue.Of(l >= r), null);
                    case TokenType.BITWISE_AND: return (IntegerValue.Of(l & r), null);
                    case TokenType.BITWISE_OR: return (IntegerValue.Of(l | r), null);
                    case TokenType.BITWISE_LEFT_SHIFT: return (IntegerValue.Of(l << r), null);
                    case TokenType.BITWISE_RIGHT_SHIFT: return (IntegerValue.Of(l >> r), null);
                }
                return null;
            }

            if (t == RuntimeValueType.Double)
            {
                double l = ((DoubleValue)left).Value;
                double r = ((DoubleValue)right).Value;
                switch (op)
                {
                    case TokenType.PLUS:
                    {
                        double v = l + r;
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Double overflow", context));
                        return (new DoubleValue(v), null);
                    }
                    case TokenType.MINUS:
                    {
                        double v = l - r;
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Double overflow", context));
                        return (new DoubleValue(v), null);
                    }
                    case TokenType.MUL:
                    {
                        double v = l * r;
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Double overflow", context));
                        return (new DoubleValue(v), null);
                    }
                    case TokenType.DIV:
                    {
                        if (r == 0.0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Division by zero", context));
                        double v = l / r;
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Double overflow", context));
                        return (new DoubleValue(v), null);
                    }
                    case TokenType.MODULO:
                    {
                        if (r == 0.0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Modulo by zero", context));
                        double v = l % r;
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Double overflow", context));
                        return (new DoubleValue(v), null);
                    }
                    case TokenType.EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.NE: return (BooleanValue.Of(l != r), null);
                    case TokenType.LT: return (BooleanValue.Of(l < r), null);
                    case TokenType.GT: return (BooleanValue.Of(l > r), null);
                    case TokenType.LTE: return (BooleanValue.Of(l <= r), null);
                    case TokenType.GTE: return (BooleanValue.Of(l >= r), null);
                }
                return null;
            }

            if (t == RuntimeValueType.Long)
            {
                long l = ((LongValue)left).Value;
                long r = ((LongValue)right).Value;
                switch (op)
                {
                    case TokenType.PLUS:
                        try { checked { return (new LongValue(l + r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Long overflow", context)); }
                    case TokenType.MINUS:
                        try { checked { return (new LongValue(l - r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Long overflow", context)); }
                    case TokenType.MUL:
                        try { checked { return (new LongValue(l * r), null); } }
                        catch { return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Long overflow", context)); }
                    case TokenType.DIV:
                        if (r == 0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Division by zero", context));
                        return (new LongValue(l / r), null);
                    case TokenType.MODULO:
                        if (r == 0) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Modulo by zero", context));
                        return (new LongValue(l % r), null);
                    case TokenType.EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.NE: return (BooleanValue.Of(l != r), null);
                    case TokenType.LT: return (BooleanValue.Of(l < r), null);
                    case TokenType.GT: return (BooleanValue.Of(l > r), null);
                    case TokenType.LTE: return (BooleanValue.Of(l <= r), null);
                    case TokenType.GTE: return (BooleanValue.Of(l >= r), null);
                }
                return null;
            }

            if (t == RuntimeValueType.Float)
            {
                float l = ((FloatValue)left).Value;
                float r = ((FloatValue)right).Value;
                switch (op)
                {
                    case TokenType.PLUS:
                    {
                        float v = l + r;
                        if (float.IsNaN(v) || float.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Float overflow", context));
                        return (new FloatValue(v), null);
                    }
                    case TokenType.MINUS:
                    {
                        float v = l - r;
                        if (float.IsNaN(v) || float.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Float overflow", context));
                        return (new FloatValue(v), null);
                    }
                    case TokenType.MUL:
                    {
                        float v = l * r;
                        if (float.IsNaN(v) || float.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Float overflow", context));
                        return (new FloatValue(v), null);
                    }
                    case TokenType.DIV:
                    {
                        if (r == 0.0f) return (null, new RuntimeError(right.PositionStart, right.PositionEnd, "Division by zero", context));
                        float v = l / r;
                        if (float.IsNaN(v) || float.IsInfinity(v))
                            return (null, new RuntimeError(node.PositionStart, node.PositionEnd, "Float overflow", context));
                        return (new FloatValue(v), null);
                    }
                    case TokenType.EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.NE: return (BooleanValue.Of(l != r), null);
                    case TokenType.LT: return (BooleanValue.Of(l < r), null);
                    case TokenType.GT: return (BooleanValue.Of(l > r), null);
                    case TokenType.LTE: return (BooleanValue.Of(l <= r), null);
                    case TokenType.GTE: return (BooleanValue.Of(l >= r), null);
                }
                return null;
            }

            if (t == RuntimeValueType.Boolean)
            {
                bool l = ((BooleanValue)left).Value;
                bool r = ((BooleanValue)right).Value;
                switch (op)
                {
                    case TokenType.EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.NE: return (BooleanValue.Of(l != r), null);
                    case TokenType.STRICT_EE: return (BooleanValue.Of(l == r), null);
                    case TokenType.STRICT_NE: return (BooleanValue.Of(l != r), null);
                }
                return null;
            }

            return null;
        }
    }
}

using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class RetryNodeVisitor : NodeVisitor<RetryNode>
    {
        protected sealed override RuntimeResult VisitNode(RetryNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            (int?, Error?) ExtractRetryInt(RuntimeValue value, AstNode sourceNode, string label)
            {
                static bool IsWhole(double d) => Math.Abs(d - Math.Truncate(d)) <= 0.000001d;

                switch (value.Type)
                {
                    case RuntimeValueType.Byte:
                        return (((ByteValue)value).Value, null);

                    case RuntimeValueType.Short:
                        return (((ShortValue)value).Value, null);

                    case RuntimeValueType.UnsignedShort:
                        return (((UnsignedShortValue)value).Value, null);

                    case RuntimeValueType.Integer:
                        return (((IntegerValue)value).Value, null);

                    case RuntimeValueType.UnsignedInteger:
                        {
                            uint v = ((UnsignedIntegerValue)value).Value;
                            if (v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Long:
                        {
                            long v = ((LongValue)value).Value;
                            if (v < int.MinValue || v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.UnsignedLong:
                        {
                            ulong v = ((UnsignedLongValue)value).Value;
                            if (v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Int128:
                        {
                            var v = ((Int128Value)value).Value;
                            if (v < int.MinValue || v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.UnsignedInt128:
                        {
                            var v = ((UnsignedInt128Value)value).Value;
                            if (v > (UInt128)int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Float:
                        {
                            float v = ((FloatValue)value).Value;
                            if (!IsWhole(v) || v < 0f)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} must be a non-negative integer", context));
                            if (v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Double:
                        {
                            double v = ((DoubleValue)value).Value;
                            if (!IsWhole(v) || v < 0d)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} must be a non-negative integer", context));
                            if (v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Decimal:
                        {
                            decimal v = ((DecimalValue)value).Value;
                            if (v != decimal.Truncate(v) || v < 0m)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} must be a non-negative integer", context));
                            if (v > int.MaxValue)
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"{label} is too large", context));
                            return ((int)v, null);
                        }

                    case RuntimeValueType.Number:
                        {
                            var s = ((NumberValue)value).Value.ToString();
                            if (!int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
                                return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"Invalid {label}", context));
                            return ((int)i, null);
                        }

                    default:
                        return (null, new RuntimeError(sourceNode.PositionStart, sourceNode.PositionEnd, $"Expected numeric expression for {label}", context));
                }
            }

            RuntimeValue countValue = res.Register(interpreter.Visit(node.CountNode, context));
            if (res.ShouldReturn()) return res;

            int retries = -1;
            var _retries = ExtractRetryInt(countValue, node.CountNode, "retry count");

            if (_retries.Item2 != null)
            {
                return res.Failure(_retries.Item2);
            }

            retries = _retries.Item1 ?? -1;
            if (retries < 0)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "retry count cannot be negative", context));

            int delayMs = 0;
            if (node.DelayNode != null)
            {
                RuntimeValue delayValue = res.Register(interpreter.Visit(node.DelayNode, context));
                if (res.ShouldReturn()) return res;

                var _delayMs = ExtractRetryInt(delayValue, node.DelayNode, "delay");
                
                if (_delayMs.Item2 != null)
                {
                    return res.Failure(_delayMs.Item2);
                }

                delayMs = _delayMs.Item1 ?? -1;
                if (delayMs < 0)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "delay cannot be negative", context));
            }

            RuntimeError? lastError = null;

            if (retries == 0)
            {
                if (node.ElseNode != null)
                {
                    var elseRes = res.Register(interpreter.Visit(node.ElseNode, context));
                    if (res.Error != null) return res;
                    return res.Success(elseRes ?? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            for (int attempt = 0; attempt < retries; attempt++)
            {
                var attemptContext = context.Copy();
                var bodyRes = interpreter.Visit(node.BodyNode, attemptContext);

                if (bodyRes.Error == null)
                {
                    context.ApplyChangesFrom(attemptContext);
                    attemptContext.Dispose();

                    if (bodyRes.FuncReturnValue != null)
                        return res.SuccessReturn(bodyRes.FuncReturnValue);

                    if (bodyRes.LoopShouldBreak)
                        return res.SuccessBreak();

                    if (bodyRes.LoopShouldContinue)
                        return res.SuccessContinue();

                    if (bodyRes.ShouldYield)
                        return res.SuccessYield(bodyRes.YieldValue!);

                    return res.Success(bodyRes.Value);
                }
                else
                {
                    attemptContext.Dispose();
                }

                if (bodyRes.FuncReturnValue != null || bodyRes.LoopShouldBreak || bodyRes.LoopShouldContinue || bodyRes.ShouldYield)
                {
                    return bodyRes;
                }

                lastError = bodyRes.Error as RuntimeError ?? new RuntimeError(
                    bodyRes.Error.PositionStart,
                    bodyRes.Error.PositionEnd,
                    bodyRes.Error.Details ?? "Retry failed",
                    context);

                if (attempt < retries - 1 && delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
            }

            if (node.ElseNode != null)
            {
                var elseRes = res.Register(interpreter.Visit(node.ElseNode, context));
                if (res.ShouldReturn()) return res;

                return res.Success(elseRes ?? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(lastError ?? new RuntimeError(node.PositionStart, node.PositionEnd, "Retry failed", context));
        }
    }
}
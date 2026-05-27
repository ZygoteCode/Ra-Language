using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class ListAssignmentNodeVisitor : NodeVisitor<ListAssignmentNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ListAssignmentNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(ListAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.Target.NodeType != AstNodeType.ListAccess)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    "Invalid assignment target. Target must be a list element.", context
                ));
            }

            ListAccessNode listAccessNode = (ListAccessNode)node.Target;
            var targetList = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(listAccessNode.Target, context, interpreter));
            if (res.ShouldReturn()) return res;

            var indexValue = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(listAccessNode.Index, context, interpreter));
            if (res.ShouldReturn()) return res;

            var valueToAssign = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Value, context, interpreter));
            if (res.ShouldReturn()) return res;

            RuntimeValue finalValue = valueToAssign;

            if (node.AssignmentToken.Type != TokenType.EQ)
            {
                var accessResult = targetList.ListAccess(indexValue);
                if (accessResult.Item2 != null) return res.Failure(accessResult.Item2);

                var currentValue = accessResult.Item1!;
                (RuntimeValue? result, Error? error) = (null, null);

                switch (node.AssignmentToken.Type)
                {
                    case TokenType.PLUS_EQ: (result, error) = currentValue.AddedTo(valueToAssign); break;
                    case TokenType.MINUS_EQ: (result, error) = currentValue.SubbedBy(valueToAssign); break;
                    case TokenType.MUL_EQ: (result, error) = currentValue.MultedBy(valueToAssign); break;
                    case TokenType.DIV_EQ: (result, error) = currentValue.DivedBy(valueToAssign); break;
                    case TokenType.MODULO_EQ: (result, error) = currentValue.ModuledBy(valueToAssign); break;
                    case TokenType.BITWISE_AND_EQ: (result, error) = currentValue.BitwiseAndedBy(valueToAssign); break;
                    case TokenType.BITWISE_OR_EQ: (result, error) = currentValue.BitwiseOredBy(valueToAssign); break;
                    case TokenType.BITWISE_LEFT_SHIFT_EQ: (result, error) = currentValue.BitwiseLeftShiftedBy(valueToAssign); break;
                    case TokenType.BITWISE_RIGHT_SHIFT_EQ: (result, error) = currentValue.BitwiseRightShiftedBy(valueToAssign); break;
                    case TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ: (result, error) = currentValue.BitwiseLeftShiftedBy(valueToAssign); break;
                    case TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ: (result, error) = currentValue.BitwiseUnsignedRightShiftedBy(valueToAssign); break;
                    case TokenType.BITWISE_ROTATE_LEFT_EQ: (result, error) = currentValue.BitwiseRotateLeftedBy(valueToAssign); break;
                    case TokenType.BITWISE_ROTATE_RIGHT_EQ: (result, error) = currentValue.BitwiseRotateRightedBy(valueToAssign); break;
                    case TokenType.POW_EQ: (result, error) = currentValue.PowedBy(valueToAssign); break;
                    case TokenType.AND_EQ: (result, error) = currentValue.AndedBy(valueToAssign); break;
                    case TokenType.OR_EQ: (result, error) = currentValue.OredBy(valueToAssign); break;
                    case TokenType.NULL_COALESCE_EQ:
                        if (currentValue.Type == RuntimeValueType.Null) (result, error) = (valueToAssign.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                        else (result, error) = (currentValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                        break;
                }

                if (error != null) return res.Failure(error);
                finalValue = result!;
            }

            var (result1, error1) = targetList.ListSet(indexValue, finalValue);
            if (error1 != null) return res.Failure(error1);
            return res.Success(finalValue.SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}
using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class BinaryOperationNodeVisitor : NodeVisitor<BinaryOperationNode>
    {
        protected sealed override RuntimeResult VisitNode(BinaryOperationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var left = res.Register(interpreter.Visit(node.LeftNode, context));
            if (res.ShouldReturn()) return res;

            var right = res.Register(interpreter.Visit(node.RightNode, context));
            if (res.ShouldReturn()) return res;

            (RuntimeValue? result, Error? error) = (null, null);

            switch (node.OpTok.Type)
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
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
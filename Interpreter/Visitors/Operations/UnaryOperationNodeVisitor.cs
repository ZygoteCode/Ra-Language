using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class UnaryOperationNodeVisitor : NodeVisitor<UnaryOperationNode>
    {
        protected sealed override RuntimeResult VisitNode(UnaryOperationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var value = res.Register(interpreter.Visit(node.Node, context));
            if (res.ShouldReturn()) return res;

            Error? error = null;

            switch (node.OpTok.Type)
            {
                case TokenType.DOUBLE_PLUS:
                case TokenType.DOUBLE_MINUS:
                    if (node.Node.NodeType != AstNodeType.VariableAccess) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to variables", context));
                    if (value.Type != RuntimeValueType.Number) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to numbers", context));

                    VariableAccessNode varAccessNode = (VariableAccessNode)node.Node;
                    NumberValue number = (NumberValue)value;

                    RuntimeValue? newValue = null;
                    if (node.OpTok.Type == TokenType.DOUBLE_PLUS) (newValue, error) = number.AddedTo(NumberValue.One);
                    else (newValue, error) = number.SubbedBy(NumberValue.One);

                    if (error != null) return res.Failure(error);
                    newValue = newValue!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    var varName = varAccessNode.VarNameTok.Value?.ToString() ?? throw new InvalidOperationException("Variable name missing");
                    context.SymbolTable.Set(varName, newValue);

                    if (node.IsLeft)
                    {
                        return res.Success(newValue);
                    }
                    else
                    {
                        var oldCopy = number.Copy().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                        return res.Success(oldCopy);
                    }
                case TokenType.MINUS:
                    (value, error) = value.MultedBy(new NumberValue(BigNumber.Parse("-1")));
                    break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.Not:
                    if (node.IsLeft) (value, error) = value.Notted();
                    else (value, error) = value.Factorial();
                    break;
                case TokenType.BITWISE_NOT:
                    (value, error) = value.BitwiseNotted();
                    break;
            }

            if (error != null) return res.Failure(error);
            return res.Success(value!.SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
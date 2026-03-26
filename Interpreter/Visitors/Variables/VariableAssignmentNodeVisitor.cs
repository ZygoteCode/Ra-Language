using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class VariableAssignmentNodeVisitor : NodeVisitor<VariableAssignmentNode>
    {
        protected sealed override RuntimeResult VisitNode(VariableAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var varName = node.VarNameTok.Value?.ToString();

            if (string.IsNullOrEmpty(varName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid assignment target", context));

            var currentValue = context.SymbolTable.Get(varName);

            if (currentValue == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

            if (currentValue.VariableDeclarationType == VariableDeclarationType.CONST)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a constant variable and cannot be modified at runtime", context));
            else if (currentValue.VariableDeclarationType == VariableDeclarationType.FINAL && currentValue.Type != RuntimeValueType.Null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a final variable and cannot be modified at runtime", context));

            var operation = node.AssignmentToken;

            RuntimeValue value;
            if (node.ValueNode.NodeType == AstNodeType.VariableAccess)
            {
                VariableAccessNode rhsVarAccess = (VariableAccessNode)node.ValueNode;
                string srcName = rhsVarAccess.VarNameTok.Value?.ToString() ?? "";
                var (extracted, err) = interpreter.ExtractVariableValueByName(srcName, rhsVarAccess.PositionStart, rhsVarAccess.PositionEnd, context);
                if (err != null) return res.Failure(err);
                value = extracted!;
            }
            else
            {
                value = res.Register(interpreter.Visit(node.ValueNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
            }

            (RuntimeValue? result, Error? error) = (null, null);

            switch (operation.Type)
            {
                case TokenType.EQ: (result, error) = (value, null); break;
                case TokenType.PLUS_EQ: (result, error) = currentValue.AddedTo(value); break;
                case TokenType.MINUS_EQ: (result, error) = currentValue.SubbedBy(value); break;
                case TokenType.MUL_EQ: (result, error) = currentValue.MultedBy(value); break;
                case TokenType.DIV_EQ: (result, error) = currentValue.DivedBy(value); break;
                case TokenType.MODULO_EQ: (result, error) = currentValue.ModuledBy(value); break;
                case TokenType.BITWISE_AND_EQ: (result, error) = currentValue.BitwiseAndedBy(value); break;
                case TokenType.BITWISE_OR_EQ: (result, error) = currentValue.BitwiseOredBy(value); break;
                case TokenType.BITWISE_LEFT_SHIFT_EQ: (result, error) = currentValue.BitwiseLeftShiftedBy(value); break;
                case TokenType.BITWISE_RIGHT_SHIFT_EQ: (result, error) = currentValue.BitwiseRightShiftedBy(value); break;
                case TokenType.POW_EQ: (result, error) = currentValue.PowedBy(value); break;
                case TokenType.AND_EQ: (result, error) = currentValue.AndedBy(value); break;
                case TokenType.OR_EQ: (result, error) = currentValue.OredBy(value); break;
                case TokenType.NULL_COALESCE_EQ:
                    if (currentValue.Type == RuntimeValueType.Null) (result, error) = (value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    else (result, error) = (currentValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    break;
            }

            if (error != null) return res.Failure(error);
            var declType = currentValue.VariableDeclarationType;
            var entry = context.SymbolTable.GetEntry(varName);

            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

            if (entry.IsStaticallyTyped && entry.DeclaredType != null)
            {
                if (!TypeSystem.IsAssignable(context, entry.DeclaredType, result!))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot assign value of type '{result.Type.ToString().ToLower()}' to variable '{varName}' of type '{entry.DeclaredType}'", context));
            }

            var declType2 = entry.Value?.VariableDeclarationType ?? VariableDeclarationType.VARIABLE;
            RuntimeValue? newValue = TypeChecker.GetNewType(entry.DeclaredType, result, context, node);

            if (newValue == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
            }

            result = newValue;
            context.SymbolTable.Set(varName, result!.SetDeclarationType(declType2));
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd).SetDeclarationType(declType2));
        }
    }
}
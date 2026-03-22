using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class VariableDeclarationNodeVisitor : NodeVisitor<VariableDeclarationNode>
    {
        protected override RuntimeResult VisitNode(VariableDeclarationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var values = new List<RuntimeValue>();

            foreach ((Token, AstNode?, TypeDescriptor?) declaration in node.Declarations)
            {
                var varName = declaration.Item1.Value?.ToString();

                if (string.IsNullOrEmpty(varName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid identifier", context));

                if (context.SymbolTable.Get(varName) != null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is already defined", context));

                RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                TypeDescriptor? declaredType = declaration.Item3;

                if (declaration.Item2 != null)
                {
                    value = res.Register(interpreter.Visit(declaration.Item2, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) continue;
                }

                if (declaredType != null)
                {
                    if (!TypeSystem.IsAssignable(context, declaredType, value))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot assign value of type '{value.Type}' to '{declaredType}'", context));
                }

                bool isLetFlag = node.DeclarationType == VariableDeclarationType.LET;
                bool isStaticallyTyped = declaredType != null;

                if (node.DeclarationType == VariableDeclarationType.CONST)
                    value.VariableDeclarationType = VariableDeclarationType.CONST;
                else if (node.DeclarationType == VariableDeclarationType.FINAL)
                    value.VariableDeclarationType = VariableDeclarationType.FINAL;
                else if (node.DeclarationType == VariableDeclarationType.LET)
                    value.VariableDeclarationType = VariableDeclarationType.LET;
                else
                    value.VariableDeclarationType = VariableDeclarationType.VARIABLE;

                RuntimeValue? newValue = TypeChecker.GetNewType(declaredType, value, context, node);

                if (newValue == null)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
                }

                value = newValue;
                context.SymbolTable.Set(varName, value, isLetFlag, declaredType, isStaticallyTyped);
                values.Add(value);
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
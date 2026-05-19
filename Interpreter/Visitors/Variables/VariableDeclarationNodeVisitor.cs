using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
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
        protected sealed override RuntimeResult VisitNode(VariableDeclarationNode node, Context context, IInterpreter interpreter)
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

                RuntimeValue value = NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                TypeDescriptor? declaredType = declaration.Item3;

                if (declaration.Item2 != null)
                {
                    context.AreCallsBlocked = node.DeclarationType == VariableDeclarationType.CONST;
                    value = res.Register(interpreter.Visit(declaration.Item2, context))!;
                    context.AreCallsBlocked = false;
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) continue;
                }
                else if (node.DeclarationType == VariableDeclarationType.CONST)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Const variables must be initialized with a value", context));
                }

                if (declaredType != null)
                {
                    if (!TypeSystem.IsAssignable(context, declaredType, value))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot assign value of type '{value.Type}' to '{declaredType}'", context));

                    // Late-bind generic element type onto async values when the
                    // declared type carries one (e.g. var ch: channel<int> = ...;).
                    // Subsequent send/emit/await operations enforce it.
                    if (declaredType.GenericArgs != null && declaredType.GenericArgs.Count > 0)
                    {
                        var inner = declaredType.GenericArgs[0];
                        if (value is RaLanguage.Interpreter.Values.Async.ChannelValue cv && cv.ElementType == null)
                            cv.ElementType = inner;
                        else if (value is RaLanguage.Interpreter.Values.Async.AsyncStreamValue sv && sv.ElementType == null)
                            sv.ElementType = inner;
                        else if (value is RaLanguage.Interpreter.Values.Async.TaskValue tv && tv.ElementType == null)
                            tv.ElementType = inner;
                    }
                }

                bool isLetFlag = node.DeclarationType == VariableDeclarationType.LET;
                bool isStaticallyTyped = declaredType != null;
                var declTypeFlag = node.DeclarationType switch
                {
                    VariableDeclarationType.CONST => VariableDeclarationType.CONST,
                    VariableDeclarationType.FINAL => VariableDeclarationType.FINAL,
                    VariableDeclarationType.LET => VariableDeclarationType.LET,
                    _ => VariableDeclarationType.VARIABLE,
                };

                value.VariableDeclarationType = declTypeFlag;

                RuntimeValue? newValue = TypeChecker.GetNewType(declaredType, value, context, node);

                if (newValue == null)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
                }

                value = newValue;
                value.VariableDeclarationType = declTypeFlag;

                context.SymbolTable.SetWithDeclarationType(varName, value, isLetFlag, declaredType, isStaticallyTyped, node.IsPublic, declTypeFlag);

                if (node.HasAnnotations)
                {
                    var target = new MetadataTarget(AnnotationTargetKind.Variable, null, varName);
                    var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                    if (annErr != null) return res.Failure(annErr);

                    var (coerced, cverr) = AnnotationValidator.CoerceAndValidate(target.Key, value, $"variable '{varName}'", context);
                    if (cverr != null) return res.Failure(cverr);
                    if (!ReferenceEquals(coerced, value))
                    {
                        value = coerced;
                        context.SymbolTable.SetWithDeclarationType(varName, value, isLetFlag, declaredType, isStaticallyTyped, node.IsPublic, declTypeFlag);
                    }
                }

                values.Add(value);
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
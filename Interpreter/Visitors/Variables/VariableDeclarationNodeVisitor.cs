using RaLanguage.Errors;
using System.Threading.Tasks;
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
        protected sealed override async ValueTask<RuntimeResult> VisitNode(VariableDeclarationNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(VariableDeclarationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var values = new List<RuntimeValue>();

            foreach ((Token, AstNode?, TypeDescriptor?) declaration in node.Declarations)
            {
                var varName = declaration.Item1.Value?.ToString();

                if (string.IsNullOrEmpty(varName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid identifier", context));

                if (RaLanguage.Interpreter.Runtime.DeclarationHelper.IsRealRedeclaration(context.SymbolTable.GetLocalEntry(varName)))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is already defined", context));

                RuntimeValue value = NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                TypeDescriptor? declaredType = declaration.Item3;

                if (declaration.Item2 != null)
                {
                    // `const` and `let const` are compile-time-stable, so the initialiser
                    // must not have side effects via function calls. The existing
                    // AreCallsBlocked flag is reused for `let const` for symmetry.
                    context.AreCallsBlocked = node.DeclarationType == VariableDeclarationType.CONST
                                           || node.DeclarationType == VariableDeclarationType.LET_CONST;
                    value = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(declaration.Item2, context, interpreter))!;
                    context.AreCallsBlocked = false;
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) continue;
                }
                else if (node.DeclarationType == VariableDeclarationType.CONST)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Const variables must be initialized with a value", context));
                }
                else if (node.DeclarationType == VariableDeclarationType.LET_CONST)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"'let const {varName}' must be initialised at declaration",
                        context,
                        code: DiagnosticCode.RuntimeImmutableBinding,
                        primaryLabel: "missing initialiser",
                        help: "'let const' bindings are compile-time-stable and require an initial value"));
                }

                if (declaredType != null)
                {
                    if (!TypeSystem.IsAssignable(context, declaredType, value))
                    {
                        if (TypeSystem.TryDescribeFunctionMismatch(context, declaredType, value, out var fm, out var fh))
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, fm, context,
                                code: DiagnosticCode.RuntimeTypeMismatch, primaryLabel: "callable signature mismatch", help: fh));
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot assign value of type '{value.Type}' to '{declaredType}'", context));
                    }

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

                // The borrow / move machinery keys off IsLet, which now covers any of
                // the let-family declarations. The DeclarationType then narrows to the
                // exact flavour (LET / LET_MUT / LET_CONST) so the assignment / borrow
                // visitors can apply the right policy.
                bool isLetFlag = node.DeclarationType == VariableDeclarationType.LET
                              || node.DeclarationType == VariableDeclarationType.LET_MUT
                              || node.DeclarationType == VariableDeclarationType.LET_CONST;
                bool isStaticallyTyped = declaredType != null;
                var declTypeFlag = node.DeclarationType switch
                {
                    VariableDeclarationType.CONST => VariableDeclarationType.CONST,
                    VariableDeclarationType.FINAL => VariableDeclarationType.FINAL,
                    VariableDeclarationType.LET => VariableDeclarationType.LET,
                    VariableDeclarationType.LET_MUT => VariableDeclarationType.LET_MUT,
                    VariableDeclarationType.LET_CONST => VariableDeclarationType.LET_CONST,
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

                context.SymbolTable.SetLocalWithDeclarationType(varName, value, isLetFlag, declaredType, isStaticallyTyped, node.IsPublic, declTypeFlag);

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
                        context.SymbolTable.SetLocalWithDeclarationType(varName, value, isLetFlag, declaredType, isStaticallyTyped, node.IsPublic, declTypeFlag);
                    }
                }

                values.Add(value);
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
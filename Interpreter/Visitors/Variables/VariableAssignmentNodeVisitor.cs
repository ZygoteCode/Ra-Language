using RaLanguage.Errors;
using System.Threading.Tasks;
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
    public class VariableAssignmentNodeVisitor : NodeVisitor<VariableAssignmentNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(VariableAssignmentNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(VariableAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var varName = node.Name;

            if (string.IsNullOrEmpty(varName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "invalid assignment target",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "the left-hand side has no resolvable name",
                    help: "assignments target variables ('x = ...'), members ('obj.f = ...') or indexes ('a[i] = ...')"));

            var ct = context.SymbolTable;
            SymbolEntry? entry;
            var cache = node.LookupCache;
            if (cache != null && ReferenceEquals(cache.Table, ct) && cache.Generation == ct.LocalGeneration)
            {
                entry = cache.Entry;
            }
            else
            {
                entry = ct.GetLocalEntry(varName);
                if (entry != null)
                {
                    node.LookupCache = new SymbolLookupCache(ct, ct.LocalGeneration, entry);
                }
                else
                {
                    var p = ct.Parent;
                    while (p != null)
                    {
                        var e = p.GetLocalEntry(varName);
                        if (e != null) { entry = e; break; }
                        p = p.Parent;
                    }
                }
            }
            var currentValue = entry?.Value;

            if (currentValue == null || entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{varName}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such variable in scope",
                    help: $"declare '{varName}' with 'var', 'let', 'const' or 'final' before assigning to it"));

            if (entry.DeclarationType == VariableDeclarationType.CONST)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is declared 'const'",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is immutable",
                    help: "use 'var' or 'let mut' if you need a mutable binding"));
            else if (entry.DeclarationType == VariableDeclarationType.LET_CONST)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is declared 'let const'",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is a compile-time-stable constant",
                    help: "use 'let mut' if you need a mutable binding"));
            else if (entry.DeclarationType == VariableDeclarationType.LET)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is an immutable 'let' binding",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding cannot be reassigned",
                    help: "declare it as 'let mut' if you need to mutate it, or shadow with a new 'let' in a nested scope"));
            else if (entry.DeclarationType == VariableDeclarationType.FINAL && currentValue.Type != RuntimeValueType.Null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot reassign '{varName}': 'final' bindings may only be initialized once",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is already initialized",
                    help: "use 'var' for a fully mutable binding, or initialize the 'final' binding at declaration"));

            // Borrow safety: assigning to a binding that is currently borrowed would
            // change the value out from under existing &/&mut aliases. Block unless we
            // are rebinding a borrow-holding entry to a new borrow (rebind path �
            // released and reissued below).
            bool rebindingBorrow = currentValue is RaLanguage.Interpreter.Values.Primitives.BorrowValue;
            if (entry.IsBorrowed && !rebindingBorrow)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is currently borrowed",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: entry.HasMutableBorrow
                        ? "binding is exclusively borrowed (&mut)"
                        : $"binding has {entry.SharedBorrowCount} shared borrow(s) alive",
                    help: "wait for the borrow's scope to end, or write through the borrow with '*ref ='"));

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
                value = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.ValueNode, context, interpreter));
                if (res.ShouldReturn()) return res;
            }

            (RuntimeValue? result, Error? error) = (null, null);

            // Borrow rebind: `let mut r = &x; r = &y;` replaces the borrow itself.
            // Skip the read-through path (which would otherwise produce "5 + &y").
            bool isBorrowRebind = currentValue is BorrowValue
                                  && operation.Type == TokenType.EQ
                                  && value is BorrowValue;

            RuntimeValue operationTarget = currentValue;
            if (!isBorrowRebind && currentValue is IReferenceValue refRead)
            {
                operationTarget = refRead.Value;
            }

            switch (operation.Type)
            {
                case TokenType.EQ: (result, error) = (value, null); break;
                case TokenType.PLUS_EQ: (result, error) = operationTarget.AddedTo(value); break;
                case TokenType.MINUS_EQ: (result, error) = operationTarget.SubbedBy(value); break;
                case TokenType.MUL_EQ: (result, error) = operationTarget.MultedBy(value); break;
                case TokenType.DIV_EQ: (result, error) = operationTarget.DivedBy(value); break;
                case TokenType.MODULO_EQ: (result, error) = operationTarget.ModuledBy(value); break;
                case TokenType.BITWISE_AND_EQ: (result, error) = operationTarget.BitwiseAndedBy(value); break;
                case TokenType.BITWISE_OR_EQ: (result, error) = operationTarget.BitwiseOredBy(value); break;
                case TokenType.BITWISE_LEFT_SHIFT_EQ: (result, error) = operationTarget.BitwiseLeftShiftedBy(value); break;
                case TokenType.BITWISE_RIGHT_SHIFT_EQ: (result, error) = operationTarget.BitwiseRightShiftedBy(value); break;
                case TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ: (result, error) = operationTarget.BitwiseLeftShiftedBy(value); break;
                case TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ: (result, error) = operationTarget.BitwiseUnsignedRightShiftedBy(value); break;
                case TokenType.BITWISE_ROTATE_LEFT_EQ: (result, error) = operationTarget.BitwiseRotateLeftedBy(value); break;
                case TokenType.BITWISE_ROTATE_RIGHT_EQ: (result, error) = operationTarget.BitwiseRotateRightedBy(value); break;
                case TokenType.POW_EQ: (result, error) = operationTarget.PowedBy(value); break;
                case TokenType.AND_EQ: (result, error) = operationTarget.AndedBy(value); break;
                case TokenType.OR_EQ: (result, error) = operationTarget.OredBy(value); break;
                case TokenType.NULL_COALESCE_EQ:
                    if (operationTarget.Type == RuntimeValueType.Null) (result, error) = (value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    else (result, error) = (operationTarget.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    break;
            }

            if (error != null) return res.Failure(error);

            // Borrow rebind: release the old borrow then fall through to the regular
            // TryAssign path so the SymbolEntry now holds the new BorrowValue.
            if (isBorrowRebind)
            {
                ((BorrowValue)currentValue).Release();
            }
            else if (currentValue is IReferenceValue refWrite)
            {
                try
                {
                    RuntimeValue? newValue = TypeChecker.GetNewType(entry.DeclaredType, result, context, node);

                    if (newValue == null)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
                    }

                    refWrite.Value = newValue;
                    return res.Success(newValue.SetPos(node.PositionStart, node.PositionEnd));
                }
                catch (Exception ex)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Failed to assign through reference: {ex.Message}", context));
                }
            }
            
            var entry2 = context.SymbolTable.GetEntry(varName);

            if (entry2 == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

            if (entry2.IsStaticallyTyped && entry2.DeclaredType != null)
            {
                if (!TypeSystem.IsAssignable(context, entry2.DeclaredType, result!))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"type mismatch: cannot assign value of type '{result.Type.ToString().ToLower()}' to '{varName}' (declared '{entry2.DeclaredType}')",
                        context,
                        code: DiagnosticCode.RuntimeTypeMismatch,
                        primaryLabel: $"value of type '{result.Type.ToString().ToLower()}' assigned here",
                        help: $"either cast the value with 'as {entry2.DeclaredType}' or declare '{varName}' with a compatible type"));
            }

            var declType2 = entry2.DeclarationType;
            RuntimeValue? newValue2 = TypeChecker.GetNewType(entry2.DeclaredType, result, context, node);

            if (newValue2 == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
            }

            result = newValue2;
            result.VariableDeclarationType = declType2;

            // Walk-up assignment that mutates only the Value of the resolved entry,
            // preserving IsLet / IsPublic / DeclaredType / DeclarationType / IsStaticallyTyped.
            // Errors if the binding has vanished between resolution and write (cannot happen
            // in single-threaded execution, but defensive).
            if (!context.SymbolTable.TryAssign(varName, result!))
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{varName}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such variable in scope",
                    help: $"declare '{varName}' with 'var', 'let', 'const' or 'final' before assigning to it"));
            }
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
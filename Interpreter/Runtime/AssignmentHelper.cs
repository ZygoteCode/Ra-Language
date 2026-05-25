using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared post-RHS assignment logic. Mirrors the post-RHS half of
    // VariableAssignmentNodeVisitor (steps 4-13 of the visitor pipeline) so
    // the AST visitor and the VM's OP_STORE_GLOBAL opcode behave
    // identically.
    //
    // Steps performed:
    //   4. Resolve borrow-rebind flag.
    //   5. Resolve operationTarget (deref through IReferenceValue).
    //   6. Apply compound operation (PLUS_EQ, MINUS_EQ, ...).
    //   7. Borrow rebind release.
    //   8. IReferenceValue through-write.
    //   9. Re-lookup entry (defensive — guards against intervening mutations).
    //   10. Type check against entry.DeclaredType.
    //   11. TypeChecker.GetNewType coerce.
    //   12. Mark VariableDeclarationType on the new value.
    //   13. TryAssign into the symbol table.
    //
    // Pre-conditions (must hold; callers are responsible):
    //   - `entry` is the symbol entry returned by ctx.SymbolTable.GetEntry(name).
    //   - `currentValue` is entry.Value at lookup time (captured before the
    //     RHS evaluated so borrow-rebind detection is consistent).
    //   - `value` is the already-evaluated right-hand side.
    //   - Pre-RHS checks (const / let / let-const / final-initialized /
    //     IsBorrowed) have already been performed by the caller.
    public static class AssignmentHelper
    {
        public static RuntimeResult ApplyPrechecked(
            VariableAssignmentNode node,
            Context context,
            SymbolEntry entry,
            RuntimeValue currentValue,
            RuntimeValue value)
        {
            var res = new RuntimeResult();
            var operation = node.AssignmentToken;
            var varName = node.Name;

            (RuntimeValue? result, Error? error) = (null, null);

            // Borrow rebind: `let mut r = &x; r = &y;` replaces the borrow itself.
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
                case TokenType.POW_EQ: (result, error) = operationTarget.PowedBy(value); break;
                case TokenType.AND_EQ: (result, error) = operationTarget.AndedBy(value); break;
                case TokenType.OR_EQ: (result, error) = operationTarget.OredBy(value); break;
                case TokenType.NULL_COALESCE_EQ:
                    if (operationTarget.Type == RuntimeValueType.Null)
                        (result, error) = (value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    else
                        (result, error) = (operationTarget.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    break;
            }

            if (error != null) return res.Failure(error);

            if (isBorrowRebind)
            {
                ((BorrowValue)currentValue).Release();
            }
            else if (currentValue is IReferenceValue refWrite)
            {
                try
                {
                    RuntimeValue? newValue = TypeChecker.GetNewType(entry.DeclaredType, result!, context, node);
                    if (newValue == null)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));
                    refWrite.Value = newValue;
                    return res.Success(newValue.SetPos(node.PositionStart, node.PositionEnd));
                }
                catch (System.Exception ex)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Failed to assign through reference: {ex.Message}", context));
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
            RuntimeValue? newValue2 = TypeChecker.GetNewType(entry2.DeclaredType, result!, context, node);
            if (newValue2 == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));

            result = newValue2;
            result.VariableDeclarationType = declType2;

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

        // Pre-RHS gating checks shared by both the AST visitor and the VM's
        // OP_STORE_GLOBAL opcode. Returns a non-null Error if the assignment
        // is forbidden before the RHS is even evaluated; the caller surfaces
        // it through RuntimeResult.Failure.
        //
        // The `valueIsBorrow` parameter lets the caller communicate whether
        // the not-yet-bound RHS will end up being a BorrowValue (only the
        // borrow-rebind path tolerates assigning to a currently-borrowed
        // binding). When unknown (e.g. the AST visitor before RHS eval), the
        // visitor uses a slightly different sequencing — that's why this
        // helper is the gate, not the full pre-check.
        public static Error? PreCheck(VariableAssignmentNode node, SymbolEntry? entry, Context context)
        {
            var varName = node.Name;
            if (string.IsNullOrEmpty(varName))
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    "invalid assignment target",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "the left-hand side has no resolvable name",
                    help: "assignments target variables ('x = ...'), members ('obj.f = ...') or indexes ('a[i] = ...')");

            if (entry == null || entry.Value == null)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{varName}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such variable in scope",
                    help: $"declare '{varName}' with 'var', 'let', 'const' or 'final' before assigning to it");

            if (entry.DeclarationType == VariableDeclarationType.CONST)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is declared 'const'",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is immutable",
                    help: "use 'var' or 'let mut' if you need a mutable binding");
            if (entry.DeclarationType == VariableDeclarationType.LET_CONST)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is declared 'let const'",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is a compile-time-stable constant",
                    help: "use 'let mut' if you need a mutable binding");
            if (entry.DeclarationType == VariableDeclarationType.LET)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{varName}': it is an immutable 'let' binding",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding cannot be reassigned",
                    help: "declare it as 'let mut' if you need to mutate it, or shadow with a new 'let' in a nested scope");
            if (entry.DeclarationType == VariableDeclarationType.FINAL && entry.Value.Type != RuntimeValueType.Null)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot reassign '{varName}': 'final' bindings may only be initialized once",
                    context,
                    code: DiagnosticCode.RuntimeImmutableBinding,
                    primaryLabel: "this binding is already initialized",
                    help: "use 'var' for a fully mutable binding, or initialize the 'final' binding at declaration");

            return null;
        }

        // Borrow gating — must run AFTER the RHS is known so the borrow
        // rebind case can be detected and allowed (a borrow being reassigned
        // to a new borrow is the one legal path while the binding holds an
        // alive BorrowValue).
        public static Error? CheckBorrowGuard(VariableAssignmentNode node, SymbolEntry entry, RuntimeValue newValue, Context context)
        {
            bool rebindingBorrow = entry.Value is BorrowValue;
            if (entry.IsBorrowed && !rebindingBorrow)
                return new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot assign to '{node.Name}': it is currently borrowed",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: entry.HasMutableBorrow
                        ? "binding is exclusively borrowed (&mut)"
                        : $"binding has {entry.SharedBorrowCount} shared borrow(s) alive",
                    help: "wait for the borrow's scope to end, or write through the borrow with '*ref ='");
            return null;
        }
    }
}

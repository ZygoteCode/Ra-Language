using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared single-declaration logic. Mirrors VariableDeclarationNodeVisitor
    // for the case of a node with exactly one entry in Declarations and no
    // annotations. The VM's OP_DECLARE_LOCAL opcode calls this directly so
    // its semantics line up with the AST path verbatim.
    //
    // Steps:
    //   1. Redeclaration check (GetLocalEntry).
    //   2. Declared-type check via TypeChecker.GetNewType.
    //   3. Late-bind generic element type for channel<T> / stream<T> / task<T>.
    //   4. Set VariableDeclarationType marker on the value.
    //   5. SetLocalWithDeclarationType in the current scope.
    public static class DeclarationHelper
    {
        // Apply a single declaration with the initializer value already
        // computed. Used by the VM after evaluating the init expression
        // natively. `declIndex` selects which entry in node.Declarations to
        // bind (always 0 for the VM in M3 — multi-declarations fall back to
        // OP_VISIT_AST in the IR compiler).
        public static RuntimeResult ApplySingle(
            VariableDeclarationNode node,
            Context context,
            int declIndex,
            RuntimeValue value)
        {
            var res = new RuntimeResult();
            var declaration = node.Declarations[declIndex];
            var varName = declaration.Item1.Value?.ToString();
            if (string.IsNullOrEmpty(varName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "Invalid identifier", context));

            if (IsRealRedeclaration(context.SymbolTable!.GetLocalEntry(varName)))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{varName}' is already defined", context));

            TypeDescriptor? declaredType = declaration.Item3;

            if (declaredType != null)
            {
                if (!TypeSystem.IsAssignable(context, declaredType, value))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Type mismatch: cannot assign value of type '{value.Type}' to '{declaredType}'", context));

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

            bool isLetFlag = node.DeclarationType == VariableDeclarationType.LET
                          || node.DeclarationType == VariableDeclarationType.LET_MUT
                          || node.DeclarationType == VariableDeclarationType.LET_CONST;
            bool isStaticallyTyped = declaredType != null;
            var declTypeFlag = node.DeclarationType;

            value.VariableDeclarationType = declTypeFlag;

            RuntimeValue? newValue = TypeChecker.GetNewType(declaredType, value, context, node);
            if (newValue == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Failed to parse value", context));

            value = newValue;
            value.VariableDeclarationType = declTypeFlag;

            context.SymbolTable.SetLocalWithDeclarationType(
                varName, value, isLetFlag, declaredType, isStaticallyTyped, node.IsPublic, declTypeFlag);

            return res.Success(value);
        }

        // A local `var`/`let`/`const` declaration may SHADOW an imported
        // built-in function: before the std-library refactor those names lived
        // in a parent scope and were silently shadowable, so `var e` / `let
        // sign` / `var pi` (all math built-ins) must keep working after an
        // explicit `import std.prelude.*` places them in the local scope.
        // A real user binding (anything that is not a BuiltInFunctionValue)
        // still cannot be redeclared.
        public static bool IsRealRedeclaration(SymbolEntry? existing)
            => existing != null
               && existing.Value is not RaLanguage.Interpreter.Values.Functions.BuiltInFunctionValue;

        // Compile-time eligibility check. Returns true when the IR compiler
        // is allowed to emit OP_DECLARE_LOCAL for this node. Annotations,
        // multi-declarations, static fields, public fields, and missing
        // initializers all push the node back to OP_VISIT_AST in M3.
        public static bool IsNativelyCompilable(VariableDeclarationNode node)
        {
            if (node.HasAnnotations) return false;
            if (node.IsStatic) return false;
            if (node.Declarations.Count != 1) return false;
            var d = node.Declarations[0];
            if (d.Item2 == null) return false; // no initializer — defer the diagnostic to the AST
            return true;
        }
    }
}

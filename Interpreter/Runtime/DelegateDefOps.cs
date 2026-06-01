using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared `delegate Name = fn(...) -> R` registration, factored so the
    // IR-lowered OP_DEFINE_TYPE handler and the visitor fallback install a
    // byte-identical DelegateTypeValue. Delegates have no value expressions /
    // bodies, so (unlike enums) there is no side-effect ordering to preserve —
    // the collision check lives here for both callers.
    public static class DelegateDefOps
    {
        public static ValueResult Register(
            string name,
            TypeDescriptor signature,
            List<string> generics,
            List<WhereConstraintNode> constraints,
            bool isPublic,
            Context ctx,
            Position posStart,
            Position posEnd)
        {
            // A delegate alias may shadow another delegate, but not any other
            // symbol kind — mirrors the visitor exactly.
            var existing = ctx.SymbolTable.Get(name);
            if (existing != null && !(existing is DelegateTypeValue))
            {
                return (null, new RuntimeError(posStart, posEnd,
                    $"name '{name}' is already declared in this scope",
                    ctx,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "duplicate declaration",
                    help: "delegate aliases share the type namespace with classes / structs / enums"));
            }

            var value = new DelegateTypeValue(name, signature, generics, constraints, isPublic)
                .SetContext(ctx)
                .SetPos(posStart, posEnd);

            ctx.SymbolTable.Set(
                name, value,
                isLet: true,
                declaredType: new TypeDescriptor("type"),
                isStaticallyTyped: true,
                isPublic: isPublic);

            return (value, null);
        }
    }
}

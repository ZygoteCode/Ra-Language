using System;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Namespaces;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared `using a.b.c [as alias]` resolution, factored so the IR-lowered
    // OP_DEFINE_TYPE handler and the visitor fallback run identical namespace
    // resolve + member-injection. The caller has already extracted the path
    // segments (the visitor keeps its per-segment empty check, which carries a
    // per-segment position; the lowered path validated segments at compile time).
    public static class UsingNamespaceOps
    {
        public static ValueResult Apply(string[] segments, string? alias, Context ctx, Position posStart, Position posEnd)
        {
            var target = NamespaceRegistry.Global.Resolve(segments);
            if (target == null)
            {
                return (null, new RuntimeError(posStart, posEnd,
                    $"Namespace '{string.Join(".", segments)}' is not defined", ctx));
            }

            if (ctx.SymbolTable == null)
                return (NullValue.Null.SetContext(ctx).SetPos(posStart, posEnd), null);

            if (!string.IsNullOrEmpty(alias))
            {
                ctx.SymbolTable.Set(alias!, target, isPublic: true);
            }
            else
            {
                foreach (var kvp in target.Members.EnumerateLocal())
                {
                    if (!kvp.Value.IsPublic) continue;

                    var existing = ctx.SymbolTable.GetEntry(kvp.Key);
                    if (existing != null && !ReferenceEquals(existing.Value, kvp.Value.Value))
                        continue;

                    ctx.SymbolTable.Set(
                        kvp.Key,
                        kvp.Value.Value,
                        isLet: kvp.Value.IsLet,
                        declaredType: kvp.Value.DeclaredType,
                        isStaticallyTyped: kvp.Value.IsStaticallyTyped,
                        isPublic: true);
                }
            }

            return (NullValue.Null.SetContext(ctx).SetPos(posStart, posEnd), null);
        }
    }
}

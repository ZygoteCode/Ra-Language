using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class VariableAccessNodeVisitor : NodeVisitor<VariableAccessNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(VariableAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var name = node.Name;

            if (string.IsNullOrEmpty(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid variable name", context));

            // Inline cache: pointer-compare on Table and int-compare on Generation
            // collapse this access to ~4 instructions when the cache hits. Generation
            // is bumped only on add/remove, so a tight loop reassigning the same
            // local (`x = x + 1`) keeps the cache valid through every iteration.
            var ct = context.SymbolTable;
            SymbolEntry? entry;
            var cache = node.LookupCache;
            if (cache != null && ReferenceEquals(cache.Table, ct) && cache.Generation == ct.LocalGeneration)
            {
                entry = cache.Entry;
            }
            else
            {
                // Local-scope hit is cacheable; parent-walk hit is not (see
                // SymbolLookupCache comment).
                entry = ct.GetLocalEntry(name);
                if (entry != null)
                {
                    node.LookupCache = new SymbolLookupCache(ct, ct.LocalGeneration, entry);
                }
                else
                {
                    var p = ct.Parent;
                    while (p != null)
                    {
                        var e = p.GetLocalEntry(name);
                        if (e != null) { entry = e; break; }
                        p = p.Parent;
                    }
                }
            }

            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{name}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such symbol in scope",
                    help: $"declare '{name}' with 'var', 'let', 'const' or 'final' before using it, or check the spelling"));

            if (entry.IsMoved)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"value of '{name}' was already moved",
                    context,
                    code: DiagnosticCode.RuntimeMovedValue,
                    primaryLabel: "used here after move",
                    help: "non-copy 'let' bindings transfer ownership on use; rebind the value or take a copy"));

            // While a mutable borrow is alive the underlying binding is exclusively
            // owned by that borrow — direct reads of the binding would expose the same
            // storage through two paths simultaneously, which is exactly what `&mut`
            // forbids. Reads through the borrow itself (`*r`) are allowed by the
            // dereference visitor.
            if (entry.HasMutableBorrow)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot read '{name}': it is exclusively borrowed by a '&mut'",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: "binding is exclusively borrowed",
                    help: "access the value through the existing '&mut' borrow with '*ref', or wait until the borrow's scope ends"));

            var value = entry.Value;

            if (value.Type == RuntimeValueType.StructInstance ||
                value.Type == RuntimeValueType.ClassInstance ||
                value.Type == RuntimeValueType.Enum ||
                value.Type == RuntimeValueType.EnumType)
            {
                return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            var valueToReturn = entry.Value.Aliased();
            return res.Success(valueToReturn.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
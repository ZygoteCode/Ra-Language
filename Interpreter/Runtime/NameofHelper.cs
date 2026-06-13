using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of NameofNodeVisitor — `nameof x` / `nameof(a.b.c)`. The
    // resolved name is computed at parse time (NameofNode.Name; the final
    // segment of a member chain), so this is a constant — the VM path folds it
    // to a LoadConst (see IrCompiler), and this AST-fallback path returns the
    // same constant string with no symbol lookup.
    public static class NameofHelper
    {
        public static RuntimeResult Apply(NameofNode node, Context context)
        {
            var res = new RuntimeResult();
            return res.Success(new StringValue(node.Name).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}

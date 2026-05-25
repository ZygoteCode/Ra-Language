using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of NameofNodeVisitor — `nameof x`. Looks up `x` to
    // verify existence, then returns the *name* as a StringValue.
    public static class NameofHelper
    {
        public static RuntimeResult Apply(NameofNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value?.ToString() ?? "";
            var value = context.SymbolTable!.Get(varName);
            if (value == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"Variable {varName} not defined", context));
            return res.Success(new StringValue(varName).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}

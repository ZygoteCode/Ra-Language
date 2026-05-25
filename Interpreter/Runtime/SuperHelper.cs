using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Classes;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of SuperNodeVisitor — bare `super` expression. Produces
    // a SuperProxyValue scoped to the lexically-owning class so super-chain
    // resolution terminates correctly (see SuperNodeVisitor for the
    // dynamic-vs-lexical rationale).
    public static class SuperHelper
    {
        public static RuntimeResult Apply(SuperNode node, Context context)
        {
            var res = new RuntimeResult();
            var selfEntry = context.SymbolTable!.GetEntry("self");
            if (selfEntry == null || selfEntry.Value.Type != RuntimeValueType.ClassInstance)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "'super' is only available inside class instance methods/constructors", context));

            var self = (ClassInstanceValue)selfEntry.Value;
            var owner = context.CurrentClassMethodOwner ?? self.Definition;
            return res.Success(new SuperProxyValue(self, owner).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

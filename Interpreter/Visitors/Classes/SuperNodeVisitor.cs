using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Classes;

namespace RaLanguage.Interpreter.Visitors.Classes
{
    public class SuperNodeVisitor : NodeVisitor<SuperNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(SuperNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var selfEntry = context.SymbolTable.GetEntry("self");
            if (selfEntry == null || selfEntry.Value.Type != RuntimeValueType.ClassInstance)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "'super' is only available inside class instance methods/constructors", context));

            var self = (ClassInstanceValue)selfEntry.Value;

            // Use the lexically-owning class (the class in whose body we are executing) instead
            // of the dynamic type of `self`. This is what makes super-chains terminate: inside
            // B's ctor body invoked on a C instance, `super` must walk B.BaseClass, not
            // C.BaseClass — otherwise super(...) loops back into B forever.
            var owner = context.CurrentClassMethodOwner ?? self.Definition;
            return res.Success(new SuperProxyValue(self, owner).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
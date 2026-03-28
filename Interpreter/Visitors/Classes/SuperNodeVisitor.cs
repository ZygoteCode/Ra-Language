using RaLanguage.Errors.Types;
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
        protected override RuntimeResult VisitNode(SuperNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var selfEntry = context.SymbolTable.GetEntry("self");
            if (selfEntry == null || selfEntry.Value.Type != RuntimeValueType.ClassInstance)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "'super' is only available inside class instance methods/constructors", context));

            var self = (ClassInstanceValue)selfEntry.Value;
            return res.Success(new SuperProxyValue(self, self.Definition).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionDefinitionNodeVisitor : NodeVisitor<FunctionDefinitionNode>
    {
        protected override RuntimeResult VisitNode(FunctionDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNameToks.Select(t => t.Value.ToString()).ToList();
            var funcValue = new FunctionValue(
                funcName,
                node.BodyNode,
                argNames,
                node.ArgTypes,
                node.ParamDefaults,
                node.HasVarArgs,
                node.VarArgNameTok,
                node.VarArgType,
                node.ReturnType,
                node.ShouldAutoReturn
            )
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            if (node.VarNameTok != null) context.SymbolTable.Set(funcName, funcValue, isPublic: node.IsPublic);
            return res.Success(funcValue);
        }
    }
}
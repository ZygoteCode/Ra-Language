using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class ReturnNodeVisitor : NodeVisitor<ReturnNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ReturnNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            RuntimeValue value = NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            if (node.NodeToReturn != null)
            {
                if (node.NodeToReturn.NodeType == AstNodeType.VariableAccess)
                {
                    VariableAccessNode varAccess = (VariableAccessNode)node.NodeToReturn;
                    string srcName = varAccess.VarNameTok.Value?.ToString() ?? "";
                    var (extracted, err) = interpreter.ExtractVariableValueByName(srcName, varAccess.PositionStart, varAccess.PositionEnd, context);
                    if (err != null) return res.Failure(err);
                    value = extracted!;
                }
                else
                {
                    value = res.Register(await interpreter.Visit(node.NodeToReturn, context));
                    if (res.ShouldReturn()) return res;
                }
            }

            return res.SuccessReturn(value);
        }
    }
}
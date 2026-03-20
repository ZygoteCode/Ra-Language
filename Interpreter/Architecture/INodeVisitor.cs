using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    public interface INodeVisitor
    {
        RuntimeResult Visit(AstNode node, Context context, IInterpreter interpreter);
    }
}
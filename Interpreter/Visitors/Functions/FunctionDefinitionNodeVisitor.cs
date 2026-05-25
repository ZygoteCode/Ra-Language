using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    // Thin shim: delegates to FunctionDefinitionHelper.Apply so VM and AST
    // dispatch produce identical FunctionValues. The full construction
    // logic (annotations, DLL binding, parameter annotations, capture
    // freezing, IR compile of the body) lives in the helper.
    public class FunctionDefinitionNodeVisitor : NodeVisitor<FunctionDefinitionNode>
    {
        protected sealed override ValueTask<RuntimeResult> VisitNode(FunctionDefinitionNode node, Context context, IInterpreter interpreter)
            => new ValueTask<RuntimeResult>(FunctionDefinitionHelper.Apply(node, context, interpreter));
    }
}

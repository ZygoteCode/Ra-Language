using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    public interface IInterpreter
    {
        List<(string, AstNode)> Labels { get; }
        ValueTask<RuntimeResult> Visit(AstNode node, Context context);
        (RuntimeValue? value, Error? error) ExtractVariableValueByName(string name, Position posStart, Position posEnd, Context context);
    }
}

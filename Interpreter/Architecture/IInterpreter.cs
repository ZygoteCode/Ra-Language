using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    public interface IInterpreter
    {
        bool AreCallsBlocked { get; }
        List<(string, AstNode)> Labels { get; }
        RuntimeResult Visit(AstNode node, Context context);
        (RuntimeValue? value, Error? error) ExtractVariableValueByName(string name, Position posStart, Position posEnd, Context context);
    }
}
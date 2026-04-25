using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public interface ICallableMethodDefinition
    {
        Token? NameTok { get; }
        List<Token> ArgNameToks { get; }
        List<TypeDescriptor?> ArgTypes { get; }
        List<bool> IsRefParams { get; }
        List<AstNode?> ParamDefaults { get; }
        bool HasVarArgs { get; }
        Token? VarArgNameTok { get; }
        TypeDescriptor? VarArgType { get; }
        TypeDescriptor? ReturnType { get; }
        AstNode? BodyNode { get; }

        bool HasBody { get; }
        bool IsAbstract { get; }
        bool IsOverride { get; }
        bool IsConstructor { get; }
        bool ShouldAutoReturn { get; }
    }
}
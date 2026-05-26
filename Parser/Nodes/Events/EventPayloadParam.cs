using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Events
{
    // Single payload parameter on an event declaration.
    // e.g. in `event Click(x: int, y: int)` each of `x: int` and
    // `y: int` becomes one EventPayloadParam.
    public sealed class EventPayloadParam
    {
        public Token NameTok { get; }
        public TypeDescriptor? Type { get; }

        public EventPayloadParam(Token nameTok, TypeDescriptor? type)
        {
            NameTok = nameTok;
            Type = type;
        }
    }
}

using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Enums
{
    // A single variant inside an enum declaration. Carries everything needed
    // to (a) parse classic integer enums (`Red = 5`), and (b) tag-payload
    // ADT enums (`Identifier(string)`, `Pair(int, int)`, `Eof`).
    //
    // PayloadTypes is null when the variant takes no payload at all, which is
    // distinct from PayloadTypes being an empty list (would represent the
    // pathological `Variant()` form — currently rejected by the parser).
    public sealed class EnumVariantSpec
    {
        public Token MemberTok { get; }
        public AstNode? ValueNode { get; }
        public List<TypeDescriptor>? PayloadTypes { get; }

        public EnumVariantSpec(Token memberTok, AstNode? valueNode, List<TypeDescriptor>? payloadTypes)
        {
            MemberTok = memberTok;
            ValueNode = valueNode;
            PayloadTypes = payloadTypes;
        }

        public string Name => MemberTok.Value?.ToString() ?? string.Empty;
        public bool HasPayload => PayloadTypes != null && PayloadTypes.Count > 0;
        public int Arity => PayloadTypes?.Count ?? 0;
    }
}

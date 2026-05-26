using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Events
{
    // A single visibility-modifier accessor inside an event body, e.g.
    // `pub raise;` or `priv subscribe;`. Accessors do NOT carry bodies
    // (events are a pure protocol — subscribe/raise are operations the
    // runtime drives, not user code).
    public sealed class EventAccessorNode : AstNode
    {
        public Token KindTok { get; }
        public EventAccessorKind Kind { get; }
        public EventAccessorVisibility Visibility { get; }

        public EventAccessorNode(
            Token kindTok,
            EventAccessorKind kind,
            EventAccessorVisibility visibility) : base(AstNodeType.EventAccessor)
        {
            KindTok = kindTok;
            Kind = kind;
            Visibility = visibility;
            PositionStart = kindTok.PositionStart;
            PositionEnd = kindTok.PositionEnd;
        }
    }
}

using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Properties
{
    // Single accessor of a property: get/set/init/observe.
    //
    // IsAuto = true means the accessor was written as the bare keyword
    // (e.g. `get;`) and has no user body. The runtime synthesises the
    // canonical behaviour:
    //   - auto get  : read backing slot
    //   - auto set  : write backing slot (after annotation-driven validate)
    //   - auto init : write backing slot, gated on IsInConstructor
    //   - auto observe: meaningless; rejected by the parser
    //
    // BodyNode is the user-written body (an Expression for `=> expr` form
    // or a Scope for `{ stmts }` form). Inside the body, `value` is the
    // implicit setter/init parameter, `field` is the backing-slot binding,
    // and `old` is the pre-update value inside observe blocks.
    public sealed class PropertyAccessorNode : AstNode
    {
        public Token KindTok { get; }
        public PropertyAccessorKind Kind { get; }
        public PropertyAccessorVisibility Visibility { get; }
        public AstNode? BodyNode { get; }
        public bool IsAuto => BodyNode == null;

        public PropertyAccessorNode(
            Token kindTok,
            PropertyAccessorKind kind,
            PropertyAccessorVisibility visibility,
            AstNode? bodyNode) : base(AstNodeType.PropertyAccessor)
        {
            KindTok = kindTok;
            Kind = kind;
            Visibility = visibility;
            BodyNode = bodyNode;
            PositionStart = kindTok.PositionStart;
            PositionEnd = bodyNode?.PositionEnd ?? kindTok.PositionEnd;
        }
    }
}

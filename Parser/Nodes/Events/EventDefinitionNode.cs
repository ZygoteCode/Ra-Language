using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Events
{
    // Event declaration as it appears in a class / record class /
    // interface / trait body:
    //
    //     [pub|priv] [static] [abstract|override] [cancellable]
    //     event NAME(payload_params) [{ accessor_block }]
    //
    // The accessor block, when present, contains zero or more
    // `pub|priv subscribe|raise` visibility-override entries (no
    // bodies — events have no user-authored accessor code in v1).
    //
    // Default visibility split:
    //   - subscribe = property's overall IsPublic
    //   - raise     = priv (regardless of overall) unless explicitly
    //                  overridden by a `pub raise;` accessor
    //
    // PayloadParams is empty for `event Beat()`.
    public sealed class EventDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public List<EventPayloadParam> PayloadParams { get; }
        public List<EventAccessorNode> Accessors { get; }

        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsOverride { get; }
        public bool IsCancellable { get; }
        public bool IsTolerant { get; }
        public bool IsAsync { get; }

        public EventDefinitionNode(
            Token nameTok,
            List<EventPayloadParam> payloadParams,
            List<EventAccessorNode> accessors,
            bool isPublic,
            bool isStatic,
            bool isAbstract,
            bool isOverride,
            bool isCancellable,
            bool isTolerant = false,
            bool isAsync = false) : base(AstNodeType.EventDefinition)
        {
            NameTok = nameTok;
            PayloadParams = payloadParams;
            Accessors = accessors;
            IsPublic = isPublic;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            IsOverride = isOverride;
            IsCancellable = isCancellable;
            IsTolerant = isTolerant;
            IsAsync = isAsync;

            PositionStart = nameTok.PositionStart;
            if (accessors.Count > 0)
                PositionEnd = accessors[^1].PositionEnd;
            else if (payloadParams.Count > 0)
                PositionEnd = payloadParams[^1].NameTok.PositionEnd;
            else
                PositionEnd = nameTok.PositionEnd;
        }
    }
}

using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Patterns
{
    // Postfix `?` operator. Evaluates `Target`; if it is `Result.Ok(v)`, the
    // node yields `v`. If it is `Result.Err(e)`, the surrounding function
    // returns `Result.Err(e)` early. The visitor enforces the Result-typed
    // scrutinee at runtime.
    public sealed class TryUnwrapNode : AstNode
    {
        public AstNode Target { get; }
        public TryUnwrapNode(AstNode target, Position s, Position e) : base(AstNodeType.TryUnwrap)
        {
            Target = target;
            PositionStart = s;
            PositionEnd = e;
        }
    }
}

using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class VariableDeleteNode : AstNode
    {
        public List<Token> Tokens { get; }

        // Parallel to Tokens: one BindingId per name. Allocated lazily by the
        // Resolver; null until the pass runs.
        public BindingId[]? Bindings;
        public BindingKind[]? BindingKinds;

        public VariableDeleteNode(List<Token> tokens) : base(AstNodeType.VariableDelete)
        {
            Tokens = tokens;
            PositionStart = tokens[0].PositionStart;
            PositionEnd = tokens[Tokens.Count - 1].PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class VariableDeleteNode : AstNode
    {
        public List<Token> Tokens { get; }
        
        public VariableDeleteNode(List<Token> tokens) : base(AstNodeType.VariableDelete)
        {
            Tokens = tokens;
            PositionStart = tokens[0].PositionStart;
            PositionEnd = tokens[Tokens.Count - 1].PositionEnd;
        }
    }
}
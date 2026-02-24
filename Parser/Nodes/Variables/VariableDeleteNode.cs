using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableDeleteNode : AstNode
    {
        public List<Token> Tokens { get; }
        
        public VariableDeleteNode(List<Token> tokens)
        {
            Tokens = tokens;
            PositionStart = tokens[0].PositionStart;
            PositionEnd = tokens[Tokens.Count - 1].PositionEnd;
        }
    }
}
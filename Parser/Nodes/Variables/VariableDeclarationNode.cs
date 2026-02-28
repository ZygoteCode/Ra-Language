using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableDeclarationNode : AstNode
    {
        public VariableDeclarationType DeclarationType { get; }
        public List<(Token, AstNode?)> Declarations { get; }

        public VariableDeclarationNode(VariableDeclarationType declarationType, List<(Token, AstNode?)> declarations) : base(AstNodeType.VariableDeclaration)
        {
            DeclarationType = declarationType;
            Declarations = declarations;

            PositionStart = Declarations[0].Item1.PositionStart;
            PositionEnd = Declarations[Declarations.Count - 1].Item1.PositionEnd;
        }
    }

    public enum VariableDeclarationType
    {
        VARIABLE,
        CONST,
        FINAL
    }
}
using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableDeclarationNode : AstNode
    {
        public VariableDeclarationType DeclarationType { get; }
        public List<(Token, AstNode?, TypeDescriptor?)> Declarations { get; }
        public bool IsPublic { get; }

        public VariableDeclarationNode(
            VariableDeclarationType declarationType,
            List<(Token, AstNode?, TypeDescriptor?)> declarations,
            bool isPublic = false
        ) : base(AstNodeType.VariableDeclaration)
        {
            DeclarationType = declarationType;
            Declarations = declarations;
            IsPublic = isPublic;

            PositionStart = Declarations[0].Item1.PositionStart;
            PositionEnd = Declarations[Declarations.Count - 1].Item1.PositionEnd;
        }
    }

    public enum VariableDeclarationType
    {
        VARIABLE,
        CONST,
        FINAL,
        LET
    }
}
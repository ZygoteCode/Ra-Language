using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class VariableDeclarationNode : AstNode
    {
        public VariableDeclarationType DeclarationType { get; }
        public List<(Token, AstNode?, TypeDescriptor?)> Declarations { get; }
        public bool IsPublic { get; }
        public bool IsStatic { get; }

        // Resolver output. Parallel to Declarations; element i is the slot
        // allocated to Declarations[i].Item1 (the name token). Allocated lazily.
        public BindingId[]? Bindings;

        public VariableDeclarationNode(
            VariableDeclarationType declarationType,
            List<(Token, AstNode?, TypeDescriptor?)> declarations,
            bool isPublic = false,
            bool isStatic = false
        ) : base(AstNodeType.VariableDeclaration)
        {
            DeclarationType = declarationType;
            Declarations = declarations;
            IsPublic = isPublic;
            IsStatic = isStatic;

            PositionStart = Declarations[0].Item1.PositionStart;
            PositionEnd = Declarations[Declarations.Count - 1].Item1.PositionEnd;
        }
    }

    public enum VariableDeclarationType
    {
        VARIABLE,
        CONST,
        FINAL,
        LET,
        LET_MUT,
        LET_CONST
    }
}
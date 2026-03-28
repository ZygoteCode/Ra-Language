using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Interfaces
{
    public class InterfaceDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<InterfaceMethodSignatureNode> Methods { get; }

        public InterfaceDefinitionNode(Token nameTok, bool isPublic, List<InterfaceMethodSignatureNode> methods)
            : base(AstNodeType.InterfaceDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Methods = methods;
            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0 ? methods[^1].PositionEnd : nameTok.PositionEnd;
        }
    }
}
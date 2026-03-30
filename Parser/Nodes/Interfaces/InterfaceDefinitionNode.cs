using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Parser.Nodes.Interfaces
{
    public class InterfaceDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<InterfaceMethodSignatureNode> Methods { get; }
        public List<StructFieldDefinitionNode> Fields { get; }

        public InterfaceDefinitionNode(Token nameTok, bool isPublic, List<InterfaceMethodSignatureNode> methods, List<StructFieldDefinitionNode> fields)
            : base(AstNodeType.InterfaceDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Methods = methods;
            Fields = fields;
            PositionStart = nameTok.PositionStart;
            PositionEnd = (methods.Count > 0 ? methods[^1].PositionEnd : nameTok.PositionEnd);
            if (fields.Count > 0)
                PositionEnd = fields[^1].PositionEnd;
        }
    }
}
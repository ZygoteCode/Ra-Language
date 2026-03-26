using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class StructFieldDefinitionNode : AstNode
    {
        public bool IsPublic { get; }
        public Token NameTok { get; }
        public TypeDescriptor? FieldType { get; }
        public AstNode? DefaultValueNode { get; }

        public StructFieldDefinitionNode(bool isPublic, Token nameTok, TypeDescriptor? fieldType, AstNode? defaultValueNode) : base(AstNodeType.StructFieldDefinition)
        {
            IsPublic = isPublic;
            NameTok = nameTok;
            FieldType = fieldType;
            DefaultValueNode = defaultValueNode;
            PositionStart = nameTok.PositionStart;
            PositionEnd = defaultValueNode != null ? defaultValueNode.PositionEnd : nameTok.PositionEnd;
        }
    }
}
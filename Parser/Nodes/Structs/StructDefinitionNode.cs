using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class StructDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<StructMethodDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; }

        public StructDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<StructFieldDefinitionNode> fields,
            List<StructMethodDefinitionNode> methods,
            List<OperatorDefinitionNode> operators) : base(AstNodeType.StructDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;
            Operators = operators;
            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0
                ? methods[^1].PositionEnd
                : (fields.Count > 0 ? fields[^1].PositionEnd : nameTok.PositionEnd);
        }
    }
}
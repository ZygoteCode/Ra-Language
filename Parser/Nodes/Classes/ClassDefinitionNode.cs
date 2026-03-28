using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Parser.Nodes.Classes
{
    public class ClassDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }

        public ClassDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<StructFieldDefinitionNode> fields,
            List<FunctionDefinitionNode> methods
        ) : base(AstNodeType.ClassDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;

            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0
                ? methods[^1].PositionEnd
                : (fields.Count > 0 ? fields[^1].PositionEnd : nameTok.PositionEnd);
        }
    }
}
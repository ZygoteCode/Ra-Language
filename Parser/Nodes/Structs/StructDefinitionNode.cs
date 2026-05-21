using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class StructDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<StructMethodDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public StructDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<StructFieldDefinitionNode> fields,
            List<StructMethodDefinitionNode> methods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null) : base(AstNodeType.StructDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;
            Operators = operators;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0
                ? methods[^1].PositionEnd
                : (fields.Count > 0 ? fields[^1].PositionEnd : nameTok.PositionEnd);
        }
    }
}

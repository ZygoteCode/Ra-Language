using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Parser.Nodes.Interfaces
{
    public class InterfaceDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<InterfaceMethodSignatureNode> Methods { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public InterfaceDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<InterfaceMethodSignatureNode> methods,
            List<StructFieldDefinitionNode> fields,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null)
            : base(AstNodeType.InterfaceDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Methods = methods;
            Fields = fields;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = (methods.Count > 0 ? methods[^1].PositionEnd : nameTok.PositionEnd);
            if (fields.Count > 0)
                PositionEnd = fields[^1].PositionEnd;
        }
    }
}

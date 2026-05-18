using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Annotations
{
    public class AnnotationDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public string Name => NameTok.Value?.ToString() ?? string.Empty;
        public bool IsPublic { get; }
        public List<AnnotationParameterNode> Parameters { get; }

        public AnnotationDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<AnnotationParameterNode> parameters
        ) : base(AstNodeType.AnnotationDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Parameters = parameters ?? new List<AnnotationParameterNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = nameTok.PositionEnd;
        }
    }
}

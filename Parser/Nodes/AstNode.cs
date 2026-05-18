using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Annotations;

namespace RaLanguage.Parser.Nodes
{
    public abstract class AstNode
    {
        public Position PositionStart { get; set; }
        public Position PositionEnd { get; set; }
        public AstNodeType NodeType { get; }

        public List<AnnotationApplicationNode>? Annotations { get; set; }

        public bool HasAnnotations => Annotations != null && Annotations.Count > 0;

        protected AstNode(AstNodeType nodeType) => NodeType = nodeType;
    }
}

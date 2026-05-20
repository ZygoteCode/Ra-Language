using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Parser.Nodes.Enums
{
    public class EnumDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public List<EnumVariantSpec> Variants { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public EnumDefinitionNode(
            Token nameTok,
            List<EnumVariantSpec> variants,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null): base(AstNodeType.EnumDefinition)
        {
            NameTok = nameTok;
            Variants = variants;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = variants.Count > 0
                ? (variants[variants.Count - 1].ValueNode?.PositionEnd ?? variants[variants.Count - 1].MemberTok.PositionEnd)
                : nameTok.PositionEnd;
        }
    }
}

using RaLanguage.Lexer.Tokens;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Parser.Nodes.Traits
{
    public class TraitDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<TraitMethodDefinitionNode> Methods { get; }

        public TraitDefinitionNode(Token nameTok, bool isPublic, List<TraitMethodDefinitionNode> methods)
            : base(AstNodeType.TraitDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Methods = methods;
            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0 ? methods[^1].PositionEnd : nameTok.PositionEnd;
        }
    }
}
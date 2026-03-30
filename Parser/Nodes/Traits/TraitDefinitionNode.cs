using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Structs;
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
        public List<StructFieldDefinitionNode> Fields { get; }

        public TraitDefinitionNode(Token nameTok, bool isPublic, List<TraitMethodDefinitionNode> methods, List<StructFieldDefinitionNode> fields)
            : base(AstNodeType.TraitDefinition)
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
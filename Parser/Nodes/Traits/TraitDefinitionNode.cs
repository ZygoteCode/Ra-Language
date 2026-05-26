using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Parser.Nodes.Traits
{
    public sealed class TraitDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public List<TraitMethodDefinitionNode> Methods { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<PropertyDefinitionNode> Properties { get; }
        public List<EventDefinitionNode> Events { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public TraitDefinitionNode(
            Token nameTok,
            bool isPublic,
            List<TraitMethodDefinitionNode> methods,
            List<StructFieldDefinitionNode> fields,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null,
            List<PropertyDefinitionNode>? properties = null,
            List<EventDefinitionNode>? events = null)
            : base(AstNodeType.TraitDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            Methods = methods;
            Fields = fields;
            Properties = properties ?? new List<PropertyDefinitionNode>();
            Events = events ?? new List<EventDefinitionNode>();
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = (methods.Count > 0 ? methods[^1].PositionEnd : nameTok.PositionEnd);
            if (fields.Count > 0)
                PositionEnd = fields[^1].PositionEnd;
        }
    }
}

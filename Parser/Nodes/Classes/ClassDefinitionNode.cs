using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Classes
{
    public class ClassDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public bool IsAbstract { get; }
        public bool IsStatic { get; }
        public TypeDescriptor? BaseType { get; }
        public List<TypeDescriptor> ImplementedInterfaces { get; }
        public List<TypeDescriptor> WithTraits { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public ClassDefinitionNode(
            Token nameTok,
            bool isPublic,
            bool isAbstract,
            bool isStatic,
            TypeDescriptor? baseType,
            List<TypeDescriptor> implementedInterfaces,
            List<TypeDescriptor> withTraits,
            List<StructFieldDefinitionNode> fields,
            List<FunctionDefinitionNode> methods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null
        ) : base(AstNodeType.ClassDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            BaseType = baseType;
            Fields = fields;
            Methods = methods;
            Operators = operators;
            ImplementedInterfaces = implementedInterfaces;
            WithTraits = withTraits;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();

            PositionStart = nameTok.PositionStart;
            PositionEnd = methods.Count > 0
                ? methods[^1].PositionEnd
                : (fields.Count > 0 ? fields[^1].PositionEnd : nameTok.PositionEnd);
        }
    }
}

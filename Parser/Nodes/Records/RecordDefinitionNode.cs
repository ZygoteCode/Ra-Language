using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Records
{
    // A record declaration. Records come in two flavors:
    //
    //   record Name(p1: T1, p2: T2)               → value record:
    //       IsCopy = true, sealed (no inheritance), structural equality,
    //       structural hash, auto to_string, with-expression.
    //
    //   record class Name(p1: T1, p2: T2)         → reference record:
    //       IsCopy = false, structural equality scoped to declared type,
    //       structural hash, auto to_string, with-expression. No
    //       inheritance in v1; the IsRefRecord flag carries the
    //       distinction for the runtime.
    //
    // Primary fields (the header tuple) are public, immutable instance
    // fields by default. Body may declare additional methods and
    // operator overloads via the existing struct/class machinery, but
    // it MAY NOT declare extra fields — auto-equality/hash/to_string
    // operate strictly over the primary-field list, and allowing free
    // fields would silently de-sync them. This rule is enforced by the
    // parser, not just by convention.
    public sealed class RecordDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public bool IsRefRecord { get; }
        public List<RecordPrimaryFieldNode> PrimaryFields { get; }
        public List<StructMethodDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public RecordDefinitionNode(
            Token nameTok,
            bool isPublic,
            bool isRefRecord,
            List<RecordPrimaryFieldNode> primaryFields,
            List<StructMethodDefinitionNode> methods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams,
            List<WhereConstraintNode>? whereConstraints) : base(AstNodeType.RecordDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            IsRefRecord = isRefRecord;
            PrimaryFields = primaryFields;
            Methods = methods;
            Operators = operators;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            if (methods.Count > 0) PositionEnd = methods[^1].PositionEnd;
            else if (primaryFields.Count > 0) PositionEnd = primaryFields[^1].PositionEnd;
            else PositionEnd = nameTok.PositionEnd;
        }
    }
}

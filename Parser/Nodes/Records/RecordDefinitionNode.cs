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
    //       structural hash, auto to_string, with-expression. Optional
    //       inheritance via `abstract record class` parents — see
    //       IsAbstract / BaseType. Equality remains type-restricted to
    //       the exact concrete Definition (no EqualityContract trap).
    //
    // Primary fields (the header tuple) are public, immutable instance
    // fields by default. Body may declare additional methods and
    // operator overloads via the existing struct/class machinery, but
    // it MAY NOT declare extra fields — auto-equality/hash/to_string
    // operate strictly over the primary-field list, and allowing free
    // fields would silently de-sync them. This rule is enforced by the
    // parser, not just by convention.
    //
    // Auto-generation flags (AutoEquals / AutoToString) are flipped to
    // false by `@derive(equals=false, to_string=false)`, in which case
    // equality falls back to reference identity and to_string falls back
    // to a non-structural format. The user can still provide their own
    // `operator ==` / `fn to_string()` to fill the gap.
    public sealed class RecordDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public bool IsPublic { get; }
        public bool IsRefRecord { get; }
        public bool IsAbstract { get; }
        public TypeDescriptor? BaseType { get; set; }
        public List<AstNode>? BaseArgs { get; set; }
        public List<RecordPrimaryFieldNode> PrimaryFields { get; }
        public List<StructMethodDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        // Auto-derive flags. Default true; @derive(equals=false, ...)
        // flips to false during the DeriveTransformer pre-pass.
        public bool AutoEquals { get; set; } = true;
        public bool AutoToString { get; set; } = true;

        public RecordDefinitionNode(
            Token nameTok,
            bool isPublic,
            bool isRefRecord,
            bool isAbstract,
            TypeDescriptor? baseType,
            List<AstNode>? baseArgs,
            List<RecordPrimaryFieldNode> primaryFields,
            List<StructMethodDefinitionNode> methods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams,
            List<WhereConstraintNode>? whereConstraints) : base(AstNodeType.RecordDefinition)
        {
            NameTok = nameTok;
            IsPublic = isPublic;
            IsRefRecord = isRefRecord;
            IsAbstract = isAbstract;
            BaseType = baseType;
            BaseArgs = baseArgs;
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

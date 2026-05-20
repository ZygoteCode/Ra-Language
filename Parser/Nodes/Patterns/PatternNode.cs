using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Patterns
{
    // Patterns are a parallel grammar to expressions: they are not visited by
    // the interpreter directly, only the match engine reads them. Living
    // outside the AstNode hierarchy keeps the visitor dispatch table small
    // and avoids burdening every pattern kind with a no-op visitor entry.
    public abstract class PatternNode
    {
        public Position PositionStart { get; protected set; }
        public Position PositionEnd { get; protected set; }
        protected PatternNode(Position start, Position end) { PositionStart = start; PositionEnd = end; }
    }

    public sealed class WildcardPatternNode : PatternNode
    {
        public WildcardPatternNode(Position s, Position e) : base(s, e) { }
    }

    // Captures a literal scalar that the engine compares against the
    // scrutinee via the standard equality operator. The wrapped AstNode is a
    // literal (number / string / boolean / null) and is evaluated lazily by
    // the engine, not at parse time.
    public sealed class LiteralPatternNode : PatternNode
    {
        public AstNode Expression { get; }
        public LiteralPatternNode(AstNode expression, Position s, Position e) : base(s, e)
        {
            Expression = expression;
        }
    }

    // Binding pattern: `x`. Always matches; introduces `x` as a local in the
    // arm's scope, bound to the scrutinee value.
    public sealed class VariablePatternNode : PatternNode
    {
        public string Name { get; }
        public VariablePatternNode(string name, Position s, Position e) : base(s, e)
        {
            Name = name;
        }
    }

    // Enum-variant pattern. EnumName may be null when the variant name can
    // be inferred from the scrutinee's declared enum type (`case Ok(v)`
    // inside a match on a `Result<T,E>` scrutinee). Always non-null when the
    // user spelled it explicitly (`case Result.Ok(v)`).
    public sealed class VariantPatternNode : PatternNode
    {
        public string? EnumName { get; }
        public string VariantName { get; }
        public List<PatternNode>? SubPatterns { get; }

        public VariantPatternNode(string? enumName, string variantName, List<PatternNode>? subs, Position s, Position e) : base(s, e)
        {
            EnumName = enumName;
            VariantName = variantName;
            SubPatterns = subs;
        }
    }

    // Tuple pattern: `(x, y, z)`.
    public sealed class TuplePatternNode : PatternNode
    {
        public List<PatternNode> Elements { get; }
        public TuplePatternNode(List<PatternNode> elements, Position s, Position e) : base(s, e)
        {
            Elements = elements;
        }
    }

    // List pattern: `[a, b, c]` or `[head, ..tail]`. RestPattern (if present)
    // sits at RestIndex inside the original `[...]` ordering.
    public sealed class ListPatternNode : PatternNode
    {
        public List<PatternNode> Elements { get; }
        public RestPatternNode? Rest { get; }
        public int RestIndex { get; }

        public ListPatternNode(List<PatternNode> elements, RestPatternNode? rest, int restIndex, Position s, Position e) : base(s, e)
        {
            Elements = elements;
            Rest = rest;
            RestIndex = restIndex;
        }
    }

    // Struct destructuring: `User { name, age: a }`. Field entry whose Pattern
    // is null performs a shorthand bind by the field name.
    public sealed class StructPatternNode : PatternNode
    {
        public string StructName { get; }
        public List<(string Field, PatternNode? Pattern)> Fields { get; }

        public StructPatternNode(string structName, List<(string, PatternNode?)> fields, Position s, Position e) : base(s, e)
        {
            StructName = structName;
            Fields = fields;
        }
    }

    // `..` rest pattern with optional binding name (`..tail`). Only valid
    // inside a ListPattern.
    public sealed class RestPatternNode : PatternNode
    {
        public string? BindName { get; }
        public RestPatternNode(string? bindName, Position s, Position e) : base(s, e)
        {
            BindName = bindName;
        }
    }
}

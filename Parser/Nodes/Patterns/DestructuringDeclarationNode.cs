using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Patterns
{
    // Destructuring declaration: 'let (a, b) = expr;', 'let [h, ..t] = expr;',
    // 'let User { name, age } = expr;'.
    //
    // The Pattern must be irrefutable for the scrutinee's declared type. A
    // refutable pattern (literal, range, relational, type-test, variant,
    // map, or-pattern, list with both prefix and suffix around the rest) is
    // rejected at parse time with a help message that points the user to
    // 'if let' or 'match'.
    //
    // At runtime the visitor evaluates Initializer once, runs the pattern
    // engine in irrefutable mode, and commits every binding to the *enclosing*
    // scope (mirroring an ordinary 'let'/'const'/'var'/'final' declaration).
    public sealed class DestructuringDeclarationNode : AstNode
    {
        public PatternNode Pattern { get; }
        public AstNode Initializer { get; }
        public VariableDeclarationType Kind { get; }
        public TypeDescriptor? DeclaredType { get; }
        public bool IsPublic { get; }
        public bool IsStatic { get; }

        public DestructuringDeclarationNode(
            PatternNode pattern,
            AstNode initializer,
            VariableDeclarationType kind,
            TypeDescriptor? declaredType,
            Position start,
            Position end,
            bool isPublic = false,
            bool isStatic = false)
            : base(AstNodeType.DestructuringDeclaration)
        {
            Pattern = pattern;
            Initializer = initializer;
            Kind = kind;
            DeclaredType = declaredType;
            IsPublic = isPublic;
            IsStatic = isStatic;
            PositionStart = start;
            PositionEnd = end;
        }
    }
}

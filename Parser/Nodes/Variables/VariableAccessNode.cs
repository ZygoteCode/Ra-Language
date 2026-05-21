using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class VariableAccessNode : AstNode
    {
        public Token VarNameTok { get; }

        // Resolved name string. Token.Value is `object?`; .ToString() per visit is
        // measurable when the loop body is short. Cache once at AST construction.
        public string Name { get; }

        // Inline cache for SymbolEntry resolution. Written atomically (single ref
        // assignment) by the visitor; reads see either the previous cache or the
        // new one, never a torn read. See SymbolLookupCache for invalidation rules.
        internal SymbolLookupCache? LookupCache;

        public VariableAccessNode(Token varNameTok) : base(AstNodeType.VariableAccess)
        {
            VarNameTok = varNameTok;
            Name = varNameTok.Value?.ToString() ?? "";
            PositionStart = varNameTok.PositionStart;
            PositionEnd = varNameTok.PositionEnd;
        }
    }
}
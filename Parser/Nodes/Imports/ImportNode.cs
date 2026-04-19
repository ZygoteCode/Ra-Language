using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Imports
{
    public abstract class ImportNode : AstNode
    {
        public Token ModulePathTok { get; }
        public string ModulePath => ModulePathTok.Value?.ToString() ?? "";

        protected ImportNode(Token modulePathTok, Position positionStart, Position positionEnd, AstNodeType nodeType)
            : base(nodeType)
        {
            ModulePathTok = modulePathTok;
        }
    }

    public class ImportAllNode : ImportNode
    {
        public ImportAllNode(Token modulePathTok, Position positionStart, Position positionEnd)
            : base(modulePathTok, positionStart, positionEnd, AstNodeType.ImportAll)
        {
        }
    }

    public class ImportSelectiveNode : ImportNode
    {
        public List<Token> SymbolNames { get; }

        public ImportSelectiveNode(Token modulePathTok, List<Token> symbolNames, Position positionStart, Position positionEnd)
            : base(modulePathTok, positionStart, positionEnd, AstNodeType.ImportSelective)
        {
            SymbolNames = symbolNames;
        }
    }

    public class ImportAliasNode : ImportNode
    {
        public Token AliasTok { get; }
        public string Alias => AliasTok.Value?.ToString() ?? "";

        public ImportAliasNode(Token modulePathTok, Token aliasTok, Position positionStart, Position positionEnd)
            : base(modulePathTok, positionStart, positionEnd, AstNodeType.ImportAlias)
        {
            AliasTok = aliasTok;
        }
    }
}

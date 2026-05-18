using RaLanguage.Interpreter.Modules;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Imports
{
    public abstract class ImportNode : AstNode
    {
        public ModuleSpecifier Specifier { get; }

        public string ModulePath => Specifier.Display;

        protected ImportNode(ModuleSpecifier specifier, Position positionStart, Position positionEnd, AstNodeType nodeType)
            : base(nodeType)
        {
            Specifier = specifier;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }

    public class ImportAllNode : ImportNode
    {
        public ImportAllNode(ModuleSpecifier specifier, Position positionStart, Position positionEnd)
            : base(specifier, positionStart, positionEnd, AstNodeType.ImportAll)
        {
        }
    }

    public class ImportSelectiveNode : ImportNode
    {
        public List<Token> SymbolNames { get; }

        public ImportSelectiveNode(ModuleSpecifier specifier, List<Token> symbolNames, Position positionStart, Position positionEnd)
            : base(specifier, positionStart, positionEnd, AstNodeType.ImportSelective)
        {
            SymbolNames = symbolNames;
        }
    }

    public class ImportAliasNode : ImportNode
    {
        public Token AliasTok { get; }
        public string Alias => AliasTok.Value?.ToString() ?? "";

        public ImportAliasNode(ModuleSpecifier specifier, Token aliasTok, Position positionStart, Position positionEnd)
            : base(specifier, positionStart, positionEnd, AstNodeType.ImportAlias)
        {
            AliasTok = aliasTok;
        }
    }
}

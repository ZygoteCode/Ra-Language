using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class FuncDefNode : AstNode
    {
        public Token? VarNameTok { get; }
        public List<Token> ArgNameToks { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }

        public FuncDefNode(Token? varNameTok, List<Token> argNameToks, AstNode bodyNode, bool shouldAutoReturn)
        {
            VarNameTok = varNameTok;
            ArgNameToks = argNameToks;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;

            if (varNameTok != null) PosStart = varNameTok.PosStart;
            else if (argNameToks.Count > 0) PosStart = argNameToks[0].PosStart;
            else PosStart = bodyNode.PosStart;

            PosEnd = bodyNode.PosEnd;
        }
    }
}
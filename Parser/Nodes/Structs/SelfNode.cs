using RaLanguage.Lexer;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class SelfNode : AstNode
    {
        public SelfNode(Position positionStart, Position positionEnd) : base(AstNodeType.Self)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
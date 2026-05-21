using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Lexer;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class SelfNode : AstNode
    {
        // Resolver pins `self` to its enclosing method frame's offset 0. Stored
        // here so the visitor can identify the owning frame without a chain walk.
        public BindingId Binding = BindingId.Unresolved;

        public SelfNode(Position positionStart, Position positionEnd) : base(AstNodeType.Self)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
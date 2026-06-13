using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Runtime
{
    // Lowers a multi-parameter index access `obj[a, b, …]` to the method call
    // `obj.op_index(a, b, …)` (read) / `obj.op_index_set(a, b, …, value)`
    // (write). A multi-index access carries N index expressions on a
    // ListAccessNode; both the IR compiler and the AST visitor reuse this to
    // build a synthetic — but fully ordinary — `FunctionCallNode`, so the
    // existing call + member-access + arity-overload machinery resolves the
    // right `op_index` overload with zero new opcodes.
    internal static class IndexDesugar
    {
        public static FunctionCallNode BuildCall(AstNode target, string method, IReadOnlyList<AstNode> args, Position p1, Position p2)
        {
            var memberTok = new Token(TokenType.IDENTIFIER, method, p1, p2);
            var member = new MemberAccessNode(target, memberTok);
            var argNodes = new List<ArgumentNode>(args.Count);
            foreach (var a in args) argNodes.Add(new ArgumentNode(null, a));
            return new FunctionCallNode(member, argNodes);
        }

        // Read: obj.op_index(indices…)
        public static FunctionCallNode BuildGet(AstNode target, IReadOnlyList<AstNode> indices, Position p1, Position p2)
            => BuildCall(target, "op_index", indices, p1, p2);

        // Write: obj.op_index_set(indices…, value)
        public static FunctionCallNode BuildSet(AstNode target, IReadOnlyList<AstNode> indices, AstNode value, Position p1, Position p2)
        {
            var args = new List<AstNode>(indices.Count + 1);
            args.AddRange(indices);
            args.Add(value);
            return BuildCall(target, "op_index_set", args, p1, p2);
        }

        // Compound write: obj.op_index_set(indices…, obj.op_index(indices…) <op> value).
        // The indices are evaluated in both the read-back and the set; multi-index
        // compound assignment therefore assumes side-effect-free index expressions
        // (the usual matrix-cell case).
        public static FunctionCallNode BuildCompoundSet(AstNode target, IReadOnlyList<AstNode> indices, Token binOp, AstNode value, Position p1, Position p2)
        {
            var current = BuildGet(target, indices, p1, p2);
            var combined = new BinaryOperationNode(current, binOp, value);
            return BuildSet(target, indices, combined, p1, p2);
        }

        // Map a compound-assignment token (`+=`, `*=`, `<<=`, …) to the binary
        // operator token used to read-modify-write a multi-index cell. Returns
        // false for the few compound ops without a clean binary desugar
        // (logical `and=`/`or=`, `??=`), which fall back to the single-index
        // path / a clear diagnostic.
        public static bool TryCompoundBinaryToken(TokenType compound, Position p1, Position p2, out Token tok)
        {
            TokenType binary;
            switch (compound)
            {
                case TokenType.PLUS_EQ: binary = TokenType.PLUS; break;
                case TokenType.MINUS_EQ: binary = TokenType.MINUS; break;
                case TokenType.MUL_EQ: binary = TokenType.MUL; break;
                case TokenType.DIV_EQ: binary = TokenType.DIV; break;
                case TokenType.MODULO_EQ: binary = TokenType.MODULO; break;
                case TokenType.POW_EQ: binary = TokenType.POW; break;
                case TokenType.BITWISE_AND_EQ: binary = TokenType.BITWISE_AND; break;
                case TokenType.BITWISE_OR_EQ: binary = TokenType.BITWISE_OR; break;
                case TokenType.BITWISE_LEFT_SHIFT_EQ: binary = TokenType.BITWISE_LEFT_SHIFT; break;
                case TokenType.BITWISE_RIGHT_SHIFT_EQ: binary = TokenType.BITWISE_RIGHT_SHIFT; break;
                case TokenType.BITWISE_ROTATE_LEFT_EQ: binary = TokenType.BITWISE_ROTATE_LEFT; break;
                case TokenType.BITWISE_ROTATE_RIGHT_EQ: binary = TokenType.BITWISE_ROTATE_RIGHT; break;
                default: tok = default; return false;
            }
            tok = new Token(binary, null, p1, p2);
            return true;
        }
    }
}

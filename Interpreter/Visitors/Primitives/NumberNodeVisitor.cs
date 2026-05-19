using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class NumberNodeVisitor : NodeVisitor<NumberNode>
    {
        protected sealed override RuntimeResult VisitNode(NumberNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            // Constant-fold: a numeric literal evaluates to the same value on every visit. Parse
            // once and reuse the RuntimeValue. SetContext mutates a single field which the
            // interpreter is single-threaded, so the reuse is safe.
            var cached = node.CachedValue;
            if (cached != null)
            {
                return res.Success(cached.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            cached = ParseLiteral(node);
            node.CachedValue = cached;
            return res.Success(cached.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private static RuntimeValue ParseLiteral(NumberNode node)
        {
            var raw = node.Tok.Value!.ToString()!;
            // Suffix tags use lowercase ASCII; comparing ordinally avoids ToLowerInvariant().
            // The lexer already normalises numeric literals into the canonical form.
            string s = raw;

            if (EndsWithCI(s, "us"))
            {
                return new UnsignedShortValue((ushort)BigNumber.Parse(s.Substring(0, s.Length - 2)));
            }
            if (EndsWithCI(s, "ul"))
            {
                string num = BigNumber.Parse(s.Substring(0, s.Length - 2)).ToString();
                return new UnsignedLongValue(ulong.Parse(num));
            }
            if (EndsWithCI(s, "ui"))
            {
                string num = BigNumber.Parse(s.Substring(0, s.Length - 2)).ToString();
                return new UnsignedIntegerValue(uint.Parse(num));
            }
            if (EndsWithCI(s, "f"))
            {
                return new FloatValue((float)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (EndsWithCI(s, "d"))
            {
                return new DoubleValue((double)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (EndsWithCI(s, "m"))
            {
                return new DecimalValue((decimal)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (EndsWithCI(s, "s"))
            {
                return new ShortValue((short)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (EndsWithCI(s, "l"))
            {
                return new LongValue((long)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (EndsWithCI(s, "i"))
            {
                return new IntegerValue((int)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }

            return new NumberValue(BigNumber.Parse(s));
        }

        private static bool EndsWithCI(string s, string suffix)
        {
            if (s.Length < suffix.Length) return false;
            int offset = s.Length - suffix.Length;
            for (int i = 0; i < suffix.Length; i++)
            {
                char a = s[offset + i];
                char b = suffix[i];
                // ASCII case fold
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (a != b) return false;
            }
            return true;
        }
    }
}

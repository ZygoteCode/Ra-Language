using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class NumberNodeVisitor : NodeVisitor<NumberNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(NumberNode node, Context context, IInterpreter interpreter)
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

        public static RuntimeValue ParseLiteral(NumberNode node)
        {
            var raw = node.Tok.Value!.ToString()!;
            string s = raw;

            // Hex / binary / octal literals never carry a type suffix - every
            // ASCII letter after the prefix is part of the value (e.g. 0xFF).
            // Skipping the suffix tests below prevents 0xFF from matching the
            // `f` (float) suffix and collapsing to 0xF.
            bool isBasePrefixed = s.Length >= 2 && s[0] == '0' &&
                (s[1] == 'x' || s[1] == 'X' || s[1] == 'b' || s[1] == 'B' || s[1] == 'o' || s[1] == 'O');

            if (!isBasePrefixed && EndsWithCI(s, "us"))
            {
                return new UnsignedShortValue((ushort)BigNumber.Parse(s.Substring(0, s.Length - 2)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "ul"))
            {
                string num = BigNumber.Parse(s.Substring(0, s.Length - 2)).ToString();
                return new UnsignedLongValue(ulong.Parse(num));
            }
            if (!isBasePrefixed && EndsWithCI(s, "ui"))
            {
                string num = BigNumber.Parse(s.Substring(0, s.Length - 2)).ToString();
                return new UnsignedIntegerValue(uint.Parse(num));
            }
            if (!isBasePrefixed && EndsWithCI(s, "f"))
            {
                // BigNumber has no explicit `float` cast operator. Going
                // directly through `(float)BigNumber` selects the
                // `(float)(int)` chain and truncates the fractional part
                // (e.g. `1.5f` collapsed to `1`). Route through `double` so
                // the fractional component survives.
                return new FloatValue((float)(double)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "d"))
            {
                return new DoubleValue((double)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "m"))
            {
                return new DecimalValue((decimal)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "s"))
            {
                return new ShortValue((short)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "l"))
            {
                return new LongValue((long)BigNumber.Parse(s.Substring(0, s.Length - 1)));
            }
            if (!isBasePrefixed && EndsWithCI(s, "i"))
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

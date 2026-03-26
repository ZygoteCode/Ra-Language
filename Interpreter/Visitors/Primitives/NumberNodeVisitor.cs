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
            var numberString = node.Tok.Value.ToString().ToLower();

            if (numberString.EndsWith("f"))
            {
                return res.Success(new FloatValue((float)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }
            else if (numberString.EndsWith("d"))
            {
                return res.Success(new DoubleValue((double)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }
            else if (numberString.EndsWith("m"))
            {
                return res.Success(new DecimalValue((decimal)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }
            else if (numberString.EndsWith("us"))
            {
                return res.Success(new UnsignedShortValue((ushort)BigNumber.Parse(numberString.Substring(0, numberString.Length - 2))));
            }
            else if (numberString.EndsWith("s"))
            {
                return res.Success(new ShortValue((short)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }
            else if (numberString.EndsWith("ul"))
            {
                string num = BigNumber.Parse(numberString.Substring(0, numberString.Length - 2)).ToString();
                return res.Success(new UnsignedLongValue(ulong.Parse(num)));
            }
            else if (numberString.EndsWith("l"))
            {
                return res.Success(new LongValue((long)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }
            else if (numberString.EndsWith("ui"))
            {
                string num = BigNumber.Parse(numberString.Substring(0, numberString.Length - 2)).ToString();
                return res.Success(new UnsignedIntegerValue(uint.Parse(num)));
            }
            else if (numberString.EndsWith("i"))
            {
                return res.Success(new IntegerValue((int)BigNumber.Parse(numberString.Substring(0, numberString.Length - 1))));
            }

            var bigNumber = BigNumber.Parse(numberString);
           

            return new RuntimeResult().Success(
                new NumberValue(BigNumber.Parse(numberString))
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd)
            );
        }
    }
}
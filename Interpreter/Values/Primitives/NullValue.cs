using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class NullValue : RuntimeValue
    {
        // Cached singleton. NullValue is stateless and immutable. Reused as the
        // result of any visitor that returns "void" (statements, loops, if without
        // else, etc.) and as the canonical null literal. SetContext/SetPos are
        // no-ops to make sharing safe.
        public static readonly NullValue Null = new NullValue();

        public sealed override RuntimeValueType Type => RuntimeValueType.Null;
        public sealed override bool IsCopy => true;

        public sealed override RuntimeValue SetContext(Context context) => this;
        public sealed override RuntimeValue SetPos(Position positionStart, Position positionEnd) => this;
        public sealed override RuntimeValue SetDeclarationType(VariableDeclarationType declarationType) => this;

        public sealed override VariableDeclarationType VariableDeclarationType
        {
            get => VariableDeclarationType.VARIABLE;
            set { /* singleton: declaration type lives on the SymbolEntry */ }
        }

        public sealed override RuntimeValue Copy() => this;

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Null)
            {
                return (BooleanValue.True, null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (BooleanValue.Of(s.Value == "null"), null);
            }

            return (BooleanValue.True, null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type != RuntimeValueType.Null)
            {
                return (BooleanValue.True, null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (BooleanValue.Of(s.Value != "null"), null);
            }

            return (BooleanValue.False, null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            return (BooleanValue.Of(other.Type == RuntimeValueType.Null), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            return (BooleanValue.Of(other.Type != RuntimeValueType.Null), null);
        }

        public sealed override bool IsTrue() => false;
        public sealed override string ToString() => "null";
    }
}

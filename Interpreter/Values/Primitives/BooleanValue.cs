using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public sealed class BooleanValue : RuntimeValue
    {
        public bool Value { get; }

        // Cached singletons. Reused for every transient boolean (comparison results,
        // logical ops, negation, conditional results). BooleanValue is immutable, so
        // sharing is safe; SetContext/SetPos/SetDeclarationType are no-ops below to
        // prevent callers from mutating the shared instance.
        public static readonly BooleanValue True = new BooleanValue(true);
        public static readonly BooleanValue False = new BooleanValue(false);

        public sealed override RuntimeValueType Type => RuntimeValueType.Boolean;
        public sealed override bool IsCopy => true;

        public BooleanValue(bool value)
        {
            Value = value;
        }

        public static BooleanValue Of(bool value) => value ? True : False;

        public sealed override RuntimeValue SetContext(Context context) => this;
        public sealed override RuntimeValue SetPos(Position positionStart, Position positionEnd) => this;
        public sealed override RuntimeValue SetDeclarationType(VariableDeclarationType declarationType) => this;

        public sealed override VariableDeclarationType VariableDeclarationType
        {
            get => VariableDeclarationType.VARIABLE;
            set { /* singleton: declaration type lives on the SymbolEntry */ }
        }

        public sealed override RuntimeValue Copy() => this;

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (Of(b.Value == Value), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (Of(s.Value == Value.ToString()), null);
            }

            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (Of(b.Value != Value), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (Of(s.Value != Value.ToString()), null);
            }

            return base.GetComparisonNe(other);
        }

        public sealed override ValueResult GetComparisonStrictEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (Of(b.Value == Value), null);
            }

            return (False, null);
        }

        public sealed override ValueResult GetComparisonStrictNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (Of(b.Value != Value), null);
            }

            return (True, null);
        }

        public sealed override ValueResult Notted()
        {
            return (Of(!Value), null);
        }

        public sealed override bool IsTrue() => Value;
        public sealed override string ToString() => Value ? "true" : "false";
    }
}

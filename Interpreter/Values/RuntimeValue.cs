using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Values
{
    public abstract class RuntimeValue
    {
        public Position PositionStart { get; set; }
        public Position PositionEnd { get; set; }
        public Context Context { get; set; }
        public VariableDeclarationType VariableDeclarationType { get; set; } = VariableDeclarationType.VARIABLE;

        public RuntimeValue SetPos(Position positionStart, Position positionEnd)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
            return this;
        }

        public RuntimeValue SetContext(Context context)
        {
            Context = context;
            return this;
        }

        public virtual (RuntimeValue?, Error?) AddedTo(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) SubbedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) MultedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) DivedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) PowedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) ModuledBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) AndedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) OredBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) Notted() => (null, IllegalOperation(this));
        public virtual (RuntimeValue?, Error?) BitwiseNotted() => (null, IllegalOperation(this));

        public virtual RuntimeResult Execute(List<RuntimeValue> args)
        {
            return new RuntimeResult().Failure(IllegalOperation());
        }

        public abstract RuntimeValue Copy();

        public virtual bool IsTrue() => false;

        public Error IllegalOperation(RuntimeValue? other = null)
        {
            if (other == null) other = this;
            return new RuntimeError(PositionStart, other.PositionEnd, "Illegal operation", Context);
        }
    }
}
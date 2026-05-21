using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class SuperProxyValue : RuntimeValue
    {
        public ClassInstanceValue Instance { get; }
        public ClassTypeValue CurrentClass { get; }
        public ClassTypeValue? BaseClass => CurrentClass.BaseClass;

        public override RuntimeValueType Type => RuntimeValueType.Super;
        public override bool IsCopy => false;

        public SuperProxyValue(ClassInstanceValue instance, ClassTypeValue currentClass)
        {
            Instance = instance;
            CurrentClass = currentClass;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            if (BaseClass == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"Class '{CurrentClass.ClassName}' has no base class", Context));

            if (Context == null || !Context.IsInConstructor)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, "'super(...)' can only be used inside a constructor", Context));

            var ctor = BaseClass.ResolveOwnConstructor(positionalArgs, namedArgs);
            if (ctor == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching base constructor found for '{BaseClass.ClassName}'", Context));

            var boundCtor = (BoundClassMethodValue) new BoundClassMethodValue(BaseClass, Instance, ctor, false)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            return await boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
        }

        public override RuntimeValue Copy()
            => new SuperProxyValue(Instance, CurrentClass)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => "<super>";
    }
}
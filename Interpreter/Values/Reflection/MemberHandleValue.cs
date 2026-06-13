using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Values.Reflection
{
    public enum MemberKind : byte { Method, Field, Property, Unknown }

    // A first-class, AOT-safe handle to a member of a type — the
    // MethodInfo/FieldInfo of Ra. Unlike the string-based reflection builtins
    // (methods_of/fields_of return names), a handle bundles the owner type, the
    // member name, its kind and its accessibility/static flags into one value
    // that can be stored, passed around and used (invoked / get / set) without
    // re-specifying the type or re-doing a fragile string lookup at each step.
    //
    // No reflection / codegen: the handle holds a reference to the already-built
    // type value plus precomputed metadata, so it is NativeAOT-clean and cheap.
    public sealed class MemberHandleValue : RuntimeValue
    {
        public RuntimeValue Owner { get; }       // the declaring type value
        public string OwnerName { get; }
        public string MemberName { get; }
        public MemberKind Kind { get; }
        public bool IsStatic { get; }
        public bool IsPublic { get; }

        public override RuntimeValueType Type => RuntimeValueType.MemberHandle;
        public override bool IsCopy => false;

        public MemberHandleValue(RuntimeValue owner, string ownerName, string memberName,
            MemberKind kind, bool isStatic, bool isPublic)
        {
            Owner = owner;
            OwnerName = ownerName;
            MemberName = memberName;
            Kind = kind;
            IsStatic = isStatic;
            IsPublic = isPublic;
        }

        public override RuntimeValue Copy() => this;
        public override bool IsTrue() => true;

        public override string ToString() => $"<{Kind.ToString().ToLowerInvariant()} {OwnerName}.{MemberName}>";

        public override bool Equals(object? obj) =>
            obj is MemberHandleValue m
            && ReferenceEquals(m.Owner, Owner)
            && string.Equals(m.MemberName, MemberName, System.StringComparison.Ordinal)
            && m.Kind == Kind;

        public override int GetHashCode() => System.HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Owner), MemberName, (int)Kind);
    }
}

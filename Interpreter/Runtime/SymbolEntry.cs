using RaLanguage.Interpreter.Values;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolEntry
    {
        public RuntimeValue Value { get; set; }
        public bool IsLet { get; set; }
        public bool IsMoved { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStaticallyTyped { get; set; }
        public TypeDescriptor? DeclaredType { get; set; }

        public SymbolEntry(
            RuntimeValue value,
            bool isLet = false,
            bool isPublic = true,
            TypeDescriptor? declaredType = null,
            bool isStaticallyTyped = false)
        {
            Value = value;
            IsLet = isLet;
            IsMoved = false;
            IsPublic = isPublic;
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
        }

        public SymbolEntry(RuntimeValue value, bool isLet, TypeDescriptor? declaredType, bool isStaticallyTyped)
            : this(value, isLet)
        {
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
        }

        public void ClearReference()
        {
            Value = null;
            IsLet = false;
            IsMoved = false;
            DeclaredType = null;
            IsStaticallyTyped = false;
        }
    }
}
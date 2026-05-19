using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
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
        public VariableDeclarationType DeclarationType { get; set; } = VariableDeclarationType.VARIABLE;

        public SymbolEntry(
            RuntimeValue value,
            bool isLet = false,
            bool isPublic = true,
            TypeDescriptor? declaredType = null,
            bool isStaticallyTyped = false,
            VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            Value = value;
            IsLet = isLet;
            IsMoved = false;
            IsPublic = isPublic;
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
            DeclarationType = declarationType;
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
            DeclarationType = VariableDeclarationType.VARIABLE;
        }
    }
}
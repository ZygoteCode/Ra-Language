using RaLanguage.Interpreter.Values;

namespace RaLanguage.Types
{
    public static class TypeSystem
    {
        public static bool IsAssignable(TypeDescriptor expected, RuntimeValue value)
        {
            if (expected.IsBuiltIn)
            {
                switch (expected.BuiltIn)
                {
                    case BuiltInType.Number:
                        return value.Type == RuntimeValueType.Number;
                    case BuiltInType.Boolean:
                        return value.Type == RuntimeValueType.Boolean;
                    case BuiltInType.String:
                        return value.Type == RuntimeValueType.String;
                    case BuiltInType.List:
                        return value.Type == RuntimeValueType.List;
                    case BuiltInType.Set:
                        return value.Type == RuntimeValueType.Set;
                    case BuiltInType.Map:
                        return value.Type == RuntimeValueType.Map;
                    case BuiltInType.Tuple:
                        return value.Type == RuntimeValueType.Tuple;
                    case BuiltInType.Any:
                        return true;
                    default:
                        return false;
                }
            }
            else
            {
                return value.Type.ToString().Equals(expected.NamedType, StringComparison.OrdinalIgnoreCase)
                       || expected.NamedType!.Equals("any", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
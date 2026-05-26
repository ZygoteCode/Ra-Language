using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;

namespace RaLanguage.Utilities
{
    public static class StringConversionUtility
    {
        public static string ConvertToString(RuntimeValue value)
        {
            if (value == null)
                return "null";

            if (value.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)value;
                return instance.TryCallToString().value;
            }

            if (value.Type == RuntimeValueType.StructInstance || value.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)value;
                return instance.TryCallToString().value;
            }

            return value.ToString() ?? "";
        }
    }
}

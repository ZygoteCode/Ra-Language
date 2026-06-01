using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared enum-registration logic, factored so the IR-lowered OP_DEFINE_TYPE
    // handler and the visitor fallback build a byte-identical EnumTypeValue.
    // The collision check stays in each caller (the visitor must keep it BEFORE
    // evaluating variant value expressions, to preserve side-effect ordering);
    // this helper just constructs + registers the already-resolved variants.
    public static class EnumDefOps
    {
        public static EnumTypeValue BuildAndRegister(
            string name,
            List<EnumVariantInfo> variants,
            List<string> generics,
            List<WhereConstraintNode> constraints,
            Context ctx,
            Position posStart,
            Position posEnd)
        {
            var built = new EnumTypeValue(name, variants, generics, constraints)
                .SetContext(ctx)
                .SetPos(posStart, posEnd);
            ctx.SymbolTable.Set(name, built);
            return (EnumTypeValue)built;
        }

        // Narrow, side-effect-free integer extraction for the COMPILE-TIME fold
        // of an enum variant's value literal. Returns true only for the plain
        // integer cases (`Red = 5`, `= 100L`, `= 0xFF`); every exotic / lossy /
        // non-numeric case returns false so IrCompiler falls back to the visitor,
        // whose ExtractEnumInt128 surfaces the precise diagnostic. The value
        // produced here MUST match the visitor's for the shared cases — both run
        // the same NumberNodeVisitor.ParseLiteral on the same literal, then this.
        public static bool TryExtractInt128(RuntimeValue value, out Int128 result)
        {
            switch (value.Type)
            {
                case RuntimeValueType.Integer:
                    result = ((IntegerValue)value).Value; return true;
                case RuntimeValueType.Long:
                    result = ((LongValue)value).Value; return true;
                case RuntimeValueType.Int128:
                    result = ((Int128Value)value).Value; return true;
                case RuntimeValueType.Number:
                    if (Int128.TryParse(((NumberValue)value).Value.ToString(), out result)) return true;
                    result = 0; return false;
                default:
                    result = 0; return false;
            }
        }
    }
}

using RaLanguage.Interpreter.Runtime.Async;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class BuiltInValidators
    {
        private static readonly HashSet<AnnotationTargetKind> ValueTargets = new()
        {
            AnnotationTargetKind.Parameter,
            AnnotationTargetKind.Variable,
            AnnotationTargetKind.Field,
            AnnotationTargetKind.StaticField
        };

        public static IEnumerable<AnnotationTypeValue> CreateAll()
        {
            yield return Make_Min();
            yield return Make_Max();
            yield return Make_Range();
            yield return Make_NotNull();
            yield return Make_NotEmpty();
            yield return Make_Length();
            yield return Make_Pattern();
            yield return Make_Positive();
            yield return Make_NonNegative();
            yield return Make_OneOf();
            yield return Make_Predicate();
            yield return Make_And();
            yield return Make_Or();
            yield return Make_When();
            yield return Make_SameAs();
            yield return Make_Chain();
            yield return Make_PositiveInt();
            yield return Make_NegativeInt();
            yield return Make_Email();
            yield return Make_Url();
            yield return Make_Uuid();
            yield return Make_IpAddress();
        }

        private static AnnotationTypeValue Make_Chain()
        {
            var ann = new AnnotationTypeValue(
                "chain",
                true,
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("steps"), null, null, true)
                },
                isBuiltIn: true)
            {
                AllowedTargets = new HashSet<AnnotationTargetKind>(ValueTargets),
                IsRepeatable = true,
                ValidatorMessageTemplate = "{subject}: @chain step failed"
            };

            ann.BuiltInCoercer = (instance, value, ctx) =>
            {
                var current = value;
                foreach (var step in instance.PositionalArgs)
                {
                    if (step is not AnnotationInstanceValue inner) continue;
                    if (inner.Definition.HasCoercion)
                    {
                        var (newVal, cerr) = AnnotationValidator.CoerceWithAnnotation(inner, current, "chain step", ctx);
                        if (cerr != null) return (null, cerr.Details);
                        if (newVal != null) current = newVal;
                    }
                    if (inner.Definition.HasValueValidation)
                    {
                        var verr = AnnotationValidator.Validate(inner, current, "chain step", ctx);
                        if (verr != null) return (null, verr.Details);
                    }
                }
                return (current, null);
            };

            return ann;
        }

        private static AnnotationTypeValue Make_PositiveInt() => BuildAlias(
            "positive_int",
            (ann, v, c) =>
            {
                if (!TryGetDouble(v, out double n)) return (false, "{subject}: @positive_int requires numeric, got non-numeric");
                if (n != System.Math.Truncate(n)) return (false, "{subject}: @positive_int requires integer, got {value}");
                if (n <= 0) return (false, "{subject}: @positive_int requires > 0, got {value}");
                return (true, null);
            },
            "{subject}: @positive_int failed");

        private static AnnotationTypeValue Make_NegativeInt() => BuildAlias(
            "negative_int",
            (ann, v, c) =>
            {
                if (!TryGetDouble(v, out double n)) return (false, "{subject}: @negative_int requires numeric");
                if (n != System.Math.Truncate(n)) return (false, "{subject}: @negative_int requires integer");
                if (n >= 0) return (false, "{subject}: @negative_int requires < 0, got {value}");
                return (true, null);
            },
            "{subject}: @negative_int failed");

        private static AnnotationTypeValue Make_Email() => BuildAlias(
            "email",
            (ann, v, c) =>
            {
                if (v is not StringValue sv) return (false, "{subject}: @email requires string");
                var pattern = new Regex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$");
                if (!pattern.IsMatch(sv.Value ?? string.Empty))
                    return (false, "{subject}: value '{value}' is not a valid email");
                return (true, null);
            },
            "{subject}: invalid email");

        private static AnnotationTypeValue Make_Url() => BuildAlias(
            "url",
            (ann, v, c) =>
            {
                if (v is not StringValue sv) return (false, "{subject}: @url requires string");
                var pattern = new Regex(@"^https?://[^\s/$.?#].[^\s]*$", RegexOptions.IgnoreCase);
                if (!pattern.IsMatch(sv.Value ?? string.Empty))
                    return (false, "{subject}: value '{value}' is not a valid URL");
                return (true, null);
            },
            "{subject}: invalid URL");

        private static AnnotationTypeValue Make_Uuid() => BuildAlias(
            "uuid",
            (ann, v, c) =>
            {
                if (v is not StringValue sv) return (false, "{subject}: @uuid requires string");
                var pattern = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
                if (!pattern.IsMatch(sv.Value ?? string.Empty))
                    return (false, "{subject}: value '{value}' is not a valid UUID");
                return (true, null);
            },
            "{subject}: invalid UUID");

        private static AnnotationTypeValue Make_IpAddress() => BuildAlias(
            "ip_address",
            (ann, v, c) =>
            {
                if (v is not StringValue sv) return (false, "{subject}: @ip_address requires string");
                if (!System.Net.IPAddress.TryParse(sv.Value, out _))
                    return (false, "{subject}: value '{value}' is not a valid IP address");
                return (true, null);
            },
            "{subject}: invalid IP address");

        private static AnnotationTypeValue BuildAlias(
            string name,
            System.Func<AnnotationInstanceValue, RuntimeValue, Context, (bool ok, string? msg)> validator,
            string defaultMessage)
        {
            return Build(name, new List<AnnotationParameterNode>(), validator, defaultMessage);
        }

        private static Token Tok(string name) => new Token(TokenType.IDENTIFIER, name, new Position(0, 0, 0, "<builtin>", string.Empty));
        private static AstNode NullDefault() => new Parser.Nodes.Primitives.NullNode(new Token(TokenType.KEYWORD, Keyword.Null, new Position(0, 0, 0, "<builtin>", string.Empty)));

        private static AnnotationTypeValue Build(
            string name,
            List<AnnotationParameterNode> parameters,
            System.Func<AnnotationInstanceValue, RuntimeValue, Context, (bool ok, string? msg)> validator,
            string defaultMessage)
        {
            var ann = new AnnotationTypeValue(name, true, parameters, isBuiltIn: true)
            {
                AllowedTargets = new HashSet<AnnotationTargetKind>(ValueTargets),
                IsRepeatable = false,
                BuiltInValueValidator = validator,
                ValidatorMessageTemplate = defaultMessage
            };
            return ann;
        }

        private static AnnotationTypeValue Make_Min()
        {
            return Build(
                "min",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("value"), new TypeDescriptor("int"), null, false)
                },
                (ann, value, ctx) =>
                {
                    if (!TryGetDouble(value, out double v)) return (true, null);
                    if (!TryGetDouble(ann.Get("value"), out double bound)) return (true, null);
                    if (v < bound) return (false, $"{{subject}}: value {{value}} is less than minimum {bound} required by @min");
                    return (true, null);
                },
                "{subject}: value {value} violates @min({value})");
        }

        private static AnnotationTypeValue Make_Max()
        {
            return Build(
                "max",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("value"), new TypeDescriptor("int"), null, false)
                },
                (ann, value, ctx) =>
                {
                    if (!TryGetDouble(value, out double v)) return (true, null);
                    if (!TryGetDouble(ann.Get("value"), out double bound)) return (true, null);
                    if (v > bound) return (false, $"{{subject}}: value {{value}} is greater than maximum {bound} required by @max");
                    return (true, null);
                },
                "{subject}: value {value} violates @max");
        }

        private static AnnotationTypeValue Make_Range()
        {
            return Build(
                "range",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("min"), new TypeDescriptor("int"), null, false),
                    new AnnotationParameterNode(Tok("max"), new TypeDescriptor("int"), null, false)
                },
                (ann, value, ctx) =>
                {
                    if (!TryGetDouble(value, out double v)) return (true, null);
                    if (!TryGetDouble(ann.Get("min"), out double lo)) return (true, null);
                    if (!TryGetDouble(ann.Get("max"), out double hi)) return (true, null);
                    if (v < lo || v > hi)
                        return (false, $"{{subject}}: value {{value}} not in [{lo}, {hi}]");
                    return (true, null);
                },
                "{subject}: value {value} outside @range");
        }

        private static AnnotationTypeValue Make_NotNull()
        {
            return Build(
                "not_null",
                new List<AnnotationParameterNode>(),
                (ann, value, ctx) =>
                {
                    bool isNull = value == null || value.Type == RuntimeValueType.Null;
                    if (isNull) return (false, "{subject}: must not be null (@not_null)");
                    return (true, null);
                },
                "{subject}: must not be null");
        }

        private static AnnotationTypeValue Make_NotEmpty()
        {
            return Build(
                "not_empty",
                new List<AnnotationParameterNode>(),
                (ann, value, ctx) =>
                {
                    if (value is StringValue sv) return (!string.IsNullOrEmpty(sv.Value), "{subject}: string must not be empty (@not_empty)");
                    if (value is ListValue lv) return (lv.Elements.Count > 0, "{subject}: list must not be empty (@not_empty)");
                    if (value is SetValue setv) return (setv.Elements.Count > 0, "{subject}: set must not be empty (@not_empty)");
                    if (value is MapValue mv) return (mv.Pairs.Count > 0, "{subject}: map must not be empty (@not_empty)");
                    return (true, null);
                },
                "{subject}: must not be empty");
        }

        private static AnnotationTypeValue Make_Length()
        {
            return Build(
                "length",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("min"), new TypeDescriptor("int"), MakeIntDefault(0), false),
                    new AnnotationParameterNode(Tok("max"), new TypeDescriptor("int"), MakeIntDefault(int.MaxValue), false)
                },
                (ann, value, ctx) =>
                {
                    int len = -1;
                    if (value is StringValue sv) len = sv.Value?.Length ?? 0;
                    else if (value is ListValue lv) len = lv.Elements.Count;
                    else if (value is SetValue setv) len = setv.Elements.Count;
                    else if (value is MapValue mv) len = mv.Pairs.Count;
                    if (len < 0) return (true, null);

                    int lo = 0, hi = int.MaxValue;
                    if (TryGetDouble(ann.Get("min"), out double mn)) lo = (int)mn;
                    if (TryGetDouble(ann.Get("max"), out double mx)) hi = (int)mx;
                    if (len < lo || len > hi)
                        return (false, $"{{subject}}: length {len} outside [{lo}, {hi}] (@length)");
                    return (true, null);
                },
                "{subject}: length violates @length");
        }

        private static AnnotationTypeValue Make_Pattern()
        {
            return Build(
                "pattern",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("regex"), new TypeDescriptor("string"), null, false)
                },
                (ann, value, ctx) =>
                {
                    if (value is not StringValue sv) return (true, null);
                    var regex = ann.Get("regex") as StringValue;
                    if (regex == null) return (true, null);
                    try
                    {
                        var re = new Regex(regex.Value);
                        if (!re.IsMatch(sv.Value ?? string.Empty))
                            return (false, $"{{subject}}: value {{value}} does not match pattern '{regex.Value}' (@pattern)");
                    }
                    catch (System.ArgumentException ex)
                    {
                        return (false, $"@pattern regex is invalid: {ex.Message}");
                    }
                    return (true, null);
                },
                "{subject}: pattern mismatch");
        }

        private static AnnotationTypeValue Make_Positive()
        {
            return Build(
                "positive",
                new List<AnnotationParameterNode>(),
                (ann, value, ctx) =>
                {
                    if (!TryGetDouble(value, out double v)) return (true, null);
                    if (v <= 0) return (false, "{subject}: value {value} must be > 0 (@positive)");
                    return (true, null);
                },
                "{subject}: must be positive");
        }

        private static AnnotationTypeValue Make_NonNegative()
        {
            return Build(
                "non_negative",
                new List<AnnotationParameterNode>(),
                (ann, value, ctx) =>
                {
                    if (!TryGetDouble(value, out double v)) return (true, null);
                    if (v < 0) return (false, "{subject}: value {value} must be >= 0 (@non_negative)");
                    return (true, null);
                },
                "{subject}: must be non-negative");
        }

        private static AnnotationTypeValue Make_OneOf()
        {
            return Build(
                "one_of",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("values"), null, null, true)
                },
                (ann, value, ctx) =>
                {
                    var allowed = ann.PositionalArgs;
                    if (allowed.Count == 0) return (true, null);
                    foreach (var a in allowed)
                    {
                        if (a is ListValue lv)
                        {
                            foreach (var e in lv.Elements)
                                if (RuntimeEqual(value, e)) return (true, null);
                            continue;
                        }
                        if (RuntimeEqual(value, a)) return (true, null);
                    }
                    return (false, "{subject}: value {value} not in allowed set (@one_of)");
                },
                "{subject}: value not allowed");
        }

        private static AnnotationTypeValue Make_And()
        {
            var ann = Build(
                "all_of",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("annotations"), null, null, true)
                },
                (instance, value, ctx) =>
                {
                    foreach (var arg in instance.PositionalArgs)
                    {
                        if (arg is not AnnotationInstanceValue inner) continue;
                        var err = AnnotationValidator.Validate(inner, value, "value", ctx);
                        if (err != null) return (false, err.Details);
                    }
                    return (true, null);
                },
                "{subject}: @all_of chain failed");
            ann.IsRepeatable = false;
            return ann;
        }

        private static AnnotationTypeValue Make_Or()
        {
            var ann = Build(
                "any_of",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("annotations"), null, null, true)
                },
                (instance, value, ctx) =>
                {
                    if (instance.PositionalArgs.Count == 0) return (true, null);
                    var errs = new List<string>();
                    foreach (var arg in instance.PositionalArgs)
                    {
                        if (arg is not AnnotationInstanceValue inner) continue;
                        var err = AnnotationValidator.Validate(inner, value, "value", ctx);
                        if (err == null) return (true, null);
                        errs.Add(err.Details);
                    }
                    return (false, "{subject}: no @any_of branch matched: " + string.Join(" | ", errs));
                },
                "{subject}: @any_of all branches failed");
            ann.IsRepeatable = false;
            return ann;
        }

        private static AnnotationTypeValue Make_When()
        {
            var ann = Build(
                "when",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("condition"), new TypeDescriptor("string"), null, false),
                    new AnnotationParameterNode(Tok("apply"), null, NullDefault(), false)
                },
                (instance, value, ctx) =>
                {
                    var condName = (instance.Get("condition") as StringValue)?.Value;
                    if (condName == null) return (true, null);
                    var condFn = ctx.SymbolTable.Get(condName);
                    if (condFn is not Values.Functions.BaseFunctionValue bfn)
                        return (false, $"@when condition '{condName}' is not a defined function");
                    var execRes = SyncAwait.Get(bfn.Execute(new List<RuntimeValue> { value }));
                    if (execRes.Error != null) return (false, execRes.Error.Details);
                    bool gate = execRes.Value switch
                    {
                        BooleanValue bv => bv.Value,
                        NumberValue nv => !nv.Value.IsZero(),
                        IntegerValue iv => iv.Value != 0,
                        NullValue => false,
                        _ => true
                    };
                    if (!gate) return (true, null);
                    var apply = instance.Get("apply");
                    if (apply is AnnotationInstanceValue inner)
                    {
                        var err = AnnotationValidator.Validate(inner, value, "value", ctx);
                        if (err != null) return (false, err.Details);
                    }
                    return (true, null);
                },
                "{subject}: @when validation failed");
            ann.IsRepeatable = true;
            return ann;
        }

        private static AnnotationTypeValue Make_SameAs()
        {
            return Build(
                "same_as",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("field"), new TypeDescriptor("string"), null, false)
                },
                (instance, value, ctx) =>
                {
                    var fieldName = (instance.Get("field") as StringValue)?.Value;
                    if (fieldName == null) return (true, null);
                    var other = ctx.SymbolTable.Get(fieldName);
                    if (other == null) return (false, $"{{subject}}: @same_as('{fieldName}') target not found in scope");
                    if (RuntimeEqual(value, other)) return (true, null);
                    return (false, $"{{subject}}: value {{value}} must equal '{fieldName}' (={other})");
                },
                "{subject}: @same_as failed");
        }

        private static AnnotationTypeValue Make_Predicate()
        {
            return Build(
                "predicate",
                new List<AnnotationParameterNode>
                {
                    new AnnotationParameterNode(Tok("check"), new TypeDescriptor("string"), null, false),
                    new AnnotationParameterNode(Tok("message"), new TypeDescriptor("string"), NullDefault(), false)
                },
                (ann, value, ctx) =>
                {
                    var fnName = ann.Get("check") as StringValue;
                    if (fnName == null) return (true, null);
                    var fnSymbol = ctx.SymbolTable.Get(fnName.Value);
                    if (fnSymbol is not Values.Functions.BaseFunctionValue bfn)
                        return (false, $"@predicate function '{fnName.Value}' not defined");
                    var execRes = SyncAwait.Get(bfn.Execute(new List<RuntimeValue> { value, ann }));
                    if (execRes.Error != null) return (false, execRes.Error.Details);
                    bool ok = execRes.Value switch
                    {
                        BooleanValue bv => bv.Value,
                        NumberValue nv => !nv.Value.IsZero(),
                        IntegerValue iv => iv.Value != 0,
                        NullValue => false,
                        _ => true
                    };
                    if (!ok)
                    {
                        var msg = ann.Get("message") as StringValue;
                        return (false, msg?.Value ?? $"{{subject}}: value {{value}} failed @predicate({fnName.Value})");
                    }
                    return (true, null);
                },
                "{subject}: predicate failed");
        }

        private static bool TryGetDouble(RuntimeValue? value, out double result)
        {
            result = 0;
            if (value == null) return false;
            switch (value)
            {
                case NumberValue nv:
                    try { result = (double)nv.Value.ToBigInteger(); return true; }
                    catch { try { result = double.Parse(nv.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture); return true; } catch { return false; } }
                case IntegerValue iv: result = iv.Value; return true;
                case LongValue lv: result = lv.Value; return true;
                case ShortValue sv: result = sv.Value; return true;
                case ByteValue bv: result = bv.Value; return true;
                case UnsignedIntegerValue uiv: result = uiv.Value; return true;
                case UnsignedLongValue ulv: result = ulv.Value; return true;
                case UnsignedShortValue usv: result = usv.Value; return true;
                case FloatValue fv: result = fv.Value; return true;
                case DoubleValue dv: result = dv.Value; return true;
                case DecimalValue mv: result = (double)mv.Value; return true;
                default: return false;
            }
        }

        private static bool RuntimeEqual(RuntimeValue a, RuntimeValue b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            var (res, err) = a.GetComparisonStrictEq(b);
            if (err != null || res == null) return false;
            return res is BooleanValue bv && bv.Value;
        }

        private static AstNode MakeIntDefault(int v)
        {
            var pos = new Position(0, 0, 0, "<builtin>", string.Empty);
            var tok = new Token(TokenType.INT, v.ToString(System.Globalization.CultureInfo.InvariantCulture), pos);
            return new Parser.Nodes.Primitives.NumberNode(tok);
        }
    }
}

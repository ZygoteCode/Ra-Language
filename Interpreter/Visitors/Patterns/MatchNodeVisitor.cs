using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Patterns;

namespace RaLanguage.Interpreter.Visitors.Patterns
{
    // Match expression evaluator.
    //
    // Semantics (mirrors Rust / Swift / F#):
    //   * scrutinee evaluated exactly once;
    //   * arms tried top-to-bottom;
    //   * for each arm, pattern is matched against scrutinee in a fresh child
    //     scope (bindings introduced by the pattern do not leak);
    //   * guard, if present, evaluated AFTER bindings (it can see them);
    //   * first matching arm's body runs; its value is the match value.
    //
    // Pattern walking is in-place, recursive, and allocation-light. Bindings
    // are committed to the arm scope only when the entire pattern matches.
    // This avoids leaking partial state when a deeper sub-pattern fails.
    public class MatchNodeVisitor : NodeVisitor<MatchNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(MatchNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(MatchNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var scrutinee = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Scrutinee, context, interpreter));
            if (res.ShouldReturn()) return res;
            if (scrutinee == null)
            {
                return res.Failure(new RuntimeError(node.Scrutinee.PositionStart, node.Scrutinee.PositionEnd,
                    "match scrutinee evaluated to no value",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "expected a concrete value",
                    help: "ensure the expression before '{' in 'match ... {' returns a value"));
            }

            foreach (var arm in node.Arms)
            {
                var armCtx = new Context(context.DisplayName, context, arm.PositionStart);
                armCtx.SymbolTable = new SymbolTable(context.SymbolTable);

                var bindings = new List<(string Name, RuntimeValue Value)>();
                var matchRes = TryMatch(arm.Pattern, scrutinee, context, bindings, out var error);
                if (error != null) return res.Failure(error);
                if (!matchRes) continue;

                foreach (var (name, value) in bindings)
                    armCtx.SymbolTable.SetLocal(name, value);

                if (arm.Guard != null)
                {
                    var guardRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(arm.Guard, armCtx, interpreter);
                    if (guardRes.Error != null) return res.Failure(guardRes.Error);
                    if (guardRes.FuncReturnValue != null || guardRes.LoopShouldBreak || guardRes.LoopShouldContinue)
                    {
                        // Guards must be pure boolean expressions. Forwarding
                        // a return / break / continue out of a guard would
                        // silently swallow the match — disallow.
                        return res.Failure(new RuntimeError(arm.Guard.PositionStart, arm.Guard.PositionEnd,
                            "guard expression cannot contain control flow",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "return / break / continue not allowed here",
                            help: "use a plain boolean expression in 'if <guard>'"));
                    }
                    if (guardRes.Value == null || !guardRes.Value.IsTrue())
                        continue;
                }

                var bodyRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(arm.Body, armCtx, interpreter);
                // Propagate everything (return / break / continue / yield).
                if (bodyRes.Error != null) return res.Failure(bodyRes.Error);
                if (bodyRes.FuncReturnValue != null) return res.SuccessReturn(bodyRes.FuncReturnValue);
                if (bodyRes.LoopShouldBreak) return res.SuccessBreak();
                if (bodyRes.LoopShouldContinue) return res.SuccessContinue();
                if (bodyRes.ShouldYield) return res.SuccessYield(bodyRes.YieldValue ?? NullValue.Null);
                return res.Success(bodyRes.Value ?? NullValue.Null);
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                "no match arm covered the scrutinee value",
                context,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "match was not exhaustive at runtime",
                help: "add a fallback arm 'case _ -> ...' or cover the missing variants"));
        }

        // --------------------------------------------------------------
        // Pattern engine
        // --------------------------------------------------------------

        // Public entry point so other visitors (destructuring let, if-let
        // sugar etc.) can drive the engine against an arbitrary scrutinee
        // value without duplicating the dispatch table.
        internal static bool TryMatch(PatternNode pattern, RuntimeValue scrutinee, Context context,
                                      List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            switch (pattern)
            {
                case WildcardPatternNode _:
                    return true;

                case VariablePatternNode vp:
                    return TryMatchVariableOrZeroArityVariant(vp, scrutinee, context, bindings, out error);

                case LiteralPatternNode lp:
                {
                    // Literal expression is evaluated in a fresh context with
                    // calls blocked — pattern literals must be pure constants.
                    // We delegate to the standard equality operator.
                    return TryMatchLiteral(lp, scrutinee, context, out error);
                }

                case VariantPatternNode vap:
                    return TryMatchVariant(vap, scrutinee, context, bindings, out error);

                case TuplePatternNode tp:
                    return TryMatchTuple(tp, scrutinee, context, bindings, out error);

                case ListPatternNode lp2:
                    return TryMatchList(lp2, scrutinee, context, bindings, out error);

                case StructPatternNode sp:
                    return TryMatchStruct(sp, scrutinee, context, bindings, out error);

                case TypePatternNode tpn:
                    return TryMatchTypePattern(tpn, scrutinee, context, bindings);

                case OrPatternNode opn:
                    return TryMatchOr(opn, scrutinee, context, bindings, out error);

                case AndPatternNode andn:
                    return TryMatchAnd(andn, scrutinee, context, bindings, out error);

                case NotPatternNode notn:
                    return TryMatchNot(notn, scrutinee, context, bindings, out error);

                case AliasPatternNode apn:
                    return TryMatchAlias(apn, scrutinee, context, bindings, out error);

                case RangePatternNode rpn:
                    return TryMatchRange(rpn, scrutinee, context, out error);

                case RelationalPatternNode rop:
                    return TryMatchRelational(rop, scrutinee, context, out error);

                case MapPatternNode mp:
                    return TryMatchMap(mp, scrutinee, context, bindings, out error);

                case RestPatternNode _:
                    error = new RuntimeError(pattern.PositionStart, pattern.PositionEnd,
                        "'..' rest pattern only valid inside a list pattern",
                        context, code: DiagnosticCode.RuntimeTypeMismatch);
                    return false;

                default:
                    error = new RuntimeError(pattern.PositionStart, pattern.PositionEnd,
                        "unsupported pattern kind",
                        context, code: DiagnosticCode.RuntimeTypeMismatch);
                    return false;
            }
        }

        // Disjunction. Each alternative is tried in source order. Bindings
        // pushed by a failing alternative are rolled back before the next
        // one is tried, so the alternatives are isolated from each other.
        private static bool TryMatchOr(OrPatternNode op, RuntimeValue scrutinee, Context context,
                                       List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            int snapshot = bindings.Count;
            for (int i = 0; i < op.Alternatives.Count; i++)
            {
                bindings.RemoveRange(snapshot, bindings.Count - snapshot);
                if (TryMatch(op.Alternatives[i], scrutinee, context, bindings, out error)) return true;
                if (error != null) return false;
            }
            bindings.RemoveRange(snapshot, bindings.Count - snapshot);
            return false;
        }

        // 'P as n' alias trailer. Try the inner pattern; on success, also
        // bind the whole scrutinee at this position to the alias name.
        private static bool TryMatchAlias(AliasPatternNode ap, RuntimeValue scrutinee, Context context,
                                          List<(string, RuntimeValue)> bindings, out Error? error)
        {
            if (!TryMatch(ap.Inner, scrutinee, context, bindings, out error)) return false;
            bindings.Add((ap.BinderName, scrutinee));
            return true;
        }

        // Conjunction. All conjuncts must succeed. Bindings from each
        // conjunct accumulate into the shared list; if any conjunct fails
        // the prior accumulated bindings (from this AndPattern only) are
        // rolled back to the snapshot taken on entry.
        private static bool TryMatchAnd(AndPatternNode ap, RuntimeValue scrutinee, Context context,
                                        List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            int snapshot = bindings.Count;
            foreach (var conj in ap.Conjuncts)
            {
                if (!TryMatch(conj, scrutinee, context, bindings, out error))
                {
                    bindings.RemoveRange(snapshot, bindings.Count - snapshot);
                    return false;
                }
                if (error != null)
                {
                    bindings.RemoveRange(snapshot, bindings.Count - snapshot);
                    return false;
                }
            }
            return true;
        }

        // Negation. Tries the inner pattern in an isolated bindings
        // sandbox (so any speculative bindings the inner pattern produced
        // are discarded). Succeeds iff the inner pattern fails.
        private static bool TryMatchNot(NotPatternNode np, RuntimeValue scrutinee, Context context,
                                        List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            var sink = new List<(string, RuntimeValue)>();
            bool innerOk = TryMatch(np.Inner, scrutinee, context, sink, out error);
            if (error != null) return false;
            return !innerOk;
        }

        // Range pattern: numeric/string-ordered bounded match.
        //   Lo..Hi   -> Lo <= scrutinee < Hi
        //   Lo..=Hi  -> Lo <= scrutinee <= Hi
        //   Lo..     -> scrutinee >= Lo
        //   ..Hi     -> scrutinee < Hi
        //   ..=Hi    -> scrutinee <= Hi
        private static bool TryMatchRange(RangePatternNode rp, RuntimeValue scrutinee, Context context, out Error? error)
        {
            error = null;
            if (rp.Lo != null)
            {
                var loVal = EvaluatePatternLiteral(rp.Lo, context);
                if (loVal == null)
                {
                    error = new RuntimeError(rp.PositionStart, rp.PositionEnd,
                        "range pattern low bound must be a numeric or string literal",
                        context, code: DiagnosticCode.RuntimeTypeMismatch);
                    return false;
                }
                var (gteVal, gteErr) = scrutinee.GetComparisonGte(loVal);
                if (gteErr != null) { error = gteErr; return false; }
                if (!(gteVal is BooleanValue gb && gb.Value)) return false;
            }
            if (rp.Hi != null)
            {
                var hiVal = EvaluatePatternLiteral(rp.Hi, context);
                if (hiVal == null)
                {
                    error = new RuntimeError(rp.PositionStart, rp.PositionEnd,
                        "range pattern high bound must be a numeric or string literal",
                        context, code: DiagnosticCode.RuntimeTypeMismatch);
                    return false;
                }
                if (rp.IsInclusive)
                {
                    var (lteVal, lteErr) = scrutinee.GetComparisonLte(hiVal);
                    if (lteErr != null) { error = lteErr; return false; }
                    if (!(lteVal is BooleanValue lb && lb.Value)) return false;
                }
                else
                {
                    var (ltVal, ltErr) = scrutinee.GetComparisonLt(hiVal);
                    if (ltErr != null) { error = ltErr; return false; }
                    if (!(ltVal is BooleanValue lb && lb.Value)) return false;
                }
            }
            return true;
        }

        // Comparison against a single literal at pattern position.
        private static bool TryMatchRelational(RelationalPatternNode rp, RuntimeValue scrutinee, Context context, out Error? error)
        {
            error = null;
            var operand = EvaluatePatternLiteral(rp.Operand, context);
            if (operand == null)
            {
                error = new RuntimeError(rp.PositionStart, rp.PositionEnd,
                    "relational pattern operand must be a numeric, string, boolean or null literal",
                    context, code: DiagnosticCode.RuntimeTypeMismatch);
                return false;
            }

            ValueResult cmp = rp.Op switch
            {
                Lexer.Tokens.TokenType.LT => scrutinee.GetComparisonLt(operand),
                Lexer.Tokens.TokenType.LTE => scrutinee.GetComparisonLte(operand),
                Lexer.Tokens.TokenType.GT => scrutinee.GetComparisonGt(operand),
                Lexer.Tokens.TokenType.GTE => scrutinee.GetComparisonGte(operand),
                Lexer.Tokens.TokenType.EE => scrutinee.GetComparisonEq(operand),
                Lexer.Tokens.TokenType.NE => scrutinee.GetComparisonNe(operand),
                _ => new ValueResult(null, new RuntimeError(rp.PositionStart, rp.PositionEnd,
                    "unsupported relational operator in pattern", context, code: DiagnosticCode.RuntimeTypeMismatch))
            };
            if (cmp.Error != null) { error = cmp.Error; return false; }
            return cmp.Value is BooleanValue b && b.Value;
        }

        // Map pattern. Scrutinee must be a MapValue. Each entry's key
        // expression is evaluated as a pure literal and looked up using
        // structural equality (the same rule MapValue uses for lookups).
        // The 'HasOpenRest' flag (set when the pattern ends in '..') lets
        // the scrutinee carry extra keys; otherwise the pattern's key set
        // must equal the scrutinee's key set.
        private static bool TryMatchMap(MapPatternNode mp, RuntimeValue scrutinee, Context context,
                                        List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            if (scrutinee is not RaLanguage.Interpreter.Values.Primitives.MapValue mv) return false;

            // Optional closed-set arity check up front for fast failure
            // without walking the entries.
            if (!mp.HasOpenRest && mv.Pairs.Count != mp.Entries.Count) return false;

            for (int i = 0; i < mp.Entries.Count; i++)
            {
                var (keyExpr, valuePat) = mp.Entries[i];
                var keyVal = EvaluatePatternLiteral(keyExpr, context);
                if (keyVal == null)
                {
                    error = new RuntimeError(keyExpr.PositionStart, keyExpr.PositionEnd,
                        "map pattern key must be a numeric, string, boolean or null literal",
                        context, code: DiagnosticCode.RuntimeTypeMismatch);
                    return false;
                }

                if (!TryMapLookup(mv, keyVal, out var foundValue, out var lookupErr))
                {
                    if (lookupErr != null) { error = lookupErr; return false; }
                    return false;
                }

                if (!TryMatch(valuePat, foundValue!, context, bindings, out error)) return false;
                if (error != null) return false;
            }
            return true;
        }

        private static bool TryMapLookup(RaLanguage.Interpreter.Values.Primitives.MapValue mv,
                                         RuntimeValue key, out RuntimeValue? value, out Error? error)
        {
            value = null;
            error = null;
            for (int i = 0; i < mv.Pairs.Count; i++)
            {
                var (eqVal, eqErr) = mv.Pairs[i].Key.GetComparisonEq(key);
                if (eqErr != null) { error = eqErr; return false; }
                if (eqVal is BooleanValue b && b.Value)
                {
                    value = mv.Pairs[i].Value;
                    return true;
                }
            }
            return false;
        }

        private static bool TryMatchVariableOrZeroArityVariant(VariablePatternNode vp, RuntimeValue scrutinee, Context context,
                                                              List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;

            // Rust-style disambiguation: if the scrutinee is an enum value
            // and one of its enum type's zero-arity variants has the same
            // name as `vp.Name`, treat the pattern as a variant test (no
            // binding). Otherwise, it's a binding.
            if (scrutinee is EnumValue ev)
            {
                var typeSymbol = context.SymbolTable.Get(ev.EnumName);
                if (typeSymbol is EnumTypeValue et && et.TryGetVariant(vp.Name, out var info) && !info.HasPayload)
                {
                    return ev.MemberName == vp.Name;
                }
            }

            // Cross-check the symbol table for a globally-known zero-arity
            // variant import (rare, but lets `case None` work when the user
            // has bound the variant value into scope individually). We only
            // treat the symbol as a variant test if it is *itself* a
            // zero-arity EnumValue — otherwise an unrelated outer `let foo =
            // Some(1)` would shadow a perfectly innocent `foo` binding name
            // and refuse to bind it.
            var direct = context.SymbolTable.Get(vp.Name);
            if (direct is EnumValue eDirect && !eDirect.HasPayload)
            {
                if (scrutinee is EnumValue scrEnum && !scrEnum.HasPayload)
                    return scrEnum.EnumName == eDirect.EnumName && scrEnum.MemberName == eDirect.MemberName;
                return false;
            }

            bindings.Add((vp.Name, scrutinee));
            return true;
        }

        private static bool TryMatchLiteral(LiteralPatternNode lp, RuntimeValue scrutinee, Context context, out Error? error)
        {
            error = null;
            // Literal patterns build their own RuntimeValue via the shared
            // interpreter visitors — but since they are always pure literals
            // (NumberNode / StringNode / BooleanNode / NullNode / unary minus
            // on a number), we lower them directly to avoid the visitor round
            // trip and to keep this engine reflection-free.
            var literalValue = EvaluatePatternLiteral(lp.Expression, context);
            if (literalValue == null)
            {
                error = new RuntimeError(lp.PositionStart, lp.PositionEnd,
                    "literal pattern must be a numeric, string, boolean, or null literal",
                    context, code: DiagnosticCode.RuntimeTypeMismatch);
                return false;
            }

            // Null match: avoid routing through GetComparisonEq because most
            // primitive operators raise 'Illegal operation' against null
            // rather than returning false. Identity check is the right
            // semantics here regardless.
            if (literalValue is NullValue) return scrutinee is NullValue;
            if (scrutinee is NullValue) return false;

            var (eqVal, eqErr) = scrutinee.GetComparisonEq(literalValue);
            if (eqErr != null)
            {
                error = eqErr;
                return false;
            }
            return eqVal is BooleanValue b && b.Value;
        }

        private static RuntimeValue? EvaluatePatternLiteral(AstNode expr, Context context)
        {
            switch (expr)
            {
                case RaLanguage.Parser.Nodes.Primitives.NumberNode nn:
                {
                    var s = nn.Tok.Value?.ToString() ?? "0";
                    if (s.Contains('.') || s.Contains('e') || s.Contains('E'))
                        return new DoubleValue(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                    if (long.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var l))
                    {
                        if (l >= int.MinValue && l <= int.MaxValue) return new IntegerValue((int)l);
                        return new LongValue(l);
                    }
                    return new NumberValue(BigNumber.Parse(s));
                }
                case RaLanguage.Parser.Nodes.Primitives.StringNode sn when sn.Parts.Count == 1
                    && sn.Parts[0] is RaLanguage.Parser.Nodes.Primitives.StringTextNode stn:
                    return new StringValue(stn.Text);
                case RaLanguage.Parser.Nodes.Primitives.BooleanNode bn:
                    return BooleanValue.Of(bn.Token.Matches(Lexer.Tokens.Keyword.True));
                case RaLanguage.Parser.Nodes.Primitives.NullNode _:
                    return NullValue.Null;
                case RaLanguage.Parser.Nodes.Operations.UnaryOperationNode un when un.OpTok.Type == Lexer.Tokens.TokenType.MINUS:
                {
                    var inner = EvaluatePatternLiteral(un.Node, context);
                    if (inner is IntegerValue iv) return new IntegerValue(-iv.Value);
                    if (inner is LongValue lv) return new LongValue(-lv.Value);
                    if (inner is DoubleValue dv) return new DoubleValue(-dv.Value);
                    if (inner is NumberValue nv) return new NumberValue(new BigNumber(-nv.Value.Unscaled, nv.Value.Scale));
                    return null;
                }
                default:
                    return null;
            }
        }

        private static bool TryMatchVariant(VariantPatternNode vap, RuntimeValue scrutinee, Context context,
                                            List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;

            // Record positional destructure: `case Point(x, y) -> ...`.
            // Routes through the primary-field list of the scrutinee's
            // Definition; binds left-to-right. Cross-type patterns
            // (mismatched names) reject; nominal identity is required.
            if (scrutinee is RaLanguage.Interpreter.Values.Records.RecordInstanceValue recInst
                && vap.EnumName == null)
            {
                var sym = context.SymbolTable.Get(vap.VariantName);
                if (sym is RaLanguage.Interpreter.Values.Records.RecordTypeValue patType)
                {
                    if (!ReferenceEquals(patType, recInst.Definition)) return false;

                    var subs = vap.SubPatterns;
                    int subCount = subs?.Count ?? 0;
                    int fieldCount = recInst.Definition.PrimaryFields.Count;

                    if (subCount != fieldCount)
                    {
                        error = new RuntimeError(vap.PositionStart, vap.PositionEnd,
                            $"record '{recInst.Definition.StructName}' has {fieldCount} primary field(s), pattern destructures {subCount}",
                            context,
                            code: DiagnosticCode.RuntimeTypeMismatch,
                            primaryLabel: "arity mismatch",
                            help: $"write 'case {recInst.Definition.StructName}({string.Join(", ", new string[fieldCount].Select((_, i) => "p" + (i + 1)))})'");
                        return false;
                    }

                    if (subs == null) return true;
                    for (int i = 0; i < subs.Count; i++)
                    {
                        var fname = recInst.Definition.PrimaryFields[i].NameTok.Value?.ToString() ?? "";
                        var fval = recInst.HasField(fname)
                            ? recInst.GetField(fname)
                            : (RuntimeValue)NullValue.Null;
                        if (!TryMatch(subs[i], fval, context, bindings, out error)) return false;
                        if (error != null) return false;
                    }
                    return true;
                }
            }

            if (scrutinee is not EnumValue ev) return false;

            if (vap.EnumName != null && !string.Equals(ev.EnumName, vap.EnumName, System.StringComparison.Ordinal))
                return false;

            if (!string.Equals(ev.MemberName, vap.VariantName, System.StringComparison.Ordinal))
                return false;

            var subsE = vap.SubPatterns;
            int subCountE = subsE?.Count ?? 0;
            if (subCountE != ev.Payload.Count)
            {
                error = new RuntimeError(vap.PositionStart, vap.PositionEnd,
                    $"variant '{ev.EnumName}.{ev.MemberName}' carries {ev.Payload.Count} value(s), pattern destructures {subCountE}",
                    context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: "arity mismatch",
                    help: $"write 'case {ev.MemberName}({string.Join(", ", new string[ev.Payload.Count].Select((_, i) => "p" + (i + 1)))})'");
                return false;
            }

            if (subsE == null) return true;
            for (int i = 0; i < subsE.Count; i++)
            {
                if (!TryMatch(subsE[i], ev.Payload[i], context, bindings, out error)) return false;
                if (error != null) return false;
            }
            return true;
        }

        private static bool TryMatchTuple(TuplePatternNode tp, RuntimeValue scrutinee, Context context,
                                         List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            if (scrutinee is not TupleValue tv) return false;
            if (tv.Elements.Count != tp.Elements.Count) return false;
            for (int i = 0; i < tp.Elements.Count; i++)
            {
                if (!TryMatch(tp.Elements[i], tv.Elements[i], context, bindings, out error)) return false;
                if (error != null) return false;
            }
            return true;
        }

        private static bool TryMatchList(ListPatternNode lp, RuntimeValue scrutinee, Context context,
                                        List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            if (scrutinee is not ListValue lv) return false;
            int total = lv.Elements.Count;

            if (lp.Rest == null)
            {
                if (total != lp.Elements.Count) return false;
                for (int i = 0; i < total; i++)
                {
                    if (!TryMatch(lp.Elements[i], lv.Elements[i], context, bindings, out error)) return false;
                    if (error != null) return false;
                }
                return true;
            }

            // With a rest pattern, the prefix (before rest) and suffix
            // (after rest) must match their corresponding slices; the rest
            // captures whatever is left in between.
            int prefixCount = lp.RestIndex;
            int suffixCount = lp.Elements.Count - prefixCount;
            if (total < prefixCount + suffixCount) return false;

            for (int i = 0; i < prefixCount; i++)
            {
                if (!TryMatch(lp.Elements[i], lv.Elements[i], context, bindings, out error)) return false;
                if (error != null) return false;
            }
            for (int i = 0; i < suffixCount; i++)
            {
                if (!TryMatch(lp.Elements[prefixCount + i], lv.Elements[total - suffixCount + i], context, bindings, out error)) return false;
                if (error != null) return false;
            }

            if (lp.Rest.BindName != null)
            {
                int restLen = total - prefixCount - suffixCount;
                var slice = new List<RuntimeValue>(restLen);
                for (int i = 0; i < restLen; i++) slice.Add(lv.Elements[prefixCount + i]);
                bindings.Add((lp.Rest.BindName, new ListValue(slice)));
            }
            return true;
        }

        // `case is Type [as binder] -> body`. Matches when the scrutinee's
        // runtime type is compatible with the pattern's TestedType under the
        // same rules as the `is` operator. When a binder is present, the
        // scrutinee is introduced into the arm body bound to that name —
        // already narrowed by the type test that just succeeded, so member
        // access on the binder works statically inside the arm.
        private static bool TryMatchTypePattern(TypePatternNode tpn, RuntimeValue scrutinee, Context context,
                                                List<(string, RuntimeValue)> bindings)
        {
            if (!Types.TypeSystem.IsRuntimeTypeMatch(context, tpn.TestedType, scrutinee))
                return false;
            if (!string.IsNullOrEmpty(tpn.BinderName))
                bindings.Add((tpn.BinderName!, scrutinee));
            return true;
        }

        private static bool TryMatchStruct(StructPatternNode sp, RuntimeValue scrutinee, Context context,
                                          List<(string, RuntimeValue)> bindings, out Error? error)
        {
            error = null;
            // Struct instance: nominal name match + field walk.
            if (scrutinee is StructInstanceValue siv)
            {
                if (!string.Equals(siv.Definition.StructName, sp.StructName, System.StringComparison.Ordinal)) return false;
                foreach (var (fieldName, fieldPattern) in sp.Fields)
                {
                    if (!siv.HasField(fieldName))
                    {
                        error = new RuntimeError(sp.PositionStart, sp.PositionEnd,
                            $"struct '{siv.Definition.StructName}' has no field '{fieldName}'",
                            context,
                            code: DiagnosticCode.RuntimeTypeMismatch);
                        return false;
                    }
                    var fieldValue = siv.GetField(fieldName);
                    if (fieldPattern == null) bindings.Add((fieldName, fieldValue));
                    else
                    {
                        if (!TryMatch(fieldPattern, fieldValue, context, bindings, out error)) return false;
                        if (error != null) return false;
                    }
                }
                return true;
            }

            // Class instance: same by-name field destructuring. Nominal
            // identity uses the class type's declared name. Allows
            // 'case User { name, age } -> ...' against a class as well as
            // against a struct.
            if (scrutinee is RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue civ)
            {
                if (!string.Equals(civ.Definition.ClassName, sp.StructName, System.StringComparison.Ordinal)) return false;
                foreach (var (fieldName, fieldPattern) in sp.Fields)
                {
                    if (!civ.HasField(fieldName))
                    {
                        error = new RuntimeError(sp.PositionStart, sp.PositionEnd,
                            $"class '{civ.Definition.ClassName}' has no field '{fieldName}'",
                            context,
                            code: DiagnosticCode.RuntimeTypeMismatch);
                        return false;
                    }
                    var fieldValue = civ.GetField(fieldName);
                    if (fieldPattern == null) bindings.Add((fieldName, fieldValue));
                    else
                    {
                        if (!TryMatch(fieldPattern, fieldValue, context, bindings, out error)) return false;
                        if (error != null) return false;
                    }
                }
                return true;
            }

            // Record instance: nominal + by-name. Record positional
            // destructuring continues to flow through TryMatchVariant.
            if (scrutinee is RaLanguage.Interpreter.Values.Records.RecordInstanceValue riv)
            {
                if (!string.Equals(riv.Definition.StructName, sp.StructName, System.StringComparison.Ordinal)) return false;
                foreach (var (fieldName, fieldPattern) in sp.Fields)
                {
                    if (!riv.HasField(fieldName))
                    {
                        error = new RuntimeError(sp.PositionStart, sp.PositionEnd,
                            $"record '{riv.Definition.StructName}' has no field '{fieldName}'",
                            context,
                            code: DiagnosticCode.RuntimeTypeMismatch);
                        return false;
                    }
                    var fieldValue = riv.GetField(fieldName);
                    if (fieldPattern == null) bindings.Add((fieldName, fieldValue));
                    else
                    {
                        if (!TryMatch(fieldPattern, fieldValue, context, bindings, out error)) return false;
                        if (error != null) return false;
                    }
                }
                return true;
            }

            return false;
        }
    }
}

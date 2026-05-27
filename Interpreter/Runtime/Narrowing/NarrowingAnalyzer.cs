using System.Collections.Generic;
using System.Linq;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Narrowing
{
    // Compile-time analysis pass that reasons about declared types and the
    // refinements induced by `is`-tests / match type-patterns. Two
    // user-visible outputs:
    //
    //   1. `is`-test diagnostics — calls out impossible tests
    //      (`x: int; x is string` ⇒ never true), and trivially-true ones
    //      (`x: int; x is int` ⇒ always true). These do not block
    //      compilation but help users catch dead branches early.
    //
    //   2. Union match-exhaustiveness — when the scrutinee of a `match` is
    //      a variable whose declared type is `T1 | T2 | … | Tn`, every
    //      member must be covered by a `case is Ti -> …` arm (or a
    //      wildcard / bare-binding fallback). Reports missing alternatives
    //      with the exact set of types still uncovered.
    //
    // The pass is conservative: it never asserts narrowing in code we
    // cannot statically prove. When the scrutinee's declared type is
    // unknown (no `let x: T = …` in scope) we emit nothing rather than
    // guess. This keeps the analyzer's recommendations actionable and
    // free of false positives.
    public static class NarrowingAnalyzer
    {
        public static List<StaticAnalyzerDiagnostic> Analyze(AstNode? root)
        {
            var diags = new List<StaticAnalyzerDiagnostic>();
            if (root == null) return diags;
            var state = new State();
            // Pre-seed the built-in ADTs (Result<T,E>, Option<T>) so
            // generic-payload narrowing works for them without requiring
            // a user-visible 'enum Result { ... }' declaration in scope.
            SeedBuiltinAdts(state);
            // First pass: collect every user enum declaration's variant
            // set so CheckMatch can decide enum exhaustiveness without
            // consulting the runtime symbol table.
            CollectEnums(root, state);
            state.PushScope();
            Walk(root, state, diags);
            state.PopScope();
            return diags;
        }

        private static void SeedBuiltinAdts(State state)
        {
            // Result<T, E> { Ok(T), Err(E) }
            state.Enums["Result"] = new Dictionary<string, int>(System.StringComparer.Ordinal)
            {
                { "Ok", 1 },
                { "Err", 1 },
            };
            state.EnumVariantPayloads["Result"] = new Dictionary<string, List<TypeDescriptor>?>(System.StringComparer.Ordinal)
            {
                { "Ok",  new List<TypeDescriptor> { TypeDescriptor.TypeParameter("T") } },
                { "Err", new List<TypeDescriptor> { TypeDescriptor.TypeParameter("E") } },
            };
            state.EnumGenericParams["Result"] = new List<string> { "T", "E" };

            // Option<T> { Some(T), None }
            state.Enums["Option"] = new Dictionary<string, int>(System.StringComparer.Ordinal)
            {
                { "Some", 1 },
                { "None", 0 },
            };
            state.EnumVariantPayloads["Option"] = new Dictionary<string, List<TypeDescriptor>?>(System.StringComparer.Ordinal)
            {
                { "Some", new List<TypeDescriptor> { TypeDescriptor.TypeParameter("T") } },
                { "None", null },
            };
            state.EnumGenericParams["Option"] = new List<string> { "T" };
        }

        // Walks the AST once before the main pass and gathers, for every
        // 'enum Name { Variant1, Variant2(payload), ... }' declaration, the
        // mapping Name -> { variantName -> arity }. Nested enum
        // declarations (inside fn bodies) are included.
        private static void CollectEnums(AstNode? node, State state)
        {
            if (node == null) return;
            switch (node)
            {
                case Parser.Nodes.Enums.EnumDefinitionNode en:
                {
                    var name = en.NameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(name) && !state.Enums.ContainsKey(name!))
                    {
                        var variantArity = new Dictionary<string, int>(System.StringComparer.Ordinal);
                        var variantPayloads = new Dictionary<string, List<TypeDescriptor>?>(System.StringComparer.Ordinal);
                        foreach (var v in en.Variants)
                        {
                            variantArity[v.Name] = v.Arity;
                            variantPayloads[v.Name] = v.PayloadTypes;
                        }
                        state.Enums[name!] = variantArity;
                        state.EnumVariantPayloads[name!] = variantPayloads;
                        state.EnumGenericParams[name!] = en.GenericTypeParams ?? new List<string>();
                    }
                    return;
                }
                case Parser.Nodes.Structs.StructDefinitionNode sd:
                {
                    var name = sd.NameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(name) && !state.Structs.ContainsKey(name!))
                    {
                        var fields = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
                        foreach (var f in sd.Fields)
                        {
                            var fname = f.NameTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(fname) && f.FieldType != null)
                                fields[fname!] = f.FieldType;
                        }
                        state.Structs[name!] = fields;
                    }
                    return;
                }
                case Parser.Nodes.Records.RecordDefinitionNode rd:
                {
                    var name = rd.NameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(name) && !state.Structs.ContainsKey(name!))
                    {
                        var fields = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
                        foreach (var f in rd.PrimaryFields)
                        {
                            var fname = f.NameTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(fname) && f.FieldType != null)
                                fields[fname!] = f.FieldType;
                        }
                        state.Structs[name!] = fields;
                    }
                    return;
                }
                case ScopeNode sc:
                    foreach (var c in sc.Nodes) CollectEnums(c, state);
                    return;
                case FunctionDefinitionNode fn:
                    CollectEnums(fn.BodyNode, state);
                    return;
                case ClassDefinitionNode cls:
                {
                    var cname = cls.NameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(cname))
                    {
                        // Sealed marker = '@sealed' annotation on the class
                        // body.
                        if (cls.Annotations != null)
                        {
                            foreach (var ann in cls.Annotations)
                            {
                                if (string.Equals(ann.Name, "sealed", System.StringComparison.Ordinal))
                                {
                                    state.SealedClasses.Add(cname!);
                                    break;
                                }
                            }
                        }
                        if (cls.BaseType != null && !string.IsNullOrEmpty(cls.BaseType.Name))
                        {
                            if (!state.Subclasses.TryGetValue(cls.BaseType.Name, out var subs))
                            {
                                subs = new List<string>();
                                state.Subclasses[cls.BaseType.Name] = subs;
                            }
                            if (!subs.Contains(cname!)) subs.Add(cname!);
                        }
                    }
                    foreach (var m in cls.Methods) CollectEnums(m, state);
                    return;
                }
                case IfNode ifn:
                    foreach (var c in ifn.Cases) CollectEnums(c.Expr, state);
                    if (ifn.ElseCase.HasValue) CollectEnums(ifn.ElseCase.Value.Expr, state);
                    return;
                case WhileNode wn:
                    CollectEnums(wn.BodyNode, state);
                    return;
                case ForNode fnd:
                    CollectEnums(fnd.BodyNode, state);
                    return;
                case MatchNode mn:
                    foreach (var arm in mn.Arms) CollectEnums(arm.Body, state);
                    return;
                default:
                    return;
            }
        }

        // Per-pass mutable state: a stack of scopes, each holding the
        // declared TypeDescriptors that came into existence inside that
        // scope. Lookup walks the stack top-down so the closest binding
        // wins (shadowing semantics).
        private sealed class State
        {
            public readonly List<Dictionary<string, TypeDescriptor>> Scopes = new();
            // Enum exhaustiveness backing: enum name -> (variant name -> arity).
            public readonly Dictionary<string, Dictionary<string, int>> Enums =
                new(System.StringComparer.Ordinal);
            // Enum variant payload types: enum name -> variant name -> payload types.
            // Used by BindPattern to narrow variant sub-pattern bindings.
            public readonly Dictionary<string, Dictionary<string, List<TypeDescriptor>?>> EnumVariantPayloads =
                new(System.StringComparer.Ordinal);
            // Enum generic type parameter names (in declaration order).
            // Combined with the scrutinee's GenericArgs the payload types
            // can be substituted to give the real binding type
            // (`Result<int, string>` -> `Ok(int)`'s `v: int`).
            public readonly Dictionary<string, List<string>> EnumGenericParams =
                new(System.StringComparer.Ordinal);
            // Struct / record field types: type name -> field name -> field type.
            // Used by BindPattern to narrow struct-pattern field bindings.
            public readonly Dictionary<string, Dictionary<string, TypeDescriptor>> Structs =
                new(System.StringComparer.Ordinal);
            // Sealed-class registry: every class marked with `@sealed`.
            public readonly HashSet<string> SealedClasses =
                new(System.StringComparer.Ordinal);
            // Direct subclasses of each class (parent -> child names).
            public readonly Dictionary<string, List<string>> Subclasses =
                new(System.StringComparer.Ordinal);

            public void PushScope() => Scopes.Add(new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal));
            public void PopScope() => Scopes.RemoveAt(Scopes.Count - 1);

            public void Declare(string name, TypeDescriptor type)
            {
                if (Scopes.Count == 0) return;
                Scopes[Scopes.Count - 1][name] = type;
            }

            public TypeDescriptor? Lookup(string name)
            {
                for (int i = Scopes.Count - 1; i >= 0; i--)
                    if (Scopes[i].TryGetValue(name, out var t)) return t;
                return null;
            }

            // Reassignment to a name shadows whatever was known. Because Ra
            // assignments are duck-typed at the source — we can't recover
            // the new RHS's type statically without a full inference pass —
            // we forget the prior refinement to avoid asserting a stale
            // claim. Future expansion (full flow analysis) can replace this
            // with a per-block join.
            public void Invalidate(string name)
            {
                for (int i = Scopes.Count - 1; i >= 0; i--)
                {
                    if (Scopes[i].ContainsKey(name)) { Scopes[i].Remove(name); return; }
                }
            }
        }

        // -------------------- AST walk --------------------

        private static void Walk(AstNode? node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (node == null) return;
            switch (node)
            {
                case ScopeNode scope:
                    state.PushScope();
                    foreach (var n in scope.Nodes) Walk(n, state, diags);
                    state.PopScope();
                    break;

                case VariableDeclarationNode vd:
                    foreach (var (nameTok, init, declType) in vd.Declarations)
                    {
                        if (declType != null)
                        {
                            var name = nameTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(name))
                                state.Declare(name!, declType);
                        }
                        Walk(init, state, diags);
                    }
                    break;

                case VariableAssignmentNode va:
                    // Mutation: drop any prior refinement we held for the
                    // target name. The RHS expression may still contain
                    // sub-expressions worth walking (e.g. nested `is`
                    // tests), so descend into it before invalidating.
                    Walk(va.ValueNode, state, diags);
                    var assignName = va.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(assignName)) state.Invalidate(assignName!);
                    break;

                case FunctionDefinitionNode fn:
                {
                    state.PushScope();
                    if (fn.ArgNames != null && fn.ArgTypes != null)
                    {
                        for (int i = 0; i < fn.ArgNames.Count && i < fn.ArgTypes.Count; i++)
                        {
                            var pname = fn.ArgNames[i];
                            var ptype = fn.ArgTypes[i];
                            if (!string.IsNullOrEmpty(pname) && ptype != null)
                                state.Declare(pname, ptype);
                        }
                    }
                    Walk(fn.BodyNode, state, diags);
                    state.PopScope();
                    break;
                }

                case ClassDefinitionNode cls:
                    foreach (var m in cls.Methods) Walk(m, state, diags);
                    break;

                case IfNode ifn:
                {
                    // `if/elif/else` is encoded as a flat list of cases plus
                    // an optional else. Every branch body gets a fresh scope
                    // so per-branch refinements don't leak into siblings.
                    foreach (var c in ifn.Cases)
                    {
                        Walk(c.Condition, state, diags);
                        state.PushScope();
                        Walk(c.Expr, state, diags);
                        state.PopScope();
                    }
                    if (ifn.ElseCase.HasValue)
                    {
                        state.PushScope();
                        Walk(ifn.ElseCase.Value.Expr, state, diags);
                        state.PopScope();
                    }
                    break;
                }

                case WhileNode wn:
                    Walk(wn.ConditionNode, state, diags);
                    state.PushScope();
                    Walk(wn.BodyNode, state, diags);
                    state.PopScope();
                    break;

                case ForNode fnd:
                {
                    state.PushScope();
                    Walk(fnd.StartValueNode, state, diags);
                    Walk(fnd.EndValueNode, state, diags);
                    Walk(fnd.BodyNode, state, diags);
                    state.PopScope();
                    break;
                }

                case MatchNode mn:
                {
                    Walk(mn.Scrutinee, state, diags);
                    CheckMatch(mn, state, diags);
                    TypeDescriptor? scrutType = null;
                    if (mn.Scrutinee is VariableAccessNode sva)
                    {
                        var sn = sva.VarNameTok.Value?.ToString();
                        if (!string.IsNullOrEmpty(sn)) scrutType = state.Lookup(sn!);
                    }
                    foreach (var arm in mn.Arms)
                    {
                        state.PushScope();
                        BindPattern(arm.Pattern, mn.Scrutinee, state, scrutType);
                        Walk(arm.Guard, state, diags);
                        Walk(arm.Body, state, diags);
                        state.PopScope();
                    }
                    break;
                }

                case IsTypeNode isNode:
                    Walk(isNode.Expression, state, diags);
                    CheckIsTest(isNode, state, diags);
                    break;

                case BinaryOperationNode bo:
                    Walk(bo.LeftNode, state, diags);
                    Walk(bo.RightNode, state, diags);
                    break;

                case UnaryOperationNode uo:
                    Walk(uo.Node, state, diags);
                    break;

                case TernaryNode tn:
                    Walk(tn.Condition, state, diags);
                    Walk(tn.TrueExpression, state, diags);
                    Walk(tn.FalseExpression, state, diags);
                    break;

                case CastNode cn:
                    Walk(cn.Expression, state, diags);
                    break;

                case ReturnNode rn:
                    Walk(rn.NodeToReturn, state, diags);
                    break;

                case ThrowNode th:
                    Walk(th.Expression, state, diags);
                    break;

                default:
                    // Unknown node kinds are walked structurally by their
                    // own visitor at runtime; for narrowing we have no
                    // additional invariant to extract.
                    break;
            }
        }

        // -------------------- `is`-test diagnostics --------------------

        private static void CheckIsTest(IsTypeNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            // We can only reason precisely when the LHS is a plain variable
            // access we have a declared type for. Any other LHS shape (a
            // function call, member access, …) is opaque to this pass — we
            // bail rather than risk a false positive.
            if (!(node.Expression is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;

            var declared = state.Lookup(name!);
            if (declared == null) return;
            var tested = node.TestedType;
            if (tested == null) return;

            // `is any` is always true; `is T` against a declared `any` is
            // honest narrowing — neither deserves a diagnostic.
            if (string.Equals(declared.Name, "any", System.StringComparison.Ordinal)) return;
            if (string.Equals(tested.Name, "any", System.StringComparison.Ordinal))
            {
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"type test '{name} is any' is always true",
                    node.PositionStart, node.PositionEnd));
                return;
            }

            // Impossible test: no value of `declared` could ever pass the
            // `is tested` check. Reported even for `is not` (where it'd be
            // trivially true — see below).
            if (!TypeSystem.TypesOverlap(null!, declared, tested))
            {
                if (node.Negated)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is not {tested}' is always true: '{name}' has declared type '{declared}' which is disjoint from '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
                else
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is {tested}' is always false: '{name}' has declared type '{declared}' which is disjoint from '{tested}'. Did you mean a different member of the union?",
                        node.PositionStart, node.PositionEnd));
                }
                return;
            }

            // Trivially-true test: the declared type is already a subtype
            // of the tested one — every value flowing here passes.
            if (TypeSystem.IsAssignableType(null!, tested, declared))
            {
                if (node.Negated)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is not {tested}' is always false: every value of declared type '{declared}' is already a '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
                else
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"type test '{name} is {tested}' is always true: declared type '{declared}' is already a subtype of '{tested}'",
                        node.PositionStart, node.PositionEnd));
                }
            }
        }

        // -------------------- Type-pattern binding propagation --------------------

        private static void BindPattern(PatternNode? pattern, AstNode scrutinee, State state, TypeDescriptor? scrutineeType)
        {
            if (pattern == null) return;
            switch (pattern)
            {
                case TypePatternNode tpn when !string.IsNullOrEmpty(tpn.BinderName) && tpn.TestedType != null:
                    state.Declare(tpn.BinderName!, tpn.TestedType);
                    return;

                case VariablePatternNode vp:
                    if (scrutineeType != null) state.Declare(vp.Name, scrutineeType);
                    return;

                case AliasPatternNode ap:
                    if (scrutineeType != null) state.Declare(ap.BinderName, scrutineeType);
                    BindPattern(ap.Inner, scrutinee, state, scrutineeType);
                    return;

                case TuplePatternNode tp:
                {
                    // Narrow each element when the scrutinee type is a
                    // tuple with matching generic args.
                    var args = scrutineeType?.GenericArgs;
                    for (int i = 0; i < tp.Elements.Count; i++)
                    {
                        TypeDescriptor? elemT = (args != null && i < args.Count) ? args[i] : null;
                        BindPattern(tp.Elements[i], scrutinee, state, elemT);
                    }
                    return;
                }

                case ListPatternNode lp:
                {
                    // The list element type is the first generic arg of
                    // List<T>. The rest binder gets the whole list type.
                    var args = scrutineeType?.GenericArgs;
                    TypeDescriptor? elemT = (args != null && args.Count > 0) ? args[0] : null;
                    foreach (var elem in lp.Elements)
                        BindPattern(elem, scrutinee, state, elemT);
                    if (lp.Rest != null && !string.IsNullOrEmpty(lp.Rest.BindName) && scrutineeType != null)
                        state.Declare(lp.Rest.BindName!, scrutineeType);
                    return;
                }

                case StructPatternNode sp:
                {
                    // Struct / class / record field-shape narrowing.
                    Dictionary<string, TypeDescriptor>? fieldMap = null;
                    state.Structs.TryGetValue(sp.StructName, out fieldMap);
                    foreach (var (fname, sub) in sp.Fields)
                    {
                        TypeDescriptor? fieldT = null;
                        if (fieldMap != null) fieldMap.TryGetValue(fname, out fieldT!);
                        if (sub == null)
                        {
                            // Field-shorthand: bind 'fname' with the field's type.
                            if (fieldT != null) state.Declare(fname, fieldT);
                        }
                        else
                        {
                            BindPattern(sub, scrutinee, state, fieldT);
                        }
                    }
                    return;
                }

                case VariantPatternNode vp2:
                {
                    if (vp2.SubPatterns == null) return;
                    // Find the variant payload type list. If the scrutinee
                    // type is an enum we know about, use its payload list;
                    // otherwise fall back to no narrowing.
                    List<TypeDescriptor>? payloads = null;
                    var enumLookup = vp2.EnumName ?? scrutineeType?.Name;
                    if (!string.IsNullOrEmpty(enumLookup)
                        && state.EnumVariantPayloads.TryGetValue(enumLookup!, out var variantMap)
                        && variantMap.TryGetValue(vp2.VariantName, out var found))
                    {
                        payloads = found;
                    }

                    // Generic substitution: if the scrutinee is a generic
                    // instantiation (Result<int, string>) and the enum
                    // declares generic parameters ([T, E]), build a map
                    // T -> int, E -> string and substitute through every
                    // payload type. Open / partial generic args fall back
                    // to the declared type-parameter name (today's
                    // behaviour).
                    Dictionary<string, TypeDescriptor>? subst = null;
                    if (payloads != null
                        && scrutineeType != null
                        && scrutineeType.GenericArgs != null
                        && scrutineeType.GenericArgs.Count > 0
                        && !string.IsNullOrEmpty(enumLookup)
                        && state.EnumGenericParams.TryGetValue(enumLookup!, out var enumParams)
                        && enumParams != null
                        && enumParams.Count > 0)
                    {
                        subst = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
                        int n = System.Math.Min(enumParams.Count, scrutineeType.GenericArgs.Count);
                        for (int i = 0; i < n; i++)
                            subst[enumParams[i]] = scrutineeType.GenericArgs[i];
                    }

                    for (int i = 0; i < vp2.SubPatterns.Count; i++)
                    {
                        TypeDescriptor? payloadT = (payloads != null && i < payloads.Count) ? payloads[i] : null;
                        if (payloadT != null && subst != null)
                            payloadT = SubstituteType(payloadT, subst);
                        BindPattern(vp2.SubPatterns[i], scrutinee, state, payloadT);
                    }
                    return;
                }

                case OrPatternNode op:
                    // Or-patterns: each alt produces its own bindings.
                    // We narrow using the first alternative as a
                    // representative; the analyzer's binding-coherence
                    // check flags mismatched alternatives separately.
                    if (op.Alternatives.Count > 0)
                        BindPattern(op.Alternatives[0], scrutinee, state, scrutineeType);
                    return;

                case AndPatternNode andp:
                    // And-pattern: every conjunct contributes bindings;
                    // walk each. Right-most conjuncts win on name clash
                    // (mirroring the runtime engine's binding order).
                    foreach (var conj in andp.Conjuncts)
                        BindPattern(conj, scrutinee, state, scrutineeType);
                    return;

                case NotPatternNode _:
                    // Negation: the parser already rejects bindings
                    // under 'not'; nothing to narrow.
                    return;

                default:
                    return;
            }
        }

        // -------------------- Match exhaustiveness & reachability --------------------

        private static void CheckMatch(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            CheckMatchReachability(node, diags);
            CheckBooleanExhaustiveness(node, state, diags);
            CheckUnionExhaustiveness(node, state, diags);
            CheckEnumExhaustiveness(node, state, diags);
            CheckSealedExhaustiveness(node, state, diags);
            CheckOrPatternBindingCoherence(node, diags);
        }

        // Sealed-class exhaustiveness: match on a value whose declared
        // type is a `@sealed`-marked class must cover every direct
        // subclass. Subclasses are detected at AST collection time via
        // ClassDefinitionNode.BaseType.
        private static void CheckSealedExhaustiveness(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (!(node.Scrutinee is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var declared = state.Lookup(name!);
            if (declared == null) return;
            var declaredName = declared.Name;
            if (string.IsNullOrEmpty(declaredName) || !state.SealedClasses.Contains(declaredName!)) return;
            if (!state.Subclasses.TryGetValue(declaredName!, out var subs) || subs.Count == 0) return;

            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (IsTotalPattern(arm.Pattern)) return;
            }

            var covered = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                CollectSealedCoverage(arm.Pattern, covered);
            }

            var missing = new List<string>();
            foreach (var sub in subs)
                if (!covered.Contains(sub)) missing.Add(sub);

            if (missing.Count > 0)
            {
                var ms = string.Join(", ", missing.Select(n => "'" + n + "'"));
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"non-exhaustive match on sealed class '{declaredName}': missing arm(s) for {ms}. Add 'case is {missing[0]} -> …' or a wildcard 'case _ -> …'.",
                    node.PositionStart, node.PositionEnd));
            }
        }

        private static void CollectSealedCoverage(PatternNode p, HashSet<string> acc)
        {
            switch (p)
            {
                case TypePatternNode tp when tp.TestedType != null && !string.IsNullOrEmpty(tp.TestedType.Name):
                    acc.Add(tp.TestedType.Name);
                    return;
                case StructPatternNode sp:
                    acc.Add(sp.StructName);
                    return;
                case AliasPatternNode ap:
                    CollectSealedCoverage(ap.Inner, acc);
                    return;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives) CollectSealedCoverage(alt, acc);
                    return;
                case AndPatternNode an:
                    foreach (var c in an.Conjuncts) CollectSealedCoverage(c, acc);
                    return;
                default: return;
            }
        }

        // Or-pattern binding coherence: every alternative of `A | B | C`
        // must bind the same set of names. Otherwise a reference to a
        // binding in the arm body could be unbound when one of the
        // alternatives wins. Walks every arm's pattern recursively so
        // nested or-patterns ('case (Ok(v) | Err(v))') are also checked.
        private static void CheckOrPatternBindingCoherence(MatchNode node, List<StaticAnalyzerDiagnostic> diags)
        {
            foreach (var arm in node.Arms)
                WalkPatternForCoherence(arm.Pattern, diags);
        }

        private static void WalkPatternForCoherence(PatternNode p, List<StaticAnalyzerDiagnostic> diags)
        {
            switch (p)
            {
                case OrPatternNode or:
                {
                    // First recurse into each alternative so nested ORs
                    // get checked too.
                    foreach (var alt in or.Alternatives) WalkPatternForCoherence(alt, diags);

                    if (or.Alternatives.Count < 2) return;

                    var baseSet = new SortedSet<string>(System.StringComparer.Ordinal);
                    CollectBindingNames(or.Alternatives[0], baseSet);
                    for (int i = 1; i < or.Alternatives.Count; i++)
                    {
                        var altSet = new SortedSet<string>(System.StringComparer.Ordinal);
                        CollectBindingNames(or.Alternatives[i], altSet);
                        if (!altSet.SetEquals(baseSet))
                        {
                            var missing = new SortedSet<string>(baseSet, System.StringComparer.Ordinal);
                            missing.ExceptWith(altSet);
                            var extra = new SortedSet<string>(altSet, System.StringComparer.Ordinal);
                            extra.ExceptWith(baseSet);
                            var msg = "or-pattern alternatives bind different name sets";
                            if (missing.Count > 0) msg += "; missing in this alt: " + string.Join(", ", missing);
                            if (extra.Count > 0) msg += "; extra in this alt: " + string.Join(", ", extra);
                            diags.Add(new StaticAnalyzerDiagnostic(
                                msg + ". Every alternative must bind the same names; the arm body cannot reference a name that only one branch creates.",
                                or.Alternatives[i].PositionStart, or.Alternatives[i].PositionEnd));
                        }
                    }
                    return;
                }
                case AliasPatternNode ap: WalkPatternForCoherence(ap.Inner, diags); return;
                case AndPatternNode an: foreach (var c in an.Conjuncts) WalkPatternForCoherence(c, diags); return;
                case NotPatternNode np: WalkPatternForCoherence(np.Inner, diags); return;
                case TuplePatternNode tp: foreach (var e in tp.Elements) WalkPatternForCoherence(e, diags); return;
                case ListPatternNode lp: foreach (var e in lp.Elements) WalkPatternForCoherence(e, diags); return;
                case StructPatternNode sp:
                    foreach (var (_, fp) in sp.Fields) if (fp != null) WalkPatternForCoherence(fp, diags);
                    return;
                case VariantPatternNode vp:
                    if (vp.SubPatterns != null) foreach (var s in vp.SubPatterns) WalkPatternForCoherence(s, diags);
                    return;
                case MapPatternNode mp:
                    foreach (var (_, vp2) in mp.Entries) WalkPatternForCoherence(vp2, diags);
                    return;
                default: return;
            }
        }

        private static void CollectBindingNames(PatternNode p, SortedSet<string> acc)
        {
            switch (p)
            {
                case VariablePatternNode v: acc.Add(v.Name); return;
                case AliasPatternNode ap: acc.Add(ap.BinderName); CollectBindingNames(ap.Inner, acc); return;
                case TypePatternNode tp: if (!string.IsNullOrEmpty(tp.BinderName)) acc.Add(tp.BinderName!); return;
                case TuplePatternNode tp2: foreach (var e in tp2.Elements) CollectBindingNames(e, acc); return;
                case ListPatternNode lp:
                    foreach (var e in lp.Elements) CollectBindingNames(e, acc);
                    if (lp.Rest != null && !string.IsNullOrEmpty(lp.Rest.BindName)) acc.Add(lp.Rest.BindName!);
                    return;
                case StructPatternNode sp:
                    foreach (var (fname, fp) in sp.Fields)
                    {
                        if (fp == null) acc.Add(fname);
                        else CollectBindingNames(fp, acc);
                    }
                    return;
                case VariantPatternNode vp:
                    if (vp.SubPatterns != null) foreach (var s in vp.SubPatterns) CollectBindingNames(s, acc);
                    return;
                case AndPatternNode an: foreach (var c in an.Conjuncts) CollectBindingNames(c, acc); return;
                case OrPatternNode or:
                    // Nested OR: take intersection of alternatives — only
                    // names bound by every branch are safe.
                    if (or.Alternatives.Count == 0) return;
                    var first = new SortedSet<string>(System.StringComparer.Ordinal);
                    CollectBindingNames(or.Alternatives[0], first);
                    for (int i = 1; i < or.Alternatives.Count; i++)
                    {
                        var nxt = new SortedSet<string>(System.StringComparer.Ordinal);
                        CollectBindingNames(or.Alternatives[i], nxt);
                        first.IntersectWith(nxt);
                    }
                    acc.UnionWith(first);
                    return;
                case NotPatternNode _: return; // bindings impossible
                case MapPatternNode mp:
                    foreach (var (_, vp2) in mp.Entries) CollectBindingNames(vp2, acc);
                    return;
                default: return;
            }
        }

        // Enum exhaustiveness: every declared variant of the scrutinee's
        // enum must be matched by at least one arm (or a wildcard / bare
        // binder fallback). Reported as a warning so existing partial-match
        // programs still compile.
        private static void CheckEnumExhaustiveness(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (!(node.Scrutinee is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var declared = state.Lookup(name!);
            if (declared == null) return;
            // Strip generic args from the type name — Result<T,E> still
            // looks up the 'Result' enum.
            var enumName = declared.Name;
            if (string.IsNullOrEmpty(enumName)) return;
            if (!state.Enums.TryGetValue(enumName!, out var variants) || variants.Count == 0) return;

            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (IsTotalPattern(arm.Pattern)) return;
            }

            var covered = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                CollectVariantNames(arm.Pattern, enumName!, covered);
            }

            if (covered.Count == 0) return;

            var missing = new List<string>();
            foreach (var v in variants.Keys)
            {
                if (!covered.Contains(v)) missing.Add(v);
            }
            if (missing.Count > 0)
            {
                var ms = string.Join(", ", missing.Select(v => "'" + enumName + "." + v + "'"));
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"non-exhaustive match on enum '{enumName}': missing arm(s) for {ms}. Add 'case {missing[0]}(...) -> …' or a wildcard 'case _ -> …'.",
                    node.PositionStart, node.PositionEnd));
            }
        }

        private static void CollectVariantNames(PatternNode p, string scrutineeEnum, HashSet<string> acc)
        {
            switch (p)
            {
                case VariantPatternNode vp:
                    // Accept both qualified ('Result.Ok') and bare ('Ok').
                    if (vp.EnumName == null || string.Equals(vp.EnumName, scrutineeEnum, System.StringComparison.Ordinal))
                        acc.Add(vp.VariantName);
                    return;
                case VariablePatternNode v:
                    // A bare-identifier pattern is ALSO a possible zero-arity
                    // variant test at runtime. To be safe (and avoid false
                    // exhaustiveness positives), if the name matches a declared
                    // variant of the scrutinee's enum, treat it as covering.
                    // The caller has already determined this is the enum's
                    // scrutinee so the lookup is appropriate.
                    acc.Add(v.Name);
                    return;
                case AliasPatternNode ap:
                    CollectVariantNames(ap.Inner, scrutineeEnum, acc);
                    return;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives) CollectVariantNames(alt, scrutineeEnum, acc);
                    return;
                default: return;
            }
        }

        // Reachability: once a guard-less arm is "total" (wildcard, bare
        // variable, or 'is any'), every subsequent arm is dead code. We
        // also catch the simpler 'duplicate literal' case where two arms
        // match the same constant.
        private static void CheckMatchReachability(MatchNode node, List<StaticAnalyzerDiagnostic> diags)
        {
            bool sawTotal = false;
            string? totalAt = null;
            var seenLiterals = new HashSet<string>(System.StringComparer.Ordinal);
            // (armIndex, lo, hi) — half-open [lo, hi). Range entries.
            var seenRanges = new List<(int Idx, long Lo, long Hi)>();
            // (armIndex, value) — int literals seen so far. Used to flag
            // a subsequent range that covers them and to detect a literal
            // landing inside an earlier range.
            var seenIntLiterals = new List<(int Idx, long Value)>();

            for (int i = 0; i < node.Arms.Count; i++)
            {
                var arm = node.Arms[i];
                if (sawTotal)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"unreachable match arm: earlier arm at {totalAt} matches every value",
                        arm.PositionStart, arm.PositionEnd));
                    continue;
                }

                if (arm.Guard == null && IsTotalPattern(arm.Pattern))
                {
                    sawTotal = true;
                    totalAt = arm.PositionStart.ToString();
                }

                if (arm.Guard == null)
                {
                    var lit = TryPrintLiteralKey(arm.Pattern);
                    if (lit != null && !seenLiterals.Add(lit))
                    {
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"unreachable match arm: literal pattern '{lit}' is already matched by an earlier arm",
                            arm.PositionStart, arm.PositionEnd));
                    }

                    // Try to read this arm's pattern as an integer literal.
                    bool gotIntLit = TryGetIntLiteralPattern(arm.Pattern, out long litVal);
                    if (gotIntLit)
                    {
                        // Is this literal already covered by any earlier
                        // integer range?
                        foreach (var (ri, rlo, rhi) in seenRanges)
                        {
                            if (litVal >= rlo && litVal < rhi)
                            {
                                diags.Add(new StaticAnalyzerDiagnostic(
                                    $"unreachable match arm: literal '{litVal}' falls inside the earlier range arm {ri + 1} (range [{rlo}, {rhi})).",
                                    arm.PositionStart, arm.PositionEnd));
                                break;
                            }
                        }
                        seenIntLiterals.Add((i, litVal));
                    }

                    // Range overlap detection.
                    if (TryGetIntRange(arm.Pattern, out long lo, out long hi))
                    {
                        // (a) overlap with earlier ranges.
                        for (int j = 0; j < seenRanges.Count; j++)
                        {
                            var (oi, olo, ohi) = seenRanges[j];
                            if (lo < ohi && olo < hi)
                            {
                                long inter_lo = lo > olo ? lo : olo;
                                long inter_hi = hi < ohi ? hi : ohi;
                                if (inter_lo < inter_hi)
                                {
                                    diags.Add(new StaticAnalyzerDiagnostic(
                                        $"range pattern overlaps earlier arm (arm {oi + 1}): both cover values in [{inter_lo}, {inter_hi}).",
                                        arm.PositionStart, arm.PositionEnd));
                                    break;
                                }
                            }
                        }
                        // (b) range *covers* an earlier int literal — the
                        // earlier literal already matched first, so it's
                        // not unreachable, but the range partially shadows
                        // a previously-handled value. We still warn
                        // because it usually indicates intent error.
                        foreach (var (li, lv) in seenIntLiterals)
                        {
                            if (lv >= lo && lv < hi)
                            {
                                diags.Add(new StaticAnalyzerDiagnostic(
                                    $"range pattern includes earlier literal arm {li + 1} (value {lv}); the literal still wins by source order.",
                                    arm.PositionStart, arm.PositionEnd));
                                break;
                            }
                        }
                        seenRanges.Add((i, lo, hi));
                    }
                }
            }
        }

        // Extract an int-literal value from a leaf pattern (and from
        // alias-wrapped equivalents). Returns false on anything else.
        private static bool TryGetIntLiteralPattern(PatternNode p, out long value)
        {
            value = 0;
            while (p is AliasPatternNode ap) p = ap.Inner;
            if (p is LiteralPatternNode lp)
                return TryEvalIntLiteral(lp.Expression, out value);
            return false;
        }

        // Recursive type substitution: walk a TypeDescriptor tree and
        // replace every occurrence of a type-parameter whose name is a
        // key in `subst` with the mapped descriptor. Non-parameter nodes
        // are rebuilt with substituted children so a partial match still
        // produces a consistent descriptor.
        private static TypeDescriptor SubstituteType(TypeDescriptor t, Dictionary<string, TypeDescriptor> subst)
        {
            if (t.IsTypeParameter)
            {
                var key = t.TypeParameterName ?? t.Name;
                if (!string.IsNullOrEmpty(key) && subst.TryGetValue(key!, out var replaced))
                    return replaced;
                return t;
            }
            if (t.IsUnionType && t.UnionMembers != null)
            {
                var members = new List<TypeDescriptor>(t.UnionMembers.Count);
                foreach (var m in t.UnionMembers) members.Add(SubstituteType(m, subst));
                return TypeDescriptor.Union(members);
            }
            if (t.IsFunctionType)
            {
                var ps = new List<TypeDescriptor>(t.FunctionParamTypes?.Count ?? 0);
                if (t.FunctionParamTypes != null)
                    foreach (var p in t.FunctionParamTypes) ps.Add(SubstituteType(p, subst));
                var rt = t.FunctionReturnType != null ? SubstituteType(t.FunctionReturnType, subst) : null;
                return TypeDescriptor.FunctionType(ps, rt);
            }
            if (t.IsRefType && t.RefElementType != null)
            {
                return TypeDescriptor.RefType(SubstituteType(t.RefElementType, subst), t.IsMutableRef, t.Lifetime);
            }
            // Nominal — Name + GenericArgs. Rebuild with substituted args.
            List<TypeDescriptor>? newArgs = null;
            if (t.GenericArgs != null && t.GenericArgs.Count > 0)
            {
                newArgs = new List<TypeDescriptor>(t.GenericArgs.Count);
                foreach (var a in t.GenericArgs) newArgs.Add(SubstituteType(a, subst));
            }
            return new TypeDescriptor(t.Name, newArgs);
        }

        // Extract the int-literal half-open interval [lo, hi) that a
        // RangePatternNode covers, when both bounds are integer literals.
        // Open-low / open-high default to long.MinValue / long.MaxValue.
        // Returns false if the bounds are not int literals or the pattern
        // is not a range.
        private static bool TryGetIntRange(PatternNode p, out long lo, out long hi)
        {
            lo = long.MinValue;
            hi = long.MaxValue;
            // Unwrap alias.
            while (p is AliasPatternNode ap) p = ap.Inner;
            if (p is not RangePatternNode rp) return false;

            if (rp.Lo != null)
            {
                if (!TryEvalIntLiteral(rp.Lo, out lo)) return false;
            }
            if (rp.Hi != null)
            {
                if (!TryEvalIntLiteral(rp.Hi, out long hv)) return false;
                hi = rp.IsInclusive ? hv + 1 : hv;
            }
            return true;
        }

        private static bool TryEvalIntLiteral(AstNode expr, out long value)
        {
            value = 0;
            switch (expr)
            {
                case Parser.Nodes.Primitives.NumberNode nn:
                {
                    var s = nn.Tok.Value?.ToString();
                    if (string.IsNullOrEmpty(s)) return false;
                    if (s.Contains('.') || s.Contains('e') || s.Contains('E')) return false;
                    return long.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out value);
                }
                case UnaryOperationNode uo when uo.OpTok.Type == Lexer.Tokens.TokenType.MINUS:
                {
                    if (!TryEvalIntLiteral(uo.Node, out var inner)) return false;
                    value = -inner;
                    return true;
                }
                default:
                    return false;
            }
        }

        // A pattern is *total* for the scrutinee type if it matches every
        // possible value: '_', a bare-identifier binder (not a variant
        // test — but the analyzer cannot decide that without a symbol
        // table, so we are conservative and treat all bare identifiers
        // as binders, matching the runtime engine's preference), or
        // 'is any'.
        private static bool IsTotalPattern(PatternNode p)
        {
            switch (p)
            {
                case WildcardPatternNode _: return true;
                case VariablePatternNode _: return true;
                case TypePatternNode tp:
                    return string.Equals(tp.TestedType?.Name, "any", System.StringComparison.Ordinal);
                case AliasPatternNode ap: return IsTotalPattern(ap.Inner);
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives)
                        if (IsTotalPattern(alt)) return true;
                    return false;
                case AndPatternNode and:
                    // Conjunction is total only if every conjunct is total.
                    foreach (var c in and.Conjuncts)
                        if (!IsTotalPattern(c)) return false;
                    return true;
                case NotPatternNode _:
                    // Conservative: cannot statically prove negation is total.
                    return false;
                default:
                    return false;
            }
        }

        private static string? TryPrintLiteralKey(PatternNode p)
        {
            if (p is LiteralPatternNode lp) return PrintLiteralExpr(lp.Expression);
            return null;
        }

        private static string? PrintLiteralExpr(AstNode expr)
        {
            switch (expr)
            {
                case Parser.Nodes.Primitives.NumberNode nn: return nn.Tok.Value?.ToString();
                case Parser.Nodes.Primitives.StringNode sn:
                    if (sn.Parts.Count == 1 && sn.Parts[0] is Parser.Nodes.Primitives.StringTextNode stn) return "\"" + stn.Text + "\"";
                    return null;
                case Parser.Nodes.Primitives.BooleanNode bn:
                    return bn.Token.Matches(Lexer.Tokens.Keyword.True) ? "true" : "false";
                case Parser.Nodes.Primitives.NullNode _: return "null";
                case UnaryOperationNode uo when uo.OpTok.Type == Lexer.Tokens.TokenType.MINUS:
                    var inner = PrintLiteralExpr(uo.Node);
                    return inner == null ? null : "-" + inner;
                default: return null;
            }
        }

        // 'match b { ... }' over a bool: must cover both true and false
        // (or have a wildcard / bare-binding fallback).
        private static void CheckBooleanExhaustiveness(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (!(node.Scrutinee is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var declared = state.Lookup(name!);
            if (declared == null || !string.Equals(declared.Name, "bool", System.StringComparison.Ordinal)) return;

            bool sawTrue = false, sawFalse = false, sawTotal = false;
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (IsTotalPattern(arm.Pattern)) { sawTotal = true; break; }
                MarkBool(arm.Pattern, ref sawTrue, ref sawFalse);
            }
            if (sawTotal) return;
            if (sawTrue && sawFalse) return;
            var missing = !sawTrue ? "true" : "false";
            diags.Add(new StaticAnalyzerDiagnostic(
                $"non-exhaustive match on bool '{name}': missing arm for '{missing}'. Add 'case {missing} -> …' or a wildcard 'case _ -> …'.",
                node.PositionStart, node.PositionEnd));
        }

        private static void MarkBool(PatternNode p, ref bool sawTrue, ref bool sawFalse)
        {
            switch (p)
            {
                case LiteralPatternNode lp when lp.Expression is Parser.Nodes.Primitives.BooleanNode bn:
                    if (bn.Token.Matches(Lexer.Tokens.Keyword.True)) sawTrue = true;
                    else sawFalse = true;
                    return;
                case AliasPatternNode ap: MarkBool(ap.Inner, ref sawTrue, ref sawFalse); return;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives) MarkBool(alt, ref sawTrue, ref sawFalse);
                    return;
                default: return;
            }
        }

        private static void CheckUnionExhaustiveness(MatchNode node, State state, List<StaticAnalyzerDiagnostic> diags)
        {
            if (!(node.Scrutinee is VariableAccessNode va)) return;
            var name = va.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var declared = state.Lookup(name!);
            if (declared == null || !declared.IsUnionType || declared.UnionMembers == null) return;

            // Wildcard / bare-binding short-circuits exhaustiveness: any
            // unmatched member would land in the fallback.
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                if (IsTotalPattern(arm.Pattern)) return;
            }

            // Collect every TestedType present in the arms. Each union
            // member must be covered by at least one collected type
            // (assignable into it). Walks or-patterns so 'case is int | is string'
            // counts both.
            var covered = new List<TypeDescriptor>();
            foreach (var arm in node.Arms)
            {
                if (arm.Guard != null) continue;
                CollectCoveredTypes(arm.Pattern, covered);
            }

            if (covered.Count == 0) return;

            var missing = new List<TypeDescriptor>();
            foreach (var member in declared.UnionMembers)
            {
                bool isCovered = false;
                for (int i = 0; i < covered.Count; i++)
                {
                    if (TypeSystem.IsAssignableType(null!, covered[i], member))
                    {
                        isCovered = true;
                        break;
                    }
                }
                if (!isCovered) missing.Add(member);
            }

            if (missing.Count > 0)
            {
                var ms = string.Join(", ", missing.Select(t => "'" + t + "'"));
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"non-exhaustive match on union '{declared}': missing arm(s) for {ms}. Add 'case is {missing[0]} -> …' or a wildcard 'case _ -> …'.",
                    node.PositionStart, node.PositionEnd));
            }
        }

        private static void CollectCoveredTypes(PatternNode p, List<TypeDescriptor> acc)
        {
            switch (p)
            {
                case TypePatternNode tp when tp.TestedType != null:
                    acc.Add(tp.TestedType);
                    return;
                case AliasPatternNode ap:
                    CollectCoveredTypes(ap.Inner, acc);
                    return;
                case OrPatternNode or:
                    foreach (var alt in or.Alternatives) CollectCoveredTypes(alt, acc);
                    return;
                default: return;
            }
        }
    }
}

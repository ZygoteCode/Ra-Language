using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Pure static diagnostics over the front-end output — no execution, no VM. Two
    /// checks: (1) <b>unknown types</b> in declarations/params/returns/fields (generics
    /// and in-scope type parameters handled), and (2) <b>undefined symbols</b> from the
    /// binder's unresolved reads, after excluding builtins, imported names, declared
    /// types and enum variants. Both are cross-file aware via the imported-name set.
    /// Single assembly, so the interpreter can call this too.
    /// </summary>
    public static class StaticDiagnostics
    {
        public static List<ToolingDiagnostic> Analyze(
            AstNode? ast,
            IReadOnlyList<Token> tokens,
            SemanticModel model,
            ISet<string> builtinNames,
            ISet<string> importedNames,
            IReadOnlyDictionary<string, List<(int Min, int Max)>> arity,
            IReadOnlyDictionary<string, HashSet<string>> aliasExports,
            TypeTable typeTable,
            VarEnv varEnv)
        {
            var diags = new List<ToolingDiagnostic>();
            if (ast == null) return diags;

            var c = new Collector();
            c.Visit(ast);

            // Known type names: primitives/structural + declared + in-scope generics + imported.
            var knownTypes = new HashSet<string>(s_builtinTypeNames, System.StringComparer.Ordinal);
            foreach (var s in model.Symbols)
                if (IsTypeKind(s.Kind)) knownTypes.Add(s.Name);
            // Also treat every declared symbol name as a value-candidate (functions,
            // top-level vars, etc. are hoisted in the binder, but be safe across passes).
            knownTypes.UnionWith(c.GenericParams);
            knownTypes.UnionWith(importedNames);

            // Names allowed as values (so we don't flag them "undefined").
            var allowedValues = new HashSet<string>(builtinNames, System.StringComparer.Ordinal);
            allowedValues.UnionWith(importedNames);
            allowedValues.UnionWith(c.ImportAliases);
            allowedValues.UnionWith(c.EnumVariants);
            allowedValues.UnionWith(c.MemberNames);
            allowedValues.UnionWith(s_builtinVariants);
            allowedValues.UnionWith(knownTypes); // a type name can appear as a constructor value

            // (1) unknown-type diagnostics.
            foreach (var use in c.TypeUsages)
                CheckType(use.Type, use.OwnerStart, use.OwnerEnd, tokens, knownTypes, diags);

            // (2) undefined-symbol diagnostics (conservative: unresolved reads only).
            foreach (var u in model.Unresolved)
            {
                if (allowedValues.Contains(u.Name)) continue;
                diags.Add(new ToolingDiagnostic(u.Start, u.End, DiagnosticSeverity.Error,
                    $"'{u.Name}' is not defined.", "RA0402"));
            }

            // (3) call-arity diagnostics (overload-aware; respects defaults + varargs).
            foreach (var call in model.Calls)
            {
                if (!arity.TryGetValue(call.Callee, out var overloads) || overloads.Count == 0) continue;
                bool ok = false;
                int lowMin = int.MaxValue, highMax = 0;
                foreach (var (min, max) in overloads)
                {
                    if (call.ArgCount >= min && call.ArgCount <= max) { ok = true; break; }
                    if (min < lowMin) lowMin = min;
                    if (max > highMax) highMax = max;
                }
                if (!ok)
                {
                    string expected = overloads.Count == 1 ? DescribeArity(overloads[0].Min, overloads[0].Max) : $"{lowMin}..{highMax}";
                    diags.Add(new ToolingDiagnostic(call.Start, call.End, DiagnosticSeverity.Error,
                        $"'{call.Callee}' expects {expected} argument(s), got {call.ArgCount}.", "RA0430"));
                }
            }

            // (4) module-qualified member existence: alias.member where member is not exported.
            foreach (var ma in model.MemberAccesses)
            {
                // exports.Count > 0 guard: skip when the module is not yet indexed
                // (avoids flagging every member during background indexing).
                if (aliasExports.TryGetValue(ma.TargetName, out var exports) && exports.Count > 0 && !exports.Contains(ma.Member))
                {
                    diags.Add(new ToolingDiagnostic(ma.Start, ma.End, DiagnosticSeverity.Error,
                        $"Module '{ma.TargetName}' has no exported member '{ma.Member}'.", "RA0505"));
                }
            }

            // (5) argument-type mismatch — conservative: local functions, clearly-incompatible only.
            foreach (var call in model.Calls)
            {
                var paramTypes = typeTable.FunctionParamTypes(call.Callee, call.ArgCount);
                if (paramTypes == null) continue;
                for (int i = 0; i < call.Args.Length && i < paramTypes.Count; i++)
                {
                    var argType = TypeInference.InferType(call.Args[i], varEnv, typeTable);
                    if (argType == null) continue;
                    if (TypeCompat.AreClearlyIncompatible(paramTypes[i], argType))
                    {
                        diags.Add(new ToolingDiagnostic(call.Args[i].PositionStart.Idx, call.Args[i].PositionEnd.Idx,
                            DiagnosticSeverity.Error,
                            $"Argument {i + 1}: expected '{paramTypes[i]}', got '{argType}'.", "RA0432"));
                    }
                }
            }

            // (6) user-type member existence (var.member / Type.member) — leaf types only,
            //     so inherited members never cause a false "no member".
            foreach (var ma in model.MemberAccesses)
            {
                if (aliasExports.ContainsKey(ma.TargetName)) continue; // module-qualified → handled above
                var recvType = varEnv.Get(ma.TargetName);
                string? typeName = recvType?.Name ?? (typeTable.IsKnownType(ma.TargetName) ? ma.TargetName : null);
                if (typeName == null || !typeTable.IsKnownType(typeName)) continue;
                // Resolve members along the full base/interface/trait chain.
                bool complete = typeTable.TryCollectMembers(typeName, out var members);
                if (members.ContainsKey(ma.Member)) continue;  // found, possibly inherited
                if (!complete) continue;                         // unknown base in chain → could inherit → skip
                diags.Add(new ToolingDiagnostic(ma.Start, ma.End, DiagnosticSeverity.Error,
                    $"'{typeName}' has no member '{ma.Member}'.", "RA0431"));
            }

            return diags;
        }

        private static string DescribeArity(int min, int max)
        {
            if (max == int.MaxValue) return $"at least {min}";
            if (min == max) return min.ToString();
            return $"{min}–{max}";
        }

        private static bool IsTypeKind(BoundKind k) => k is
            BoundKind.Class or BoundKind.Struct or BoundKind.Record or BoundKind.Enum or
            BoundKind.Interface or BoundKind.Trait or BoundKind.Annotation or BoundKind.Delegate;

        // ---- unknown-type recursion ----

        private static void CheckType(TypeDescriptor? td, int ownerStart, int ownerEnd,
            IReadOnlyList<Token> tokens, HashSet<string> knownTypes, List<ToolingDiagnostic> diags)
        {
            if (td == null) return;
            if (td.IsTypeParameter) return;                       // parser already scoped it
            if (td.IsRefType) { CheckType(td.RefElementType, ownerStart, ownerEnd, tokens, knownTypes, diags); return; }
            if (td.IsFunctionType)
            {
                if (td.FunctionParamTypes != null)
                    foreach (var p in td.FunctionParamTypes) CheckType(p, ownerStart, ownerEnd, tokens, knownTypes, diags);
                CheckType(td.FunctionReturnType, ownerStart, ownerEnd, tokens, knownTypes, diags);
                return;
            }
            if (td.IsUnionType)
            {
                if (td.UnionMembers != null)
                    foreach (var m in td.UnionMembers) CheckType(m, ownerStart, ownerEnd, tokens, knownTypes, diags);
                return;
            }
            if (td.IsTupleType)
            {
                foreach (var a in td.GenericArgs) CheckType(a, ownerStart, ownerEnd, tokens, knownTypes, diags);
                return;
            }

            // Nominal type.
            if (td.PrimitiveKind == PrimitiveTypeKind.None && !knownTypes.Contains(td.Name))
            {
                if (TryFindTypeToken(tokens, ownerStart, ownerEnd, td.Name, out int s, out int e))
                {
                    diags.Add(new ToolingDiagnostic(s, e, DiagnosticSeverity.Error,
                        $"Unknown type '{td.Name}'.", "RA0420"));
                }
            }
            foreach (var a in td.GenericArgs) CheckType(a, ownerStart, ownerEnd, tokens, knownTypes, diags);
        }

        // Find the identifier token carrying a type name so the squiggle lands on the
        // type. Some declaration nodes (var decls, fields without a default) span only
        // their name token, with the type annotation just after it — so the search runs
        // from the declaration start to the end of its first line, not the node span.
        private const int TypeSearchCharWindow = 400;

        private static bool TryFindTypeToken(IReadOnlyList<Token> tokens, int ownerStart, int ownerEnd, string name, out int start, out int end)
        {
            start = end = 0;
            int lo = TokenLocator.FloorIndex(tokens, ownerStart);
            if (lo < 0) lo = 0;
            int limit = ownerStart + TypeSearchCharWindow;
            for (int i = lo; i < tokens.Count; i++)
            {
                int ts = tokens[i].PositionStart.Idx;
                if (ts < ownerStart) continue;
                if (ts > limit) break;
                // Type annotations live on the declaration's first line.
                if (tokens[i].Type == TokenType.NEWLINE) break;
                if (tokens[i].Type == TokenType.IDENTIFIER && TokenLocator.Text(tokens[i]) == name)
                {
                    start = ts;
                    end = tokens[i].PositionEnd.Idx;
                    return true;
                }
            }
            return false;
        }

        // ---- collector ----

        private readonly struct TypeUsage
        {
            public readonly TypeDescriptor Type;
            public readonly int OwnerStart;
            public readonly int OwnerEnd;
            public TypeUsage(TypeDescriptor type, int s, int e) { Type = type; OwnerStart = s; OwnerEnd = e; }
        }

        private sealed class Collector
        {
            public readonly List<TypeUsage> TypeUsages = new();
            public readonly HashSet<string> GenericParams = new(System.StringComparer.Ordinal);
            public readonly HashSet<string> EnumVariants = new(System.StringComparer.Ordinal);
            public readonly HashSet<string> ImportAliases = new(System.StringComparer.Ordinal);
            // Member names (fields/methods/properties) so a bare member reference inside
            // a method (implicit self) is never mis-flagged as undefined.
            public readonly HashSet<string> MemberNames = new(System.StringComparer.Ordinal);

            private void Member(in Token t) { var n = t.Value?.ToString(); if (!string.IsNullOrEmpty(n)) MemberNames.Add(n); }
            private void Member(in Token? t) { if (t.HasValue) Member(t.Value); }

            private void AddType(TypeDescriptor? td, AstNode owner)
            {
                if (td != null) TypeUsages.Add(new TypeUsage(td, owner.PositionStart.Idx, owner.PositionEnd.Idx));
            }

            private void AddTypes(IReadOnlyList<TypeDescriptor?>? types, AstNode owner)
            {
                if (types == null) return;
                for (int i = 0; i < types.Count; i++) AddType(types[i], owner);
            }

            // Descends containers + declarations only (type annotations never live inside
            // bare expressions here), which keeps this lighter than the full binder walk.
            public void Visit(AstNode? node)
            {
                switch (node)
                {
                    case null: return;
                    case ScopeNode block: foreach (var n in block.Nodes) Visit(n); return;
                    case NamespaceDeclarationNode ns: Visit(ns.Body); return;

                    case VariableDeclarationNode decl:
                        foreach (var (_, _, type) in decl.Declarations) AddType(type, decl);
                        return;

                    case FunctionDefinitionNode fn:
                        GenericParams.UnionWith(fn.GenericTypeParams);
                        AddTypes(fn.ArgTypes, fn);
                        AddType(fn.ReturnType, fn);
                        AddType(fn.VarArgType, fn);
                        Visit(fn.BodyNode);
                        return;

                    case ClassDefinitionNode cls:
                        GenericParams.UnionWith(cls.GenericTypeParams);
                        AddType(cls.BaseType, cls);
                        foreach (var it in cls.ImplementedInterfaces) AddType(it, cls);
                        foreach (var wt in cls.WithTraits) AddType(wt, cls);
                        foreach (var f in cls.Fields) { Member(f.NameTok); AddType(f.FieldType, f); Visit(f.DefaultValueNode); }
                        foreach (var m in cls.Methods) { Member(m.VarNameTok); Visit(m); }
                        foreach (var pr in cls.Properties) { Member(pr.NameTok); AddType(pr.PropertyType, pr); }
                        return;

                    case StructDefinitionNode st:
                        GenericParams.UnionWith(st.GenericTypeParams);
                        foreach (var f in st.Fields) { Member(f.NameTok); AddType(f.FieldType, f); Visit(f.DefaultValueNode); }
                        foreach (var m in st.Methods) { Member(m.NameTok); AddTypes(m.ArgTypes, m); AddType(m.ReturnType, m); Visit(m.BodyNode); }
                        foreach (var pr in st.Properties) { Member(pr.NameTok); AddType(pr.PropertyType, pr); }
                        return;

                    case RecordDefinitionNode rec:
                        GenericParams.UnionWith(rec.GenericTypeParams);
                        AddType(rec.BaseType, rec);
                        foreach (var pf in rec.PrimaryFields) { Member(pf.NameTok); AddType(pf.FieldType, pf); }
                        foreach (var m in rec.Methods) { Member(m.NameTok); AddTypes(m.ArgTypes, m); AddType(m.ReturnType, m); Visit(m.BodyNode); }
                        foreach (var pr in rec.Properties) { Member(pr.NameTok); AddType(pr.PropertyType, pr); }
                        return;

                    case TraitDefinitionNode tr:
                        GenericParams.UnionWith(tr.GenericTypeParams);
                        foreach (var f in tr.Fields) { Member(f.NameTok); AddType(f.FieldType, f); }
                        foreach (var m in tr.Methods) { Member(m.NameTok); AddTypes(m.ArgTypes, m); AddType(m.ReturnType, m); Visit(m.BodyNode); }
                        foreach (var pr in tr.Properties) { Member(pr.NameTok); AddType(pr.PropertyType, pr); }
                        return;

                    case InterfaceDefinitionNode it2:
                        GenericParams.UnionWith(it2.GenericTypeParams);
                        foreach (var f in it2.Fields) { Member(f.NameTok); AddType(f.FieldType, f); }
                        foreach (var m in it2.Methods) { Member(m.NameTok); AddTypes(m.ArgTypes, m); AddType(m.ReturnType, m); }
                        foreach (var pr in it2.Properties) { Member(pr.NameTok); AddType(pr.PropertyType, pr); }
                        return;

                    case ExtensionDefinitionNode ext:
                        foreach (var m in ext.Methods) Visit(m);
                        foreach (var pr in ext.Properties) AddType(pr.PropertyType, pr);
                        return;

                    case EnumDefinitionNode en:
                        GenericParams.UnionWith(en.GenericTypeParams);
                        foreach (var v in en.Variants) EnumVariants.Add(v.MemberTok.Value?.ToString() ?? "");
                        return;

                    case ImportAliasNode ia: ImportAliases.Add(ia.Alias); return;

                    // Statement containers that can hold local declarations.
                    case IfNode ifn:
                        foreach (var cse in ifn.Cases) Visit(cse.Item2);
                        if (ifn.ElseCase != null) Visit(ifn.ElseCase.Value.Item1);
                        return;
                    case ForNode fr: Visit(fr.BodyNode); return;
                    case ForEachNode fe: Visit(fe.BodyNode); return;
                    case ForAwaitNode fa: Visit(fa.BodyNode); return;
                    case WhileNode wn: Visit(wn.BodyNode); return;
                    case DoWhileNode dwn: Visit(dwn.BodyNode); return;
                    case SuperForNode sfn: Visit(sfn.BodyNode); return;
                    case RetryNode rn: Visit(rn.BodyNode); if (rn.ElseNode != null) Visit(rn.ElseNode); return;
                    case SwitchNode sw:
                        foreach (var cse in sw.Cases) if (cse.Body != null) Visit(cse.Body);
                        return;
                    case MatchNode m2:
                        foreach (var arm in m2.Arms) Visit(arm.Body);
                        return;
                    case TryNode tn:
                        Visit(tn.TryBody);
                        if (tn.CatchBody != null) Visit(tn.CatchBody);
                        if (tn.FinallyBody != null) Visit(tn.FinallyBody);
                        return;
                    case LabelNode lbl: Visit(lbl.Statements); return;
                }
            }
        }

        // Primitives + structural + collection + builtin ADT type names. Generous on
        // purpose: a missed real error is better than a false positive on a valid type.
        private static readonly HashSet<string> s_builtinTypeNames = new(System.StringComparer.Ordinal)
        {
            "int", "number", "long", "float", "double", "uint", "ulong", "short", "ushort",
            "int128", "uint128", "decimal", "byte", "bool", "string", "char", "void", "object", "any",
            "list", "List", "map", "Map", "set", "Set", "tuple", "Tuple", "array", "Array",
            "dict", "Dict", "fn", "function", "Function", "union", "Result", "Option", "Self",
        };

        private static readonly HashSet<string> s_builtinVariants = new(System.StringComparer.Ordinal)
        {
            "Ok", "Err", "Some", "None",
        };
    }
}

using System.Collections.Generic;
using System.Linq;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;
using LexTok = RaLanguage.Lexer.Tokens;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Type model over the structural symbols: member lookup (declaration + extensions,
    /// local + imported), function returns, and inheritance flags. Query-based so it
    /// stays cheap; the workspace index supplies cross-module members.
    /// </summary>
    public sealed class TypeTable
    {
        private readonly SymbolIndex _local;
        private readonly IReadOnlyList<RaSymbol> _imported;
        private readonly WorkspaceIndex? _ws;

        public TypeTable(SymbolIndex local, IReadOnlyList<RaSymbol> importedExports, WorkspaceIndex? ws)
        {
            _local = local;
            _imported = importedExports ?? System.Array.Empty<RaSymbol>();
            _ws = ws;
        }

        // Every top-level declaration named `name` — current file, imported modules, and
        // (for base-chain resolution that may cross non-imported files) the workspace.
        private IEnumerable<RaSymbol> TypeDecls(string name)
        {
            foreach (var s in _local.TopLevel) if (s.Name == name) yield return s;
            foreach (var s in _imported) if (s.Name == name) yield return s;
            if (_ws != null) foreach (var s in _ws.FindTypes(name)) yield return s;
        }

        public bool IsKnownType(string name)
        {
            foreach (var d in TypeDecls(name)) if (IsTypeKind(d.Kind)) return true;
            return false;
        }

        /// <summary>
        /// Collect a type's members following its base/interface/trait chain. Returns
        /// false when some type in the chain could not be resolved (members incomplete)
        /// — callers must then NOT flag a member as missing.
        /// </summary>
        public bool TryCollectMembers(string typeName, out Dictionary<string, RaSymbol> members)
        {
            members = new Dictionary<string, RaSymbol>(System.StringComparer.Ordinal);
            bool complete = true;
            Collect(typeName, members, new HashSet<string>(System.StringComparer.Ordinal), ref complete);
            return complete;
        }

        private void Collect(string name, Dictionary<string, RaSymbol> byName, HashSet<string> visited, ref bool complete)
        {
            if (!visited.Add(name)) return; // cycle guard
            bool resolved = false;
            foreach (var d in TypeDecls(name))
            {
                resolved = true;
                foreach (var c in d.Children)
                    if (!byName.ContainsKey(c.Name)) byName[c.Name] = c;
                if (d.BaseTypes != null)
                    foreach (var b in d.BaseTypes) Collect(b, byName, visited, ref complete);
            }
            if (!resolved) complete = false; // unknown base/type in the chain
        }

        public IEnumerable<RaSymbol> AllMembers(string typeName)
        {
            TryCollectMembers(typeName, out var m);
            return m.Values;
        }

        public RaSymbol? Member(string typeName, string member)
        {
            TryCollectMembers(typeName, out var m);
            return m.TryGetValue(member, out var s) ? s : null;
        }

        public TypeDescriptor? FunctionReturn(string name)
        {
            foreach (var s in _local.TopLevel) if (s.Name == name && s.IsCallable) return s.DeclaredType;
            foreach (var s in _imported) if (s.Name == name && s.IsCallable) return s.DeclaredType;
            return null;
        }

        /// <summary>Generic type-parameter names of a type (e.g. ["T"] for Box&lt;T&gt;), for member substitution.</summary>
        public IReadOnlyList<string>? GenericParamsOf(string typeName)
        {
            foreach (var d in TypeDecls(typeName))
                if (d.GenericParams != null && d.GenericParams.Count > 0) return d.GenericParams;
            return null;
        }

        /// <summary>Generic type-parameter names of a callable (function), for explicit type-arg binding.</summary>
        public IReadOnlyList<string>? CallableGenericParams(string name)
        {
            foreach (var s in _local.TopLevel) if (s.Name == name && s.IsCallable && s.GenericParams != null) return s.GenericParams;
            foreach (var s in _imported) if (s.Name == name && s.IsCallable && s.GenericParams != null) return s.GenericParams;
            return null;
        }

        /// <summary>Constructor parameter types for a type (unnamed ctor matching the arg count), for inferring generic args.</summary>
        public IReadOnlyList<TypeDescriptor?>? ConstructorParamTypes(string typeName, int argCount)
        {
            foreach (var m in AllMembers(typeName))
                if (m.Kind == SymbolKind.Constructor && m.ParameterTypes != null &&
                    argCount >= m.MinArgs && argCount <= m.MaxArgs)
                    return m.ParameterTypes;
            return null;
        }

        /// <summary>Declared parameter types of the local or imported function overload matching the call's arg count.</summary>
        public IReadOnlyList<TypeDescriptor?>? FunctionParamTypes(string name, int argCount)
        {
            foreach (var s in _local.TopLevel) if (MatchOverload(s, name, argCount)) return s.ParameterTypes;
            foreach (var s in _imported) if (MatchOverload(s, name, argCount)) return s.ParameterTypes;
            return null;
        }

        private static bool MatchOverload(RaSymbol s, string name, int argCount) =>
            s.Name == name && s.IsCallable && s.ParameterTypes != null &&
            argCount >= s.MinArgs && argCount <= s.MaxArgs;

        private static bool IsTypeKind(SymbolKind k) => k is
            SymbolKind.Class or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Interface;
    }

    /// <summary>Variable name → static type, collected conservatively (a name with conflicting types is dropped).</summary>
    public sealed class VarEnv
    {
        private readonly Dictionary<string, TypeDescriptor?> _types = new(System.StringComparer.Ordinal);

        public TypeDescriptor? Get(string name) => _types.TryGetValue(name, out var t) ? t : null;

        private void Put(string name, TypeDescriptor? type)
        {
            if (string.IsNullOrEmpty(name) || type == null) return;
            if (_types.TryGetValue(name, out var existing))
            {
                if (existing == null || !existing.Equals(type)) _types[name] = null; // ambiguous → drop
            }
            else _types[name] = type;
        }

        /// <summary>Collect parameter + typed/simple-init variable types across the file.</summary>
        public static VarEnv Build(AstNode? ast, TypeTable table)
        {
            var env = new VarEnv();
            env.Walk(ast, table);
            return env;
        }

        private void Walk(AstNode? node, TypeTable table)
        {
            switch (node)
            {
                case null: return;
                case ScopeNode b: foreach (var n in b.Nodes) Walk(n, table); return;

                case VariableDeclarationNode decl:
                    foreach (var (tok, init, declared) in decl.Declarations)
                    {
                        var name = tok.Value?.ToString() ?? string.Empty;
                        var t = declared ?? TypeInference.InferType(init, this, table);
                        Put(name, t);
                    }
                    return;

                case FunctionDefinitionNode fn:
                    BindParams(fn.ArgNameToks, fn.ArgTypes);
                    Walk(fn.BodyNode, table);
                    return;

                case ClassDefinitionNode cls:
                    foreach (var m in cls.Methods) Walk(m, table);
                    return;
                case StructDefinitionNode st:
                    foreach (var m in st.Methods) { BindParams(m.ArgNameToks, m.ArgTypes); Walk(m.BodyNode, table); }
                    return;
                case RecordDefinitionNode rec:
                    foreach (var m in rec.Methods) { BindParams(m.ArgNameToks, m.ArgTypes); Walk(m.BodyNode, table); }
                    return;
                case TraitDefinitionNode tr:
                    foreach (var m in tr.Methods) { BindParams(m.ArgNameToks, m.ArgTypes); Walk(m.BodyNode, table); }
                    return;
                case ExtensionDefinitionNode ext:
                    foreach (var m in ext.Methods) Walk(m, table);
                    return;
                case NamespaceDeclarationNode ns: Walk(ns.Body, table); return;

                // Statement containers that hold local declarations.
                case IfNode ifn:
                    foreach (var c in ifn.Cases) Walk(c.Item2, table);
                    if (ifn.ElseCase != null) Walk(ifn.ElseCase.Value.Item1, table);
                    return;
                case ForNode fr: Walk(fr.BodyNode, table); return;
                case ForEachNode fe: Walk(fe.BodyNode, table); return;
                case ForAwaitNode fa: Walk(fa.BodyNode, table); return;
                case WhileNode wn: Walk(wn.BodyNode, table); return;
                case DoWhileNode dwn: Walk(dwn.BodyNode, table); return;
                case SuperForNode sfn: Walk(sfn.BodyNode, table); return;
                case RetryNode rn: Walk(rn.BodyNode, table); if (rn.ElseNode != null) Walk(rn.ElseNode, table); return;
                case SwitchNode sw: foreach (var c in sw.Cases) if (c.Body != null) Walk(c.Body, table); return;
                case MatchNode m2: foreach (var arm in m2.Arms) Walk(arm.Body, table); return;
                case TryNode tn:
                    Walk(tn.TryBody, table);
                    if (tn.CatchBody != null) Walk(tn.CatchBody, table);
                    if (tn.FinallyBody != null) Walk(tn.FinallyBody, table);
                    return;
                case LabelNode lbl: Walk(lbl.Statements, table); return;
            }
        }

        private void BindParams(IReadOnlyList<LexTok.Token> names, IReadOnlyList<TypeDescriptor?>? types)
        {
            if (types == null) return;
            for (int i = 0; i < names.Count && i < types.Count; i++)
                Put(names[i].Value?.ToString() ?? string.Empty, types[i]);
        }
    }

    /// <summary>
    /// Best-effort static type inference for an expression. Returns null when the type
    /// cannot be determined confidently — callers must treat null as "unknown" and skip
    /// any error that would depend on it.
    /// </summary>
    public static class TypeInference
    {
        public static TypeDescriptor? InferType(AstNode? node, VarEnv env, TypeTable table)
        {
            switch (node)
            {
                case null: return null;
                case NumberNode n:
                    return new TypeDescriptor(n.Tok.Type == LexTok.TokenType.FLOAT ? "float" : "int");
                case StringNode: return new TypeDescriptor("string");
                case StringTextNode: return new TypeDescriptor("string");
                case BooleanNode: return new TypeDescriptor("bool");
                case CastNode c: return c.TargetType;
                case VariableAccessNode v: return env.Get(v.Name);

                case FunctionCallNode fc:
                    if (fc.NodeToCall is VariableAccessNode callee)
                    {
                        if (table.IsKnownType(callee.Name)) return InferConstructed(callee.Name, fc, env, table);
                        return ApplyExplicitTypeArgs(table.FunctionReturn(callee.Name), callee.Name, fc, table);
                    }
                    if (fc.NodeToCall is MemberAccessNode mc)
                    {
                        var recv = InferType(mc.TargetNode, env, table);
                        if (recv == null) return null;
                        var method = table.Member(recv.Name, mc.MemberTok.Value?.ToString() ?? string.Empty);
                        return SubstituteMember(method?.DeclaredType, recv, table);
                    }
                    return null;

                case MemberAccessNode ma:
                {
                    var recv = InferType(ma.TargetNode, env, table);
                    if (recv == null) return null;
                    var member = table.Member(recv.Name, ma.MemberTok.Value?.ToString() ?? string.Empty);
                    return SubstituteMember(member?.DeclaredType, recv, table);
                }

                default: return null;
            }
        }

        // Infer a constructed type's generic arguments from the constructor call:
        // explicit `Box<int>(...)` type args win; otherwise unify the constructor's
        // parameter types with the inferred argument types (so `Box(5)` → `Box<int>`).
        private static TypeDescriptor InferConstructed(string typeName, FunctionCallNode fc, VarEnv env, TypeTable table)
        {
            var gp = table.GenericParamsOf(typeName);
            if (gp == null || gp.Count == 0) return new TypeDescriptor(typeName);

            var bindings = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
            if (fc.GenericTypeArgs != null)
                for (int i = 0; i < fc.GenericTypeArgs.Count && i < gp.Count; i++)
                    if (fc.GenericTypeArgs[i] != null) bindings[gp[i]] = fc.GenericTypeArgs[i]!;

            var ptypes = table.ConstructorParamTypes(typeName, fc.ArgNodes.Count);
            if (ptypes != null)
                for (int i = 0; i < ptypes.Count && i < fc.ArgNodes.Count; i++)
                {
                    if (ptypes[i] == null) continue;
                    var at = InferType(fc.ArgNodes[i].Expr, env, table);
                    if (at != null) Unify(ptypes[i]!, at, bindings);
                }

            var args = new List<TypeDescriptor>(gp.Count);
            foreach (var p in gp) args.Add(bindings.TryGetValue(p, out var b) ? b : TypeDescriptor.TypeParameter(p));
            return new TypeDescriptor(typeName, args);
        }

        // Explicit type arguments on a generic function call: `foo<int>(...)` → substitute the return.
        private static TypeDescriptor? ApplyExplicitTypeArgs(TypeDescriptor? ret, string fnName, FunctionCallNode fc, TypeTable table)
        {
            if (ret == null || fc.GenericTypeArgs == null || fc.GenericTypeArgs.Count == 0) return ret;
            var fgp = table.CallableGenericParams(fnName);
            if (fgp == null || fgp.Count == 0) return ret;
            var bindings = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
            for (int i = 0; i < fgp.Count && i < fc.GenericTypeArgs.Count; i++)
                if (fc.GenericTypeArgs[i] != null) bindings[fgp[i]] = fc.GenericTypeArgs[i]!;
            try { return ret.Substitute(bindings); }
            catch { return ret; }
        }

        // One-level structural unification of a parameter type against an argument type.
        private static void Unify(TypeDescriptor param, TypeDescriptor arg, Dictionary<string, TypeDescriptor> bindings)
        {
            if (param.IsTypeParameter)
            {
                if (!bindings.ContainsKey(param.TypeParameterName)) bindings[param.TypeParameterName] = arg;
                return;
            }
            int n = System.Math.Min(param.GenericArgs.Count, arg.GenericArgs.Count);
            for (int i = 0; i < n; i++) Unify(param.GenericArgs[i], arg.GenericArgs[i], bindings);
        }

        // Bind the receiver's generic arguments to the type's type-parameters and
        // substitute, so a member declared `T` on Box&lt;int&gt; infers as `int`.
        private static TypeDescriptor? SubstituteMember(TypeDescriptor? memberType, TypeDescriptor receiver, TypeTable table)
        {
            if (memberType == null) return null;
            var gp = table.GenericParamsOf(receiver.Name);
            if (gp == null || gp.Count == 0 || receiver.GenericArgs.Count == 0) return memberType;
            var bindings = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
            int n = System.Math.Min(gp.Count, receiver.GenericArgs.Count);
            for (int i = 0; i < n; i++) bindings[gp[i]] = receiver.GenericArgs[i];
            try { return memberType.Substitute(bindings); }
            catch { return memberType; }
        }
    }

    /// <summary>
    /// Conservative type-compatibility used for argument checking. Returns true ONLY for
    /// clearly-incompatible pairs (cross primitive families, or a primitive against a
    /// user type). Nominal-vs-nominal, refs, generics, unions, functions, any/object and
    /// anything uncertain are treated as compatible to avoid false positives — there is
    /// no inheritance/subtyping knowledge here.
    /// </summary>
    public static class TypeCompat
    {
        private enum Family { Numeric, Bool, Str, Nominal, Unknown }

        public static bool AreClearlyIncompatible(TypeDescriptor? param, TypeDescriptor? arg)
        {
            if (param == null || arg == null) return false;
            if (param.IsTypeParameter || arg.IsTypeParameter) return false;
            if (param.IsUnionType || arg.IsUnionType) return false;
            if (param.IsFunctionType || arg.IsFunctionType) return false;
            if (param.IsTupleType || arg.IsTupleType) return false;
            if (param.IsRefType || arg.IsRefType) return false;
            if (IsWildcard(param.Name) || IsWildcard(arg.Name)) return false;

            var pf = FamilyOf(param);
            var af = FamilyOf(arg);
            if (pf == Family.Unknown || af == Family.Unknown) return false;

            // primitive (numeric/bool/str) on one side, user type on the other → incompatible.
            if (pf == Family.Nominal ^ af == Family.Nominal) return true;
            if (pf == Family.Nominal && af == Family.Nominal) return false; // subtyping unknown → skip

            return pf != af; // numeric vs bool vs string are mutually incompatible
        }

        private static bool IsWildcard(string name) =>
            name is "any" or "object" or "void" or "null" or "union" or "fn" or "tuple";

        private static Family FamilyOf(TypeDescriptor td)
        {
            switch (td.PrimitiveKind)
            {
                case PrimitiveTypeKind.None: break;
                case PrimitiveTypeKind.Bool: return Family.Bool;
                case PrimitiveTypeKind.String: return Family.Str;
                default: return Family.Numeric;
            }
            if (td.Name == "char") return Family.Unknown; // char coerces broadly → don't judge
            return Family.Nominal;
        }
    }
}

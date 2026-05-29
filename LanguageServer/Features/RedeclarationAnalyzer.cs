using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Compilation;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Duplicate-definition detector mirroring the interpreter's exact rules
    /// (per the declaration visitors):
    /// <list type="bullet">
    /// <item><b>Variables</b> (<c>var/let/const/final</c>, parameters, loop/catch vars):
    /// error only on a redeclaration in the <i>same</i> scope (<c>GetLocalEntry</c>) —
    /// shadowing an outer/builtin/imported name is legal.</item>
    /// <item><b>Types</b> (class/struct/record/enum/interface/trait/annotation/delegate):
    /// error if the name collides with anything visible — same-scope sibling, a builtin,
    /// or an imported name (<c>Get</c> walks up).</item>
    /// <item><b>Functions</b>: never flagged — Ra permits redefinition (last wins).</item>
    /// </list>
    /// Processing is in source order (not hoisted), so <c>var x; fn x</c> and
    /// <c>fn x; var x</c> behave exactly as at runtime. Member/field/generic-param
    /// duplicates are already reported by the parser.
    /// </summary>
    public static class RedeclarationAnalyzer
    {
        public static void Analyze(AstNode? ast, ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            if (ast is ScopeNode root)
                WalkScope(root.Nodes, new Dictionary<string, byte>(System.StringComparer.Ordinal), builtins, imported, sink);
        }

        // Declaration kinds in a scope's seen-map.
        private const byte KVar = 0, KType = 1, KFunc = 2;

        private static void WalkScope(IReadOnlyList<AstNode> nodes, Dictionary<string, byte> seen,
            ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            foreach (var stmt in nodes)
            {
                Register(stmt, seen, builtins, imported, sink);
                Recurse(stmt, builtins, imported, sink);
            }
        }

        // Record (and check) the names a statement introduces into the current scope.
        private static void Register(AstNode node, Dictionary<string, byte> seen,
            ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            switch (node)
            {
                case VariableDeclarationNode vd:
                    foreach (var (tok, _, _) in vd.Declarations)
                    {
                        var name = tok.Value?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (seen.ContainsKey(name)) Report(sink, tok, name);
                        seen[name] = KVar;
                    }
                    return;

                case ClassDefinitionNode c: RegisterType(c.NameTok, seen, builtins, imported, sink); return;
                case StructDefinitionNode s: RegisterType(s.NameTok, seen, builtins, imported, sink); return;
                case RecordDefinitionNode r: RegisterType(r.NameTok, seen, builtins, imported, sink); return;
                case EnumDefinitionNode e: RegisterType(e.NameTok, seen, builtins, imported, sink); return;
                case InterfaceDefinitionNode i: RegisterType(i.NameTok, seen, builtins, imported, sink); return;
                case TraitDefinitionNode t: RegisterType(t.NameTok, seen, builtins, imported, sink); return;
                case AnnotationDefinitionNode a: RegisterType(a.NameTok, seen, builtins, imported, sink); return;
                case DelegateDefinitionNode d: RegisterType(d.NameTok, seen, builtins, imported, sink); return;

                case FunctionDefinitionNode fn when fn.VarNameTok.HasValue && !fn.IsConstructor && !fn.IsFactory:
                    // Functions may be redefined (last wins) — register the name so a later
                    // variable collision is caught, but never flag the function itself.
                    var fname = fn.VarNameTok.Value.Value?.ToString();
                    if (!string.IsNullOrEmpty(fname)) seen[fname] = KFunc;
                    return;
            }
        }

        private static void RegisterType(in Token nameTok, Dictionary<string, byte> seen,
            ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            var name = nameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            if (seen.ContainsKey(name) || builtins.Contains(name) || imported.Contains(name))
                Report(sink, nameTok, name);
            seen[name] = KType;
        }

        // Descend into nested scopes (function/method bodies, blocks, control-flow bodies).
        private static void Recurse(AstNode node, ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            switch (node)
            {
                case FunctionDefinitionNode fn: WalkFunction(fn.ArgNameToks, fn.VarArgNameTok, fn.BodyNode, builtins, imported, sink); return;

                case ClassDefinitionNode c:
                    foreach (var m in c.Methods) WalkFunction(m.ArgNameToks, m.VarArgNameTok, m.BodyNode, builtins, imported, sink);
                    return;
                case StructDefinitionNode s:
                    foreach (var m in s.Methods) WalkFunction(m.ArgNameToks, m.VarArgNameTok, m.BodyNode, builtins, imported, sink);
                    return;
                case RecordDefinitionNode r:
                    foreach (var m in r.Methods) WalkFunction(m.ArgNameToks, m.VarArgNameTok, m.BodyNode, builtins, imported, sink);
                    return;
                case TraitDefinitionNode t:
                    foreach (var m in t.Methods) if (m.BodyNode != null) WalkFunction(m.ArgNameToks, m.VarArgNameTok, m.BodyNode, builtins, imported, sink);
                    return;
                case ExtensionDefinitionNode ext:
                    foreach (var m in ext.Methods) WalkFunction(m.ArgNameToks, m.VarArgNameTok, m.BodyNode, builtins, imported, sink);
                    return;

                case NamespaceDeclarationNode ns: WalkChild(ns.Body, null, builtins, imported, sink); return;

                case ScopeNode block: WalkChild(block, null, builtins, imported, sink); return;

                case IfNode ifn:
                    foreach (var cs in ifn.Cases) WalkChild(cs.Item2, null, builtins, imported, sink);
                    if (ifn.ElseCase != null) WalkChild(ifn.ElseCase.Value.Item1, null, builtins, imported, sink);
                    return;
                case ForNode fr: WalkChild(fr.BodyNode, fr.VarNameTok, builtins, imported, sink); return;
                case ForEachNode fe: WalkChild(fe.BodyNode, fe.VarNameToken, builtins, imported, sink); return;
                case ForAwaitNode fa: WalkChild(fa.BodyNode, fa.VarNameToken, builtins, imported, sink); return;
                case WhileNode wn: WalkChild(wn.BodyNode, null, builtins, imported, sink); return;
                case DoWhileNode dwn: WalkChild(dwn.BodyNode, null, builtins, imported, sink); return;
                case SuperForNode sfn: WalkChild(sfn.BodyNode, null, builtins, imported, sink); return;
                case RetryNode rn:
                    WalkChild(rn.BodyNode, null, builtins, imported, sink);
                    if (rn.ElseNode != null) WalkChild(rn.ElseNode, null, builtins, imported, sink);
                    return;
                case SwitchNode sw:
                    foreach (var cs in sw.Cases) if (cs.Body != null) WalkChild(cs.Body, null, builtins, imported, sink);
                    return;
                case MatchNode m2:
                    foreach (var arm in m2.Arms) WalkChild(arm.Body, null, builtins, imported, sink);
                    return;
                case TryNode tn:
                    WalkChild(tn.TryBody, null, builtins, imported, sink);
                    if (tn.CatchBody != null) WalkChild(tn.CatchBody, tn.CatchVarTok, builtins, imported, sink);
                    if (tn.FinallyBody != null) WalkChild(tn.FinallyBody, null, builtins, imported, sink);
                    return;
                case LabelNode lbl: WalkChild(lbl.Statements, null, builtins, imported, sink); return;
            }
        }

        // A function/method scope: parameters + body share one scope (matching the runtime frame).
        private static void WalkFunction(IReadOnlyList<Token> argToks, Token? varArg, AstNode? body,
            ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            var seen = new Dictionary<string, byte>(System.StringComparer.Ordinal);
            foreach (var p in argToks)
            {
                var name = p.Value?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (seen.ContainsKey(name)) Report(sink, p, name);
                seen[name] = KVar;
            }
            if (varArg.HasValue)
            {
                var name = varArg.Value.Value?.ToString();
                if (!string.IsNullOrEmpty(name)) { if (seen.ContainsKey(name)) Report(sink, varArg.Value, name); seen[name] = KVar; }
            }
            if (body is ScopeNode bs) WalkScope(bs.Nodes, seen, builtins, imported, sink);
        }

        // A nested block scope, optionally pre-seeded with a loop/catch binding.
        private static void WalkChild(AstNode? body, Token? injected, ISet<string> builtins, ISet<string> imported, List<ToolingDiagnostic> sink)
        {
            if (body is not ScopeNode bs)
            {
                if (body != null) Recurse(body, builtins, imported, sink);
                return;
            }
            var seen = new Dictionary<string, byte>(System.StringComparer.Ordinal);
            if (injected.HasValue)
            {
                var name = injected.Value.Value?.ToString();
                if (!string.IsNullOrEmpty(name)) seen[name] = KVar;
            }
            WalkScope(bs.Nodes, seen, builtins, imported, sink);
        }

        private static void Report(List<ToolingDiagnostic> sink, in Token tok, string name)
        {
            sink.Add(new ToolingDiagnostic(tok.PositionStart.Idx, tok.PositionEnd.Idx,
                DiagnosticSeverity.Error, $"'{name}' is already defined.", "RA0440"));
        }
    }
}

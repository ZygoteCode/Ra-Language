using System;
using System.Collections.Generic;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Archive
{
    // v1.1 (#6): tree-shake the bundled standard library.
    //
    // Pipeline runs at build time, after the import-graph walk has parsed
    // every reached module. For each module classified as `std/*`:
    //
    //   1. Enumerate top-level *named* declarations: pub/private
    //      functions, var/const/final/let, structs, classes, enums,
    //      interfaces, traits, records, annotation defs.
    //   2. Build a global reference set — every identifier name appearing
    //      anywhere in a *non-std* module's AST plus every identifier
    //      name appearing inside KEPT std declarations. (Iterative
    //      fixed point: a std fn used by the entry pulls in any std
    //      helper it references, which in turn pulls in its own helpers.)
    //   3. Drop every top-level declaration in a std module whose name
    //      is not in the reference set.
    //   4. Splice the source by `Position.Idx` ranges of dropped decls.
    //      Whitespace / comments outside dropped decls are preserved
    //      verbatim, so error line numbers stay close to the original.
    //
    // Conservative-by-design:
    //   * Only std modules (RacModuleKind.StdLib) participate.
    //   * Unknown top-level construct (extension blocks, namespace
    //     declarations, asm blocks, etc.) anchors a "keep everything"
    //     decision for that module — we never silently drop something
    //     the shaker doesn't model.
    //   * Reflective access (`exists`, `annotations_of`, etc.) by name
    //     literal is NOT introspected. A program that resolves a std
    //     helper by string-literal name will see the shaker drop it.
    //     Workaround: list the dotted module via `import std.x;` and
    //     reference the helper directly somewhere, or pass
    //     `--no-tree-shake` (planned escape hatch).
    public static class StdLibTreeShaker
    {
        public sealed class ShakeStats
        {
            public int ModulesScanned;
            public int ModulesShaken;
            public int DeclsKept;
            public int DeclsDropped;
            public long BytesBefore;
            public long BytesAfter;
            // Per-module entries: (logicalPath, kept[], dropped[])
            public readonly List<ModuleShakeReport> Modules = new();
        }

        public sealed class ModuleShakeReport
        {
            public string Path = "";
            public List<string> Kept = new();
            public List<string> Dropped = new();
            public int BytesBefore;
            public int BytesAfter;
        }

        // Result.RewrittenSources keys are absolute paths matching the
        // packager's module identity keys. Missing key == module
        // unchanged. The caller MUST re-hash any rewritten source before
        // emitting the manifest.
        public sealed class Result
        {
            public Dictionary<string, string> RewrittenSources = new(StringComparer.OrdinalIgnoreCase);
            public ShakeStats Stats = new();
        }

        public static Result Shake(
            IReadOnlyList<string> modulePaths,
            IReadOnlyDictionary<string, string> sources,
            IReadOnlyDictionary<string, AstNode> asts,
            IReadOnlySet<string> stdModulePaths)
        {
            var result = new Result();
            var stats = result.Stats;

            // Step 1: enumerate top-level decls per std module.
            //
            // For each std module we collect a list of `Decl`s — each
            // carrying its name(s), source-byte span, and the per-decl
            // identifier-reference set that the body uses. Modules that
            // contain an unknown top-level construct opt out wholesale
            // (we mark them "tainted" and skip them in step 4).
            var moduleDeclLists = new Dictionary<string, List<Decl>>(StringComparer.OrdinalIgnoreCase);
            var taintedStdModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in stdModulePaths)
            {
                if (!asts.TryGetValue(path, out var ast)) continue;
                stats.ModulesScanned++;
                var declList = new List<Decl>();
                bool tainted = false;
                if (ast is ScopeNode scope)
                {
                    foreach (var child in scope.Nodes)
                    {
                        if (!TryClassifyTopLevel(child, declList))
                        {
                            tainted = true;
                            break;
                        }
                    }
                }
                else
                {
                    tainted = true;
                }
                if (tainted)
                {
                    taintedStdModules.Add(path);
                }
                else
                {
                    moduleDeclLists[path] = declList;
                }
            }

            // Step 2: seed reference set from non-std modules + tainted
            // std modules (treated as opaque users of every symbol they
            // contain). Walks every AstNode under each module's tree.
            var globalRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in modulePaths)
            {
                if (stdModulePaths.Contains(path) && !taintedStdModules.Contains(path)) continue;
                if (!asts.TryGetValue(path, out var ast)) continue;
                CollectIdentifierRefs(ast, globalRefs);
            }

            // Step 3: iterative fixed point — for each std module's
            // decls, if a decl name is in the ref set, mark it kept
            // and merge its body's refs into the global set.
            var keptDecls = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in moduleDeclLists)
                keptDecls[kvp.Key] = new HashSet<int>();

            bool changed = true;
            int safety = 0;
            while (changed && safety < 1024)
            {
                changed = false;
                safety++;
                foreach (var kvp in moduleDeclLists)
                {
                    var path = kvp.Key;
                    var declList = kvp.Value;
                    var keepSet = keptDecls[path];
                    for (int i = 0; i < declList.Count; i++)
                    {
                        if (keepSet.Contains(i)) continue;
                        var d = declList[i];
                        bool reach = false;
                        foreach (var n in d.Names)
                        {
                            if (globalRefs.Contains(n)) { reach = true; break; }
                        }
                        if (!reach) continue;
                        keepSet.Add(i);
                        changed = true;
                        // Pull the decl's body refs into the global set
                        // so its private helpers get marked next iter.
                        foreach (var r in d.BodyRefs) globalRefs.Add(r);
                    }
                }
            }
            if (safety >= 1024)
                throw new InvalidOperationException("rac: tree-shake fixed point did not converge");

            // Step 4: rewrite each shakeable std module's source.
            foreach (var kvp in moduleDeclLists)
            {
                var path = kvp.Key;
                var declList = kvp.Value;
                var keepSet = keptDecls[path];
                if (declList.Count == 0) continue;
                string original = sources[path];

                var dropped = new List<Decl>();
                var report = new ModuleShakeReport { Path = path, BytesBefore = original.Length };
                for (int i = 0; i < declList.Count; i++)
                {
                    var d = declList[i];
                    string firstName = d.Names.Count > 0 ? d.Names[0] : "<anon>";
                    if (keepSet.Contains(i))
                    {
                        report.Kept.Add(firstName);
                        stats.DeclsKept++;
                    }
                    else
                    {
                        report.Dropped.Add(firstName);
                        stats.DeclsDropped++;
                        dropped.Add(d);
                    }
                }
                stats.BytesBefore += original.Length;
                if (dropped.Count == 0)
                {
                    stats.BytesAfter += original.Length;
                    report.BytesAfter = original.Length;
                    stats.Modules.Add(report);
                    continue;
                }
                string rewritten = SpliceOutDecls(original, dropped);
                result.RewrittenSources[path] = rewritten;
                stats.BytesAfter += rewritten.Length;
                report.BytesAfter = rewritten.Length;
                stats.ModulesShaken++;
                stats.Modules.Add(report);
            }
            // For tainted modules — counted as scanned + kept verbatim.
            foreach (var path in taintedStdModules)
            {
                stats.BytesBefore += sources[path].Length;
                stats.BytesAfter += sources[path].Length;
            }
            return result;
        }

        // ----------------------------------------------------------------
        // Top-level decl classifier. Returns false on anything the shaker
        // doesn't recognise — tells the caller to opt the whole module
        // out (the safer move than guessing wrong).
        private static bool TryClassifyTopLevel(AstNode node, List<Decl> output)
        {
            switch (node)
            {
                case ImportNode:
                    // Keep imports as-is. Their identifier-side effects
                    // are already captured by the global-ref scan.
                    return true;
                case FunctionDefinitionNode fd:
                {
                    string name = fd.VarNameTok?.Value?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name)) return true; // skip anon
                    var d = new Decl
                    {
                        StartIdx = node.PositionStart.Idx,
                        EndIdx = node.PositionEnd.Idx,
                    };
                    d.Names.Add(name);
                    CollectIdentifierRefs(node, d.BodyRefs);
                    d.BodyRefs.Remove(name); // self-references don't gate
                    output.Add(d);
                    return true;
                }
                case VariableDeclarationNode vd:
                {
                    // VariableDeclarationNode.PositionEnd is only the
                    // last *name* token — doesn't cover the initialiser
                    // expression. Compute the real end by taking the
                    // max of every initialiser's end so a `pub const
                    // X = "..."` decl drops cleanly.
                    int endIdx = node.PositionEnd.Idx;
                    foreach (var (_, expr, _) in vd.Declarations)
                    {
                        if (expr != null && expr.PositionEnd.Idx > endIdx)
                            endIdx = expr.PositionEnd.Idx;
                    }
                    var d = new Decl
                    {
                        StartIdx = node.PositionStart.Idx,
                        EndIdx = endIdx,
                    };
                    foreach (var (tok, expr, _) in vd.Declarations)
                    {
                        string name = tok.Value?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(name)) d.Names.Add(name);
                        if (expr != null) CollectIdentifierRefs(expr, d.BodyRefs);
                    }
                    foreach (var n in d.Names) d.BodyRefs.Remove(n);
                    output.Add(d);
                    return true;
                }
                case ClassDefinitionNode cd:
                    return AddNamedType(node, cd.NameTok, output);
                case StructDefinitionNode sd:
                    return AddNamedType(node, sd.NameTok, output);
                case EnumDefinitionNode ed:
                    return AddNamedType(node, ed.NameTok, output);
                case InterfaceDefinitionNode id:
                    return AddNamedType(node, id.NameTok, output);
                case TraitDefinitionNode td:
                    return AddNamedType(node, td.NameTok, output);
                case RecordDefinitionNode rd:
                    return AddNamedType(node, rd.NameTok, output);
                default:
                    // Extension blocks, namespace decls, asm, free-form
                    // expression statements (rare at module top-level),
                    // annotation defs etc. — taint the module.
                    return false;
            }
        }

        private static bool AddNamedType(AstNode node, Token nameTok, List<Decl> output)
        {
            string name = nameTok.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return true;
            var d = new Decl
            {
                StartIdx = node.PositionStart.Idx,
                EndIdx = node.PositionEnd.Idx,
            };
            d.Names.Add(name);
            CollectIdentifierRefs(node, d.BodyRefs);
            d.BodyRefs.Remove(name);
            output.Add(d);
            return true;
        }

        // ----------------------------------------------------------------
        // Walk every AstNode recursively, pull out every IDENTIFIER token
        // name. Conservative: every identifier-shape token contributes,
        // even keys in annotations, member access targets, and so on.
        // For tree-shaking we want "could be referenced" not "definitely
        // referenced."
        private static void CollectIdentifierRefs(AstNode? node, HashSet<string> sink)
        {
            if (node == null) return;
            var stack = new Stack<AstNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                CollectImmediate(n, sink);
                foreach (var child in EnumerateChildren(n)) if (child != null) stack.Push(child);
            }
        }

        private static void CollectImmediate(AstNode n, HashSet<string> sink)
        {
            // Every node type that carries a "name" token contributes
            // that name. Most node payloads are either VariableAccess
            // (name token), MemberAccess (member token), Token-bearing
            // operators (op token = symbol, not an identifier).
            switch (n)
            {
                case VariableAccessNode va:
                    AddIdentifierToken(va.VarNameTok, sink);
                    break;
                case VariableAssignmentNode vas:
                    AddIdentifierToken(vas.VarNameTok, sink);
                    break;
                case MemberAccessNode ma:
                    AddIdentifierToken(ma.MemberTok, sink);
                    break;
                case MemberAssignmentNode masg:
                    AddIdentifierToken(masg.TargetNode.MemberTok, sink);
                    break;
                case EnumAccessNode ea:
                    AddIdentifierToken(ea.MemberTok, sink);
                    break;
                case NameofNode no:
                    AddIdentifierToken(no.Token, sink);
                    break;
                case ImportSelectiveNode iss:
                    foreach (var t in iss.SymbolNames) AddIdentifierToken(t, sink);
                    break;
                case AnnotationApplicationNode aap:
                    AddIdentifierToken(aap.NameTok, sink);
                    foreach (var (nameTok, _) in aap.NamedArgs) AddIdentifierToken(nameTok, sink);
                    break;
                case ArgumentNode arg when arg.NameTok != null:
                    AddIdentifierToken(arg.NameTok.Value, sink);
                    break;
            }
        }

        private static void AddIdentifierToken(Token t, HashSet<string> sink)
        {
            if (t.Type != TokenType.IDENTIFIER) return;
            string s = t.Value?.ToString() ?? "";
            if (s.Length > 0) sink.Add(s);
        }

        // Coarse child enumerator. Returns every AstNode-shaped field of
        // a given node — used to walk the tree without recursive
        // pattern-match. Covers the same surface the visitor dispatcher
        // does; new node kinds that the shaker can't see are still
        // tolerated because the top-level classifier will have tainted
        // the containing module before we reach here.
        private static IEnumerable<AstNode?> EnumerateChildren(AstNode n)
        {
            switch (n)
            {
                case ScopeNode sn:
                    foreach (var c in sn.Nodes) yield return c;
                    break;
                case IfNode ifn:
                    foreach (var (cond, body, _) in ifn.Cases) { yield return cond; yield return body; }
                    if (ifn.ElseCase != null) yield return ifn.ElseCase.Value.Expr;
                    break;
                case IfCasesWrapperNode wifn:
                    foreach (var (cond, body, _) in wifn.Cases) { yield return cond; yield return body; }
                    if (wifn.ElseCase != null) yield return wifn.ElseCase.Value.Body;
                    break;
                case WhileNode w:
                    yield return w.ConditionNode; yield return w.BodyNode; break;
                case DoWhileNode dw:
                    yield return dw.ConditionNode; yield return dw.BodyNode; break;
                case ForNode f:
                    yield return f.StartValueNode; yield return f.EndValueNode;
                    yield return f.StepValueNode; yield return f.BodyNode; break;
                case ForEachNode fe:
                    yield return fe.CollectionNode; yield return fe.BodyNode; break;
                case SwitchNode sw:
                    yield return sw.Expression;
                    foreach (var c in sw.Cases) yield return c;
                    break;
                case SwitchCaseNode sc:
                    foreach (var lab in sc.Labels) yield return lab;
                    yield return sc.Body;
                    break;
                case SuperForNode supF:
                    foreach (var init in supF.InitializationNodes) yield return init;
                    foreach (var cond in supF.ConditionNodes) yield return cond;
                    foreach (var step in supF.StepNodes) yield return step;
                    yield return supF.BodyNode;
                    break;
                case ThrowNode th:
                    yield return th.Expression; break;
                case BinaryOperationNode bin:
                    yield return bin.LeftNode; yield return bin.RightNode; break;
                case UnaryOperationNode un:
                    yield return un.Node; break;
                case TernaryNode tern:
                    yield return tern.Condition; yield return tern.TrueExpression; yield return tern.FalseExpression; break;
                case CastNode cast:
                    yield return cast.Expression; break;
                case NullCoalescingNode nc:
                    yield return nc.Left; yield return nc.Right; break;
                case RangeNode rn:
                    yield return rn.Start; yield return rn.End; yield return rn.Step; break;
                case SpreadNode sp:
                    yield return sp.Expression; break;
                case PipelineNode pn:
                    yield return pn.LeftNode; yield return pn.RightNode; break;
                case BorrowNode bn:
                    yield return bn.Target; break;
                case DereferenceNode dn:
                    yield return dn.Target; break;
                case DereferenceAssignmentNode dasn:
                    yield return dasn.RefTarget; yield return dasn.ValueNode; break;
                case IsTypeNode itn:
                    yield return itn.Expression; break;
                case WithExpressionNode wen:
                    yield return wen.Receiver;
                    foreach (var u in wen.Updates) yield return u.Value;
                    break;
                case StringNode s:
                    foreach (var part in s.Parts) yield return part;
                    break;
                case ListNode list:
                    foreach (var e in list.ElementNodes) yield return e;
                    break;
                case SetNode set:
                    foreach (var e in set.ElementNodes) yield return e;
                    break;
                case MapNode map:
                    foreach (var p in map.Pairs) { yield return p.Key; yield return p.Value; }
                    break;
                case TupleNode tup:
                    foreach (var e in tup.ElementNodes) yield return e;
                    break;
                case FormattedInterpolationNode fmt:
                    yield return fmt.Expression; break;
                case ListAccessNode la:
                    yield return la.Target; yield return la.Index; break;
                case ListAssignmentNode las:
                    yield return las.Target; yield return las.Value; break;
                case VariableAssignmentNode vas:
                    yield return vas.ValueNode; break;
                case VariableDeclarationNode vd:
                    foreach (var (_, expr, _) in vd.Declarations) yield return expr;
                    break;
                case VariableDeleteNode vdel:
                    // VariableDelete carries only Tokens (names), no AstNode children.
                    break;
                case FunctionDefinitionNode fd:
                    foreach (var d in fd.ParamDefaults) yield return d;
                    yield return fd.BodyNode;
                    break;
                case FunctionCallNode fc:
                    yield return fc.NodeToCall;
                    foreach (var a in fc.ArgNodes) yield return a;
                    break;
                case ArgumentNode arg:
                    yield return arg.Expr; break;
                case ReturnNode ret:
                    yield return ret.NodeToReturn; break;
                case TypeofNode tn:
                    yield return tn.Node; break;
                case TryNode tnode:
                    yield return tnode.TryBody; yield return tnode.CatchBody; yield return tnode.FinallyBody;
                    break;
                case YieldNode yn:
                    yield return yn.Expression; break;
                case MemberAccessNode ma:
                    yield return ma.TargetNode; break;
                case MemberAssignmentNode masg:
                    yield return masg.TargetNode; yield return masg.ValueNode; break;
                case EnumAccessNode ea:
                    yield return ea.EnumNode; break;
                case MatchNode mn:
                    yield return mn.Scrutinee;
                    foreach (var arm in mn.Arms)
                    {
                        yield return arm.Guard;
                        yield return arm.Body;
                    }
                    break;
                case TryUnwrapNode tu:
                    yield return tu.Target; break;
                case ClassDefinitionNode cd:
                    foreach (var fd in cd.Methods) yield return fd;
                    foreach (var op in cd.Operators) yield return op;
                    foreach (var fld in cd.Fields) yield return fld;
                    foreach (var pr in cd.Properties) yield return pr;
                    break;
                case StructDefinitionNode sd:
                    foreach (var m in sd.Methods) yield return m;
                    foreach (var op in sd.Operators) yield return op;
                    foreach (var fld in sd.Fields) yield return fld;
                    foreach (var pr in sd.Properties) yield return pr;
                    break;
                case AnnotationApplicationNode aap:
                    foreach (var p in aap.PositionalArgs) yield return p;
                    foreach (var (_, v) in aap.NamedArgs) yield return v;
                    break;
                case AwaitNode aw:
                    yield return aw.Expression; break;
                case SpawnNode sp:
                    yield return sp.Expression; break;
                case EmitNode em:
                    yield return em.Expression; break;
            }
            // Annotations on the node itself.
            if (n.Annotations != null)
            {
                foreach (var a in n.Annotations) yield return a;
            }
        }

        // ----------------------------------------------------------------
        // Splice out a set of source spans, sorting by start index and
        // removing each (start, end) range. The decl's `PositionStart`
        // is the *name* token, not the leading `pub`/`const`/`fn`
        // modifier — we expand the drop start backward to the previous
        // newline so modifier keywords get carried out with the decl,
        // and forward through any trailing `;` / `}` / whitespace /
        // newline so a `var X = 1;` decl drops cleanly.
        //
        // Whitespace / comments outside the expanded spans survive,
        // so error line numbers in any KEPT decl stay close to the
        // original.
        private static string SpliceOutDecls(string source, List<Decl> dropped)
        {
            dropped.Sort((a, b) => a.StartIdx.CompareTo(b.StartIdx));
            var sb = new System.Text.StringBuilder(source.Length);
            int cursor = 0;
            foreach (var d in dropped)
            {
                int start = d.StartIdx;
                int end = d.EndIdx;
                if (start > source.Length) start = source.Length;
                if (end > source.Length) end = source.Length;

                // Expand `start` backwards through whitespace, modifier
                // keywords, and any '@'-prefixed annotation applications
                // until the previous newline (or BOF). Stops at any
                // non-modifier character so we don't eat the tail of a
                // preceding kept decl.
                start = ExpandStartBackwards(source, start, cursor);
                // Expand `end` forward through trailing whitespace, a
                // single `;`, and one trailing newline.
                end = ExpandEndForwards(source, end);

                if (start < cursor) start = cursor;
                if (end < start) continue;

                sb.Append(source, cursor, start - cursor);
                cursor = end;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);
            return sb.ToString();
        }

        // Walk back from `start` over whitespace + modifier keywords
        // (`pub`, `const`, `final`, `let`, `var`, `static`, `fn`,
        // `async`, `pub fn`, `pub const`, …) and any `@annotation`
        // application lines. Stops at the previous newline or at
        // `floor` (the cursor — never consumes bytes from the prior
        // kept range).
        private static int ExpandStartBackwards(string s, int start, int floor)
        {
            int i = start;
            while (i > floor)
            {
                int j = i - 1;
                // Skip ASCII whitespace (' ', '\t').
                while (j > floor && (s[j] == ' ' || s[j] == '\t')) j--;
                // If we landed on a newline, the decl starts at i (after
                // the line of leading whitespace + modifiers we already
                // consumed). Move past the newline so the dropped span
                // begins at column 0 of the modifier line.
                if (j >= floor && (s[j] == '\n' || s[j] == '\r'))
                {
                    // Consume one CR / LF (or CRLF pair when present).
                    int afterNl = j + 1;
                    i = afterNl;
                    break;
                }
                // Try to peel a modifier keyword left-adjacent to the
                // current `i`.
                int newI = TryConsumeModifierLeft(s, i, floor);
                if (newI == i)
                {
                    // No match — stop. We don't cross arbitrary code.
                    break;
                }
                i = newI;
            }
            return i;
        }

        private static readonly string[] s_modifiers = new[]
        {
            "pub", "const", "final", "let", "var", "static",
            "fn", "async", "abstract", "override", "@",
        };

        private static int TryConsumeModifierLeft(string s, int rightExclusive, int floor)
        {
            // Skip whitespace immediately to the left.
            int j = rightExclusive;
            while (j > floor && (s[j - 1] == ' ' || s[j - 1] == '\t')) j--;
            // Try each modifier keyword.
            foreach (var kw in s_modifiers)
            {
                int klen = kw.Length;
                if (j - klen < floor) continue;
                bool match = true;
                for (int k = 0; k < klen; k++)
                {
                    if (s[j - klen + k] != kw[k]) { match = false; break; }
                }
                if (!match) continue;
                // Boundary check on the left: previous char must be
                // whitespace, newline, ';' or BOF — keeps us from
                // mistaking a substring of an identifier (e.g. "myvar")
                // for the `var` modifier.
                if (j - klen - 1 >= floor)
                {
                    char prev = s[j - klen - 1];
                    if (!(prev == ' ' || prev == '\t' || prev == '\n' || prev == '\r' || prev == ';' || prev == '}'))
                        continue;
                }
                return j - klen;
            }
            return rightExclusive;
        }

        private static int ExpandEndForwards(string s, int end)
        {
            int i = end;
            // The decl's PositionEnd often points *inside* the closing
            // delimiter of the trailing expression — e.g. `"x"` carries
            // a Position past the `x` but not past the closing quote.
            // We expand greedily forward to end-of-line, skipping at
            // most one `;`. End-of-line is the right boundary: std
            // module decls are one per line by convention, and any
            // trailing comment / kept content lives on a separate line.
            // Reasonable belt-and-braces: if we hit `}` (which would
            // be the closing brace of an unterminated decl block) we
            // include it and stop.
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n' || c == '\r')
                {
                    // Consume one CR/LF or CRLF pair.
                    if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i += 2;
                    else i++;
                    break;
                }
                i++;
            }
            return i;
        }

        private sealed class Decl
        {
            public int StartIdx;
            public int EndIdx;
            public readonly List<string> Names = new();
            public readonly HashSet<string> BodyRefs = new(StringComparer.Ordinal);
        }
    }
}

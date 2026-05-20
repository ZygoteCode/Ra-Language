using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Runtime.Borrowing
{
    // Static-analysis arm of the borrow / ownership system. Runs once over the
    // post-derive AST, after StaticAnalyzer, before interpretation. Emits
    // diagnostics for the easiest-to-prove violations so authors get early
    // feedback (before any code runs). The runtime visitors enforce the same
    // rules dynamically — anything missed here is still caught at runtime, just
    // later.
    //
    // What this pass catches today:
    //   * Re-assignment of an immutable `let` / `let const` / `const` binding.
    //   * Use of a `let` binding after it was moved by a previous statement
    //     (linear single-block flow).
    //   * `&mut` of a `let const` / `let` / `const` / `final` binding.
    //   * Returning `&local` where `local` is declared in the function body
    //     (dangling reference at the call site).
    //   * Declaring a `let const` without an initialiser.
    //
    // What it does NOT yet handle:
    //   * Branchy control-flow merging (`if` / loops / `match`): we walk the
    //     children for nested diagnostics but do not propagate move state out
    //     of branches, to keep the analysis free of false positives.
    //   * Cross-function aliasing through arguments.
    //   * Lifetime parameter inference beyond the simple "is this binding
    //     declared inside the current function" check.
    public static class BorrowChecker
    {
        private sealed class BindingState
        {
            public VariableDeclarationType Kind;
            public bool Moved;
            public bool DeclaredInThisScope;
        }

        private sealed class ScopeFrame
        {
            public Dictionary<string, BindingState> Bindings = new Dictionary<string, BindingState>(System.StringComparer.Ordinal);
            public ScopeFrame? Parent;
            public bool IsFunctionRoot;
        }

        public static List<StaticAnalyzerDiagnostic> Analyze(AstNode root)
        {
            var diags = new List<StaticAnalyzerDiagnostic>();
            if (root == null) return diags;
            var frame = new ScopeFrame { IsFunctionRoot = true };
            Walk(root, frame, diags);
            return diags;
        }

        private static void Walk(AstNode? node, ScopeFrame frame, List<StaticAnalyzerDiagnostic> diags)
        {
            if (node == null) return;

            switch (node)
            {
                case ScopeNode scope:
                {
                    var child = new ScopeFrame { Parent = frame };
                    foreach (var n in scope.Nodes) Walk(n, child, diags);
                    break;
                }

                case VariableDeclarationNode decl:
                    HandleDeclaration(decl, frame, diags);
                    break;

                case VariableAssignmentNode assign:
                    HandleAssignment(assign, frame, diags);
                    break;

                case VariableAccessNode acc:
                {
                    var name = acc.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        var bs = Lookup(frame, name!);
                        if (bs != null && bs.Moved)
                        {
                            diags.Add(new StaticAnalyzerDiagnostic(
                                $"use of '{name}' after move (declared as '{Pretty(bs.Kind)}')",
                                acc.PositionStart, acc.PositionEnd));
                        }
                    }
                    break;
                }

                case BorrowNode borrow:
                    HandleBorrow(borrow, frame, diags);
                    Walk(borrow.Target, frame, diags);
                    break;

                case DereferenceNode deref:
                    Walk(deref.Target, frame, diags);
                    break;

                case FunctionDefinitionNode fn:
                {
                    var fnFrame = new ScopeFrame { Parent = frame, IsFunctionRoot = true };
                    foreach (var argTok in fn.ArgNameToks)
                    {
                        var n = argTok.Value?.ToString();
                        if (string.IsNullOrEmpty(n)) continue;
                        fnFrame.Bindings[n!] = new BindingState { Kind = VariableDeclarationType.VARIABLE, DeclaredInThisScope = true };
                    }
                    if (fn.BodyNode != null) Walk(fn.BodyNode, fnFrame, diags);
                    if (fn.BodyNode != null) ScanReturns(fn.BodyNode, fnFrame, diags);
                    break;
                }

                case ClassDefinitionNode cls:
                    foreach (var m in cls.Methods)
                    {
                        if (m.BodyNode == null) continue;
                        var mFrame = new ScopeFrame { Parent = frame, IsFunctionRoot = true };
                        foreach (var argTok in m.ArgNameToks)
                        {
                            var n = argTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(n)) mFrame.Bindings[n!] = new BindingState { Kind = VariableDeclarationType.VARIABLE, DeclaredInThisScope = true };
                        }
                        Walk(m.BodyNode, mFrame, diags);
                        ScanReturns(m.BodyNode, mFrame, diags);
                    }
                    break;

                case StructDefinitionNode str:
                    foreach (var m in str.Methods)
                    {
                        var mFrame = new ScopeFrame { Parent = frame, IsFunctionRoot = true };
                        foreach (var argTok in m.ArgNameToks)
                        {
                            var n = argTok.Value?.ToString();
                            if (!string.IsNullOrEmpty(n)) mFrame.Bindings[n!] = new BindingState { Kind = VariableDeclarationType.VARIABLE, DeclaredInThisScope = true };
                        }
                        Walk(m.BodyNode, mFrame, diags);
                        ScanReturns(m.BodyNode, mFrame, diags);
                    }
                    break;

                default:
                    foreach (var child in EnumerateChildren(node)) Walk(child, frame, diags);
                    break;
            }
        }

        private static void HandleDeclaration(VariableDeclarationNode decl, ScopeFrame frame, List<StaticAnalyzerDiagnostic> diags)
        {
            foreach (var (nameTok, initNode, _) in decl.Declarations)
            {
                if (initNode != null) Walk(initNode, frame, diags);

                var name = nameTok.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                if (decl.DeclarationType == VariableDeclarationType.LET_CONST && initNode == null)
                {
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"'let const {name}' is missing an initialiser",
                        decl.PositionStart, decl.PositionEnd));
                }

                // Mark source as moved for non-copy initialisers via VariableAccess
                // (linear flow: `let y = x;` moves x).
                if (initNode is VariableAccessNode rhsAcc)
                {
                    var srcName = rhsAcc.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(srcName))
                    {
                        var src = Lookup(frame, srcName!);
                        if (src != null && IsLetFamily(src.Kind) && src.Kind != VariableDeclarationType.LET_CONST)
                        {
                            src.Moved = true;
                        }
                    }
                }

                frame.Bindings[name] = new BindingState
                {
                    Kind = decl.DeclarationType,
                    Moved = false,
                    DeclaredInThisScope = true,
                };
            }
        }

        private static void HandleAssignment(VariableAssignmentNode assign, ScopeFrame frame, List<StaticAnalyzerDiagnostic> diags)
        {
            Walk(assign.ValueNode, frame, diags);

            var name = assign.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;

            var bs = Lookup(frame, name!);
            if (bs == null) return;

            switch (bs.Kind)
            {
                case VariableDeclarationType.LET:
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"cannot assign to '{name}': immutable 'let' binding",
                        assign.PositionStart, assign.PositionEnd));
                    break;
                case VariableDeclarationType.LET_CONST:
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"cannot assign to '{name}': 'let const' binding is a compile-time constant",
                        assign.PositionStart, assign.PositionEnd));
                    break;
                case VariableDeclarationType.CONST:
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"cannot assign to '{name}': 'const' binding",
                        assign.PositionStart, assign.PositionEnd));
                    break;
            }

            bs.Moved = false;
        }

        private static void HandleBorrow(BorrowNode borrow, ScopeFrame frame, List<StaticAnalyzerDiagnostic> diags)
        {
            if (borrow.Target is not VariableAccessNode vacc) return;
            var name = vacc.VarNameTok.Value?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var bs = Lookup(frame, name!);
            if (bs == null) return;

            if (bs.Moved)
            {
                diags.Add(new StaticAnalyzerDiagnostic(
                    $"cannot borrow '{name}': value was moved",
                    borrow.PositionStart, borrow.PositionEnd));
                return;
            }

            if (borrow.IsMutable)
            {
                switch (bs.Kind)
                {
                    case VariableDeclarationType.LET:
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"cannot take '&mut {name}': '{name}' is an immutable 'let' binding",
                            borrow.PositionStart, borrow.PositionEnd));
                        break;
                    case VariableDeclarationType.LET_CONST:
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"cannot take '&mut {name}': '{name}' is a 'let const' constant",
                            borrow.PositionStart, borrow.PositionEnd));
                        break;
                    case VariableDeclarationType.CONST:
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"cannot take '&mut {name}': '{name}' is 'const'",
                            borrow.PositionStart, borrow.PositionEnd));
                        break;
                    case VariableDeclarationType.FINAL:
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"cannot take '&mut {name}': '{name}' is 'final'",
                            borrow.PositionStart, borrow.PositionEnd));
                        break;
                }
            }
        }

        // Walks the function body looking for `return &name` where `name` is a
        // function-local binding. Classic dangling-reference case.
        private static void ScanReturns(AstNode? node, ScopeFrame fnFrame, List<StaticAnalyzerDiagnostic> diags)
        {
            if (node == null) return;
            if (node is ReturnNode ret && ret.NodeToReturn is BorrowNode br)
            {
                if (br.Target is VariableAccessNode v)
                {
                    var name = v.VarNameTok.Value?.ToString();
                    if (!string.IsNullOrEmpty(name) && IsLocalToFunction(fnFrame, name!))
                    {
                        diags.Add(new StaticAnalyzerDiagnostic(
                            $"returning '&{name}' to a function-local binding (dangling reference at the call site)",
                            ret.PositionStart, ret.PositionEnd));
                    }
                }
            }
            foreach (var child in EnumerateChildren(node)) ScanReturns(child, fnFrame, diags);
        }

        private static bool IsLocalToFunction(ScopeFrame fnFrame, string name)
        {
            // Walk up but stop at the function root: anything declared above the
            // function root is from outside (capture/import) and outlives the call.
            ScopeFrame? f = fnFrame;
            while (f != null)
            {
                if (f.Bindings.ContainsKey(name)) return true;
                if (f.IsFunctionRoot) return false;
                f = f.Parent;
            }
            return false;
        }

        private static BindingState? Lookup(ScopeFrame frame, string name)
        {
            ScopeFrame? f = frame;
            while (f != null)
            {
                if (f.Bindings.TryGetValue(name, out var bs)) return bs;
                f = f.Parent;
            }
            return null;
        }

        private static bool IsLetFamily(VariableDeclarationType k) =>
            k == VariableDeclarationType.LET ||
            k == VariableDeclarationType.LET_MUT ||
            k == VariableDeclarationType.LET_CONST;

        private static string Pretty(VariableDeclarationType k) => k switch
        {
            VariableDeclarationType.LET => "let",
            VariableDeclarationType.LET_MUT => "let mut",
            VariableDeclarationType.LET_CONST => "let const",
            VariableDeclarationType.CONST => "const",
            VariableDeclarationType.FINAL => "final",
            VariableDeclarationType.VARIABLE => "var",
            _ => k.ToString(),
        };

        // Best-effort recursive child walker for AST nodes we do not specially
        // case-match. Covers control-flow, declarations, and reference-like
        // expressions so move state can propagate as far as the AST shape lets
        // us. Branches walked for nested diagnostics, not state merging.
        private static IEnumerable<AstNode> EnumerateChildren(AstNode node)
        {
            switch (node)
            {
                case ScopeNode s:
                    foreach (var n in s.Nodes) yield return n;
                    break;
                case VariableDeclarationNode vd:
                    foreach (var (_, init, _) in vd.Declarations)
                        if (init != null) yield return init;
                    break;
                case VariableAssignmentNode va:
                    yield return va.ValueNode;
                    break;
                case BorrowNode bn:
                    yield return bn.Target;
                    break;
                case DereferenceNode dn:
                    yield return dn.Target;
                    break;
                case ReturnNode rn:
                    if (rn.NodeToReturn != null) yield return rn.NodeToReturn;
                    break;
                case IfNode ifn:
                    foreach (var c in ifn.Cases)
                    {
                        yield return c.Item1;
                        yield return c.Item2;
                    }
                    if (ifn.ElseCase != null) yield return ifn.ElseCase.Value.Item1;
                    break;
                case WhileNode wn:
                    yield return wn.ConditionNode;
                    yield return wn.BodyNode;
                    break;
                case DoWhileNode dwn:
                    yield return dwn.ConditionNode;
                    yield return dwn.BodyNode;
                    break;
                case ForNode fn:
                    if (fn.StartValueNode != null) yield return fn.StartValueNode;
                    if (fn.EndValueNode != null) yield return fn.EndValueNode;
                    if (fn.StepValueNode != null) yield return fn.StepValueNode;
                    yield return fn.BodyNode;
                    break;
                case ForEachNode fen:
                    yield return fen.CollectionNode;
                    yield return fen.BodyNode;
                    break;
                case FunctionDefinitionNode fdn:
                    if (fdn.BodyNode != null) yield return fdn.BodyNode;
                    break;
                case ClassDefinitionNode cdn:
                    foreach (var m in cdn.Methods)
                        if (m.BodyNode != null) yield return m.BodyNode;
                    break;
                case StructDefinitionNode sdn:
                    foreach (var m in sdn.Methods) yield return m.BodyNode;
                    break;
                case FunctionCallNode fcn:
                    yield return fcn.NodeToCall;
                    foreach (var arg in fcn.ArgNodes) yield return arg.Expr;
                    break;
                case Parser.Nodes.Operations.BinaryOperationNode bon:
                    yield return bon.LeftNode;
                    yield return bon.RightNode;
                    break;
                case Parser.Nodes.Operations.UnaryOperationNode uon:
                    yield return uon.Node;
                    break;
                case DereferenceAssignmentNode dan:
                    yield return dan.RefTarget;
                    yield return dan.ValueNode;
                    break;
            }
        }
    }
}

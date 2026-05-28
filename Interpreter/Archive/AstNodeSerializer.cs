using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Archive
{
    // Polymorphic serialiser for the subset of AST nodes that the v1.1
    // ModuleBytecode payload can persist. Each node is written as:
    //
    //   u8 tag                 0xFF == null, otherwise (AstNodeType + 1)
    //   Position start / end
    //   u8 hasAnnotations + List<AnnotationApplicationNode>?
    //   per-NodeType payload (see Write/ReadXxx methods)
    //
    // Unsupported node kinds throw ModuleBytecodeUnsupportedException so the
    // packager can fall back to source-only mode. The set of supported kinds
    // is deliberately limited to what hello.ra / multi-module tests exercise
    // plus closely related kinds (numerics, primitives, control flow,
    // functions, imports, collections, member access). Future PRs widen it.
    public static class AstNodeSerializer
    {
        private const byte NullTag = 0xFF;

        public static void WriteNode(RacBinaryWriter w, AstNode? node)
        {
            if (node == null)
            {
                w.WriteU8(NullTag);
                return;
            }
            // Tag = NodeType + 1 so 0xFF stays distinct from the
            // lowest enum entry.
            int t = (int)node.NodeType + 1;
            if (t < 0 || t > 254)
                throw new ModuleBytecodeUnsupportedException(
                    $"AstNodeType {node.NodeType} out of byte range");
            w.WriteU8((byte)t);
            ModuleBytecodeIo.WritePosition(w, node.PositionStart);
            ModuleBytecodeIo.WritePosition(w, node.PositionEnd);

            // Annotations
            if (node.Annotations == null || node.Annotations.Count == 0)
            {
                w.WriteU8(0);
            }
            else
            {
                w.WriteU8(1);
                w.WriteI32(node.Annotations.Count);
                foreach (var a in node.Annotations) WriteAnnotationApplication(w, a);
            }

            WritePayload(w, node);
        }

        public static AstNode? ReadNode(RacBinaryReader r)
        {
            byte tag = r.ReadU8();
            if (tag == NullTag) return null;
            var nodeType = (AstNodeType)(tag - 1);
            var ps = ModuleBytecodeIo.ReadPosition(r);
            var pe = ModuleBytecodeIo.ReadPosition(r);

            List<AnnotationApplicationNode>? annotations = null;
            if (r.ReadU8() != 0)
            {
                int n = r.ReadI32();
                if (n < 0 || n > 1_000_000)
                    throw new InvalidDataException($"rac: bogus annotation count {n}");
                annotations = new List<AnnotationApplicationNode>(n);
                for (int i = 0; i < n; i++) annotations.Add(ReadAnnotationApplication(r));
            }

            var node = ReadPayload(r, nodeType, ps, pe);
            if (node == null)
                throw new InvalidDataException($"rac: payload returned null for {nodeType}");
            // Some nodes set PositionStart/End in their constructor from
            // sub-nodes; overwrite with the serialised values for fidelity.
            node.PositionStart = ps;
            node.PositionEnd = pe;
            if (annotations != null) node.Annotations = annotations;
            return node;
        }

        // --- Payload dispatch ---------------------------------------------
        private static void WritePayload(RacBinaryWriter w, AstNode node)
        {
            switch (node)
            {
                case NumberNode n:
                    WriteToken(w, n.Tok);
                    return;
                case StringNode s:
                    w.WriteI32(s.Parts.Count);
                    foreach (var p in s.Parts) WriteNode(w, p);
                    return;
                case StringTextNode st:
                    w.WriteString(st.Text);
                    return;
                case NullNode nul:
                    WriteToken(w, nul.Token);
                    return;
                case BooleanNode b:
                    WriteToken(w, b.Token);
                    return;
                case VariableAccessNode va:
                    WriteToken(w, va.VarNameTok);
                    return;
                case VariableAssignmentNode vas:
                    WriteToken(w, vas.VarNameTok);
                    WriteToken(w, vas.AssignmentToken);
                    WriteNode(w, vas.ValueNode);
                    return;
                case VariableDeclarationNode vd:
                    w.WriteU8((byte)vd.DeclarationType);
                    w.WriteU8(vd.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(vd.IsStatic ? (byte)1 : (byte)0);
                    w.WriteI32(vd.Declarations.Count);
                    foreach (var (tok, expr, td) in vd.Declarations)
                    {
                        WriteToken(w, tok);
                        if (expr == null) w.WriteU8(0);
                        else { w.WriteU8(1); WriteNode(w, expr); }
                        WriteOptionalTypeDescriptor(w, td);
                    }
                    return;
                case BinaryOperationNode bin:
                    WriteNode(w, bin.LeftNode);
                    WriteToken(w, bin.OpTok);
                    WriteNode(w, bin.RightNode);
                    return;
                case UnaryOperationNode un:
                    WriteToken(w, un.OpTok);
                    WriteNode(w, un.Node);
                    w.WriteU8(un.IsLeft ? (byte)1 : (byte)0);
                    return;
                case IfNode ifn:
                    w.WriteI32(ifn.Cases.Count);
                    foreach (var (cond, body, srn) in ifn.Cases)
                    {
                        WriteNode(w, cond);
                        WriteNode(w, body);
                        w.WriteU8(srn ? (byte)1 : (byte)0);
                    }
                    if (ifn.ElseCase == null) w.WriteU8(0);
                    else
                    {
                        w.WriteU8(1);
                        WriteNode(w, ifn.ElseCase.Value.Expr);
                        w.WriteU8(ifn.ElseCase.Value.ShouldReturnNull ? (byte)1 : (byte)0);
                    }
                    return;
                case IfCasesWrapperNode wifn:
                    w.WriteI32(wifn.Cases.Count);
                    foreach (var (cond, body, srn) in wifn.Cases)
                    {
                        WriteNode(w, cond);
                        WriteNode(w, body);
                        w.WriteU8(srn ? (byte)1 : (byte)0);
                    }
                    if (wifn.ElseCase == null) w.WriteU8(0);
                    else
                    {
                        w.WriteU8(1);
                        WriteNode(w, wifn.ElseCase.Value.Body);
                        w.WriteU8(wifn.ElseCase.Value.ShouldReturnNull ? (byte)1 : (byte)0);
                    }
                    return;
                case WhileNode wn:
                    WriteNode(w, wn.ConditionNode);
                    WriteNode(w, wn.BodyNode);
                    w.WriteU8(wn.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;
                case DoWhileNode dwn:
                    WriteNode(w, dwn.ConditionNode);
                    WriteNode(w, dwn.BodyNode);
                    w.WriteU8(dwn.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;
                case ForNode fn:
                    WriteToken(w, fn.VarNameTok);
                    WriteNode(w, fn.StartValueNode);
                    WriteNode(w, fn.EndValueNode);
                    if (fn.StepValueNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, fn.StepValueNode); }
                    WriteNode(w, fn.BodyNode);
                    w.WriteU8(fn.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;
                case ForEachNode fe:
                    WriteToken(w, fe.VarNameToken);
                    WriteNode(w, fe.CollectionNode);
                    WriteNode(w, fe.BodyNode);
                    w.WriteU8(fe.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;
                case BreakNode:
                case ContinueNode:
                case PassNode:
                    // Pure position-only markers — no payload beyond the
                    // header.
                    return;
                case ReturnNode ret:
                    if (ret.NodeToReturn == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, ret.NodeToReturn); }
                    return;
                case FunctionDefinitionNode fd:
                    WriteFunctionDefinition(w, fd);
                    return;
                case FunctionCallNode fc:
                    WriteNode(w, fc.NodeToCall);
                    w.WriteI32(fc.ArgNodes.Count);
                    foreach (var a in fc.ArgNodes) WriteNode(w, a);
                    if (fc.GenericTypeArgs == null) w.WriteU8(0);
                    else
                    {
                        w.WriteU8(1);
                        w.WriteI32(fc.GenericTypeArgs.Count);
                        foreach (var td in fc.GenericTypeArgs) WriteOptionalTypeDescriptor(w, td);
                    }
                    return;
                case ArgumentNode arg:
                    if (arg.NameTok == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteToken(w, arg.NameTok.Value); }
                    WriteNode(w, arg.Expr);
                    w.WriteU8(arg.IsRef ? (byte)1 : (byte)0);
                    return;
                case ScopeNode sc:
                    w.WriteI32(sc.Nodes.Count);
                    foreach (var c in sc.Nodes) WriteNode(w, c);
                    return;
                case ListNode lst:
                    w.WriteI32(lst.ElementNodes.Count);
                    foreach (var e in lst.ElementNodes) WriteNode(w, e);
                    return;
                case ListAccessNode la:
                    WriteNode(w, la.Target);
                    WriteNode(w, la.Index);
                    return;
                case ListAssignmentNode las:
                    WriteNode(w, las.Target);
                    WriteToken(w, las.AssignmentToken);
                    WriteNode(w, las.Value);
                    return;
                case MemberAccessNode ma:
                    WriteNode(w, ma.TargetNode);
                    WriteToken(w, ma.MemberTok);
                    return;
                case MemberAssignmentNode masg:
                    // Round-trip the wrapped MemberAccessNode via the
                    // polymorphic helper so its annotations / positions
                    // survive verbatim.
                    WriteNode(w, masg.TargetNode);
                    WriteToken(w, masg.AssignmentToken);
                    WriteNode(w, masg.ValueNode);
                    return;
                case TypeofNode tn:
                    WriteNode(w, tn.Node);
                    return;
                case NameofNode nn:
                    WriteToken(w, nn.Token);
                    return;
                case DereferenceNode dn:
                    WriteNode(w, dn.Target);
                    return;
                case SuperNode:
                    return;
                case EnumAccessNode ea:
                    WriteNode(w, ea.EnumNode);
                    WriteToken(w, ea.MemberTok);
                    return;
                case CastNode c:
                    WriteNode(w, c.Expression);
                    WriteTypeDescriptor(w, c.TargetType);
                    return;
                case TernaryNode tern:
                    WriteNode(w, tern.Condition);
                    WriteNode(w, tern.TrueExpression);
                    WriteNode(w, tern.FalseExpression);
                    WriteToken(w, tern.OperatorToken);
                    return;
                case ImportAllNode iall:
                    WriteModuleSpecifier(w, iall.Specifier);
                    return;
                case ImportSelectiveNode isel:
                    WriteModuleSpecifier(w, isel.Specifier);
                    w.WriteI32(isel.SymbolNames.Count);
                    foreach (var t in isel.SymbolNames) WriteToken(w, t);
                    return;
                case ImportAliasNode ial:
                    WriteModuleSpecifier(w, ial.Specifier);
                    WriteToken(w, ial.AliasTok);
                    return;
                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"AstNode payload writer for {node.NodeType} ({node.GetType().Name}) not implemented");
            }
        }

        private static AstNode? ReadPayload(RacBinaryReader r, AstNodeType nt, Position ps, Position pe)
        {
            switch (nt)
            {
                case AstNodeType.Number: return new NumberNode(ReadToken(r));
                case AstNodeType.String:
                {
                    int n = r.ReadI32();
                    var parts = new List<AstNode>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var p = ReadNode(r);
                        if (p == null) throw new InvalidDataException("rac: null part in StringNode");
                        parts.Add(p);
                    }
                    return new StringNode(parts, ps, pe);
                }
                case AstNodeType.StringPart:
                    return new StringTextNode(r.ReadString() ?? "", ps, pe);
                case AstNodeType.Null:
                    return new NullNode(ReadToken(r));
                case AstNodeType.Boolean:
                    return new BooleanNode(ReadToken(r));
                case AstNodeType.VariableAccess:
                    return new VariableAccessNode(ReadToken(r));
                case AstNodeType.VariableAssignment:
                {
                    var name = ReadToken(r);
                    var op = ReadToken(r);
                    var val = ReadNode(r);
                    if (val == null) throw new InvalidDataException("rac: VariableAssignment.value is null");
                    return new VariableAssignmentNode(name, op, val);
                }
                case AstNodeType.VariableDeclaration:
                {
                    var dt = (VariableDeclarationType)r.ReadU8();
                    bool isPub = r.ReadU8() != 0;
                    bool isStatic = r.ReadU8() != 0;
                    int n = r.ReadI32();
                    var decls = new List<(Token, AstNode?, TypeDescriptor?)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var tok = ReadToken(r);
                        AstNode? expr = null;
                        if (r.ReadU8() != 0) expr = ReadNode(r);
                        var td = ReadOptionalTypeDescriptor(r);
                        decls.Add((tok, expr, td));
                    }
                    return new VariableDeclarationNode(dt, decls, isPub, isStatic);
                }
                case AstNodeType.BinaryOperation:
                {
                    var l = ReadNode(r)!;
                    var op = ReadToken(r);
                    var rn = ReadNode(r)!;
                    return new BinaryOperationNode(l, op, rn);
                }
                case AstNodeType.UnaryOperation:
                {
                    var op = ReadToken(r);
                    var inner = ReadNode(r)!;
                    bool isLeft = r.ReadU8() != 0;
                    return new UnaryOperationNode(op, inner, isLeft);
                }
                case AstNodeType.If:
                {
                    int n = r.ReadI32();
                    var cases = new List<(AstNode, AstNode, bool)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var cond = ReadNode(r)!;
                        var body = ReadNode(r)!;
                        bool srn = r.ReadU8() != 0;
                        cases.Add((cond, body, srn));
                    }
                    (AstNode, bool)? elseCase = null;
                    if (r.ReadU8() != 0)
                    {
                        var body = ReadNode(r)!;
                        bool srn = r.ReadU8() != 0;
                        elseCase = (body, srn);
                    }
                    return new IfNode(cases, elseCase);
                }
                case AstNodeType.IfCasesWrapper:
                {
                    int n = r.ReadI32();
                    var cases = new List<(AstNode, AstNode, bool)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var cond = ReadNode(r)!;
                        var body = ReadNode(r)!;
                        bool srn = r.ReadU8() != 0;
                        cases.Add((cond, body, srn));
                    }
                    (AstNode, bool)? elseCase = null;
                    if (r.ReadU8() != 0)
                    {
                        var body = ReadNode(r)!;
                        bool srn = r.ReadU8() != 0;
                        elseCase = (body, srn);
                    }
                    return new IfCasesWrapperNode(cases, elseCase);
                }
                case AstNodeType.While:
                {
                    var cond = ReadNode(r)!;
                    var body = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new WhileNode(cond, body, srn);
                }
                case AstNodeType.DoWhile:
                {
                    var cond = ReadNode(r)!;
                    var body = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new DoWhileNode(cond, body, srn);
                }
                case AstNodeType.For:
                {
                    var name = ReadToken(r);
                    var start = ReadNode(r)!;
                    var end = ReadNode(r)!;
                    AstNode? step = null;
                    if (r.ReadU8() != 0) step = ReadNode(r);
                    var body = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new ForNode(name, start, end, step, body, srn);
                }
                case AstNodeType.ForEach:
                {
                    var name = ReadToken(r);
                    var coll = ReadNode(r)!;
                    var body = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new ForEachNode(name, coll, body, srn);
                }
                case AstNodeType.Break: return new BreakNode(ps, pe);
                case AstNodeType.Continue: return new ContinueNode(ps, pe);
                case AstNodeType.Pass: return new PassNode(ps, pe);
                case AstNodeType.Return:
                {
                    AstNode? inner = null;
                    if (r.ReadU8() != 0) inner = ReadNode(r);
                    return new ReturnNode(inner, ps, pe);
                }
                case AstNodeType.FunctionDefinition:
                    return ReadFunctionDefinition(r);
                case AstNodeType.FunctionCall:
                {
                    var callee = ReadNode(r)!;
                    int an = r.ReadI32();
                    var args = new List<ArgumentNode>(an);
                    for (int i = 0; i < an; i++)
                    {
                        var arg = ReadNode(r);
                        if (arg is ArgumentNode an2) args.Add(an2);
                        else throw new InvalidDataException("rac: non-ArgumentNode in FunctionCall.ArgNodes");
                    }
                    List<TypeDescriptor?>? generics = null;
                    if (r.ReadU8() != 0)
                    {
                        int gn = r.ReadI32();
                        generics = new List<TypeDescriptor?>(gn);
                        for (int i = 0; i < gn; i++) generics.Add(ReadOptionalTypeDescriptor(r));
                    }
                    return new FunctionCallNode(callee, args, generics);
                }
                case AstNodeType.Argument:
                {
                    Token? nameTok = null;
                    if (r.ReadU8() != 0) nameTok = ReadToken(r);
                    var expr = ReadNode(r)!;
                    bool isRef = r.ReadU8() != 0;
                    return new ArgumentNode(nameTok, expr, isRef);
                }
                case AstNodeType.Scope:
                {
                    int n = r.ReadI32();
                    var children = new List<AstNode>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var c = ReadNode(r);
                        if (c != null) children.Add(c);
                    }
                    return new ScopeNode(children, ps, pe);
                }
                case AstNodeType.List:
                {
                    int n = r.ReadI32();
                    var items = new List<AstNode>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var c = ReadNode(r);
                        if (c != null) items.Add(c);
                    }
                    return new ListNode(items, ps, pe);
                }
                case AstNodeType.ListAccess:
                {
                    var t = ReadNode(r)!;
                    var idx = ReadNode(r)!;
                    return new ListAccessNode(t, idx, ps, pe);
                }
                case AstNodeType.ListAssignment:
                {
                    var t = ReadNode(r)!;
                    var op = ReadToken(r);
                    var v = ReadNode(r)!;
                    return new ListAssignmentNode(t, op, v);
                }
                case AstNodeType.MemberAccess:
                {
                    var t = ReadNode(r)!;
                    var memTok = ReadToken(r);
                    return new MemberAccessNode(t, memTok);
                }
                case AstNodeType.MemberAssignment:
                {
                    var t = ReadNode(r);
                    if (t is not MemberAccessNode man)
                        throw new InvalidDataException("rac: MemberAssignment target must be MemberAccessNode");
                    var op = ReadToken(r);
                    var v = ReadNode(r)!;
                    return new MemberAssignmentNode(man, op, v);
                }
                case AstNodeType.Typeof:
                {
                    var inner = ReadNode(r)!;
                    return new TypeofNode(inner);
                }
                case AstNodeType.Nameof:
                    return new NameofNode(ReadToken(r));
                case AstNodeType.Dereference:
                {
                    var inner = ReadNode(r)!;
                    return new DereferenceNode(inner, ps, pe);
                }
                case AstNodeType.Super:
                    return new SuperNode(ps, pe);
                case AstNodeType.EnumAccess:
                {
                    var inner = ReadNode(r)!;
                    var memTok = ReadToken(r);
                    return new EnumAccessNode(inner, memTok);
                }
                case AstNodeType.Cast:
                {
                    var inner = ReadNode(r)!;
                    var td = ReadTypeDescriptor(r);
                    return new CastNode(inner, td);
                }
                case AstNodeType.Ternary:
                {
                    var cond = ReadNode(r)!;
                    var tExpr = ReadNode(r)!;
                    var fExpr = ReadNode(r)!;
                    var op = ReadToken(r);
                    return new TernaryNode(cond, tExpr, fExpr, op);
                }
                case AstNodeType.ImportAll:
                {
                    var spec = ReadModuleSpecifier(r);
                    return new ImportAllNode(spec, ps, pe);
                }
                case AstNodeType.ImportSelective:
                {
                    var spec = ReadModuleSpecifier(r);
                    int n = r.ReadI32();
                    var names = new List<Token>(n);
                    for (int i = 0; i < n; i++) names.Add(ReadToken(r));
                    return new ImportSelectiveNode(spec, names, ps, pe);
                }
                case AstNodeType.ImportAlias:
                {
                    var spec = ReadModuleSpecifier(r);
                    var alias = ReadToken(r);
                    return new ImportAliasNode(spec, alias, ps, pe);
                }
                default:
                    throw new InvalidDataException(
                        $"rac: ModuleBytecode contains node type {nt} not supported by this loader");
            }
        }

        // --- FunctionDefinition (the gnarly one) --------------------------
        private static void WriteFunctionDefinition(RacBinaryWriter w, FunctionDefinitionNode fd)
        {
            // Resolver outputs — required so the load-time lazy IR-compile
            // (GetOrCompileBody) can pick up where the build-time resolver
            // left off. Without these, FrameId is -1 and the lazy compile
            // refuses to run, producing the "function 'X' has no executable
            // body" error at first call.
            w.WriteI32(fd.FrameId);
            if (fd.ParamBindings == null) w.WriteI32(-1);
            else
            {
                w.WriteI32(fd.ParamBindings.Length);
                for (int i = 0; i < fd.ParamBindings.Length; i++)
                    w.WriteI32(fd.ParamBindings[i].Raw);
            }
            // VarNameTok? (Token)
            if (fd.VarNameTok == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteToken(w, fd.VarNameTok.Value); }
            // ArgNameToks
            w.WriteI32(fd.ArgNameToks.Count);
            foreach (var t in fd.ArgNameToks) WriteToken(w, t);
            // ArgTypes
            w.WriteI32(fd.ArgTypes.Count);
            foreach (var td in fd.ArgTypes) WriteOptionalTypeDescriptor(w, td);
            // IsRefParams
            w.WriteI32(fd.IsRefParams.Count);
            foreach (var b in fd.IsRefParams) w.WriteU8(b ? (byte)1 : (byte)0);
            // ParamDefaults
            w.WriteI32(fd.ParamDefaults.Count);
            foreach (var n in fd.ParamDefaults)
            {
                if (n == null) w.WriteU8(0);
                else { w.WriteU8(1); WriteNode(w, n); }
            }
            // ParamAnnotations
            w.WriteI32(fd.ParamAnnotations.Count);
            foreach (var anns in fd.ParamAnnotations)
            {
                if (anns == null || anns.Count == 0) { w.WriteU8(0); }
                else
                {
                    w.WriteU8(1);
                    w.WriteI32(anns.Count);
                    foreach (var a in anns) WriteAnnotationApplication(w, a);
                }
            }
            // HasVarArgs / VarArgNameTok / VarArgType
            w.WriteU8(fd.HasVarArgs ? (byte)1 : (byte)0);
            if (fd.VarArgNameTok == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteToken(w, fd.VarArgNameTok.Value); }
            WriteOptionalTypeDescriptor(w, fd.VarArgType);
            // ReturnType
            WriteOptionalTypeDescriptor(w, fd.ReturnType);
            // BodyNode
            if (fd.BodyNode == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteNode(w, fd.BodyNode); }
            // Bits
            byte bits = 0;
            if (fd.ShouldAutoReturn) bits |= 0x01;
            if (fd.IsPublic) bits |= 0x02;
            if (fd.IsConstructor) bits |= 0x04;
            if (fd.IsOverride) bits |= 0x08;
            if (fd.IsAbstract) bits |= 0x10;
            if (fd.IsStatic) bits |= 0x20;
            if (fd.IsAsync) bits |= 0x40;
            if (fd.IsAsyncStream) bits |= 0x80;
            w.WriteU8(bits);
            // GenericTypeParams
            w.WriteI32(fd.GenericTypeParams.Count);
            foreach (var g in fd.GenericTypeParams) w.WriteString(g);
            // WhereConstraints / CaptureList — unsupported in v1.1.
            if (fd.WhereConstraints.Count != 0)
                throw new ModuleBytecodeUnsupportedException(
                    "FunctionDefinition with where-constraints not supported in v1.1");
            if (fd.CaptureList != null)
                throw new ModuleBytecodeUnsupportedException(
                    "FunctionDefinition with explicit capture-list not supported in v1.1");
            if (fd.VarArgAnnotations != null && fd.VarArgAnnotations.Count > 0)
                throw new ModuleBytecodeUnsupportedException(
                    "FunctionDefinition VarArgAnnotations not supported in v1.1");
        }

        private static FunctionDefinitionNode ReadFunctionDefinition(RacBinaryReader r)
        {
            int frameId = r.ReadI32();
            int pbLen = r.ReadI32();
            BindingId[]? paramBindings = null;
            if (pbLen >= 0)
            {
                paramBindings = new BindingId[pbLen];
                for (int i = 0; i < pbLen; i++) paramBindings[i] = new BindingId(r.ReadI32());
            }
            Token? nameTok = null;
            if (r.ReadU8() != 0) nameTok = ReadToken(r);
            int na = r.ReadI32();
            var argToks = new List<Token>(na);
            for (int i = 0; i < na; i++) argToks.Add(ReadToken(r));
            int nat = r.ReadI32();
            var argTypes = new List<TypeDescriptor?>(nat);
            for (int i = 0; i < nat; i++) argTypes.Add(ReadOptionalTypeDescriptor(r));
            int nrp = r.ReadI32();
            var isRef = new List<bool>(nrp);
            for (int i = 0; i < nrp; i++) isRef.Add(r.ReadU8() != 0);
            int npd = r.ReadI32();
            var defaults = new List<AstNode?>(npd);
            for (int i = 0; i < npd; i++)
            {
                AstNode? d = null;
                if (r.ReadU8() != 0) d = ReadNode(r);
                defaults.Add(d);
            }
            int npa = r.ReadI32();
            var paramAnns = new List<List<AnnotationApplicationNode>?>(npa);
            for (int i = 0; i < npa; i++)
            {
                if (r.ReadU8() == 0) paramAnns.Add(null);
                else
                {
                    int an = r.ReadI32();
                    var list = new List<AnnotationApplicationNode>(an);
                    for (int j = 0; j < an; j++) list.Add(ReadAnnotationApplication(r));
                    paramAnns.Add(list);
                }
            }
            bool hasVar = r.ReadU8() != 0;
            Token? varArgTok = null;
            if (r.ReadU8() != 0) varArgTok = ReadToken(r);
            var varArgType = ReadOptionalTypeDescriptor(r);
            var retType = ReadOptionalTypeDescriptor(r);
            AstNode? body = null;
            if (r.ReadU8() != 0) body = ReadNode(r);
            byte bits = r.ReadU8();
            bool sar = (bits & 0x01) != 0;
            bool pub = (bits & 0x02) != 0;
            bool ctor = (bits & 0x04) != 0;
            bool over = (bits & 0x08) != 0;
            bool abs = (bits & 0x10) != 0;
            bool stat = (bits & 0x20) != 0;
            bool async = (bits & 0x40) != 0;
            bool asyncStream = (bits & 0x80) != 0;
            int ngp = r.ReadI32();
            var generics = new List<string>(ngp);
            for (int i = 0; i < ngp; i++) generics.Add(r.ReadString() ?? "");

            var fd = new FunctionDefinitionNode(
                nameTok, argToks, argTypes, isRef, defaults,
                hasVar, varArgTok, varArgType, retType, body, sar,
                generics, pub, ctor, over, abs, stat,
                null, paramAnns, null);
            fd.IsAsync = async;
            fd.IsAsyncStream = asyncStream;
            fd.FrameId = frameId;
            fd.ParamBindings = paramBindings;
            return fd;
        }

        // --- Annotations -------------------------------------------------
        private static void WriteAnnotationApplication(RacBinaryWriter w, AnnotationApplicationNode a)
        {
            ModuleBytecodeIo.WritePosition(w, a.PositionStart);
            ModuleBytecodeIo.WritePosition(w, a.PositionEnd);
            WriteToken(w, a.NameTok);
            w.WriteI32(a.PositionalArgs.Count);
            foreach (var p in a.PositionalArgs) WriteNode(w, p);
            w.WriteI32(a.NamedArgs.Count);
            foreach (var (nameTok, val) in a.NamedArgs)
            {
                WriteToken(w, nameTok);
                WriteNode(w, val);
            }
        }

        private static AnnotationApplicationNode ReadAnnotationApplication(RacBinaryReader r)
        {
            var ps = ModuleBytecodeIo.ReadPosition(r);
            var pe = ModuleBytecodeIo.ReadPosition(r);
            var name = ReadToken(r);
            int pn = r.ReadI32();
            var pos = new List<AstNode>(pn);
            for (int i = 0; i < pn; i++) { var node = ReadNode(r); if (node != null) pos.Add(node); }
            int nn = r.ReadI32();
            var named = new List<(Token, AstNode)>(nn);
            for (int i = 0; i < nn; i++)
            {
                var nt = ReadToken(r);
                var nv = ReadNode(r)!;
                named.Add((nt, nv));
            }
            return new AnnotationApplicationNode(name, pos, named, ps, pe);
        }

        // --- Tokens ------------------------------------------------------
        //
        // Token.Value tags:
        //   0 = null, 1 = string, 2 = Keyword, 3 = i64 (boxed long/int),
        //   4 = BigNumber. Anything else → unsupported.
        private const byte TokValueTag_Null = 0;
        private const byte TokValueTag_String = 1;
        private const byte TokValueTag_Keyword = 2;
        private const byte TokValueTag_I64 = 3;
        private const byte TokValueTag_BigNumber = 4;

        private static void WriteToken(RacBinaryWriter w, Token t)
        {
            w.WriteI32((int)t.Type);
            ModuleBytecodeIo.WritePosition(w, t.PositionStart);
            ModuleBytecodeIo.WritePosition(w, t.PositionEnd);
            switch (t.Value)
            {
                case null:
                    w.WriteU8(TokValueTag_Null);
                    return;
                case string s:
                    w.WriteU8(TokValueTag_String);
                    w.WriteString(s);
                    return;
                case Keyword k:
                    w.WriteU8(TokValueTag_Keyword);
                    w.WriteI32((int)k);
                    return;
                case int i:
                    w.WriteU8(TokValueTag_I64);
                    w.WriteI64(i);
                    return;
                case long l:
                    w.WriteU8(TokValueTag_I64);
                    w.WriteI64(l);
                    return;
                case BigInteger bi:
                    w.WriteU8(TokValueTag_BigNumber);
                    ModuleBytecodeIo.WriteBigInteger(w, bi);
                    ModuleBytecodeIo.WriteBigInteger(w, BigInteger.Zero);
                    return;
                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"Token.Value type {t.Value.GetType().Name} not supported");
            }
        }

        private static Token ReadToken(RacBinaryReader r)
        {
            var type = (TokenType)r.ReadI32();
            var ps = ModuleBytecodeIo.ReadPosition(r);
            var pe = ModuleBytecodeIo.ReadPosition(r);
            byte tag = r.ReadU8();
            object? value = null;
            switch (tag)
            {
                case TokValueTag_Null: value = null; break;
                case TokValueTag_String: value = r.ReadString(); break;
                case TokValueTag_Keyword: value = (Keyword)r.ReadI32(); break;
                case TokValueTag_I64: value = r.ReadI64(); break;
                case TokValueTag_BigNumber:
                {
                    var u = ModuleBytecodeIo.ReadBigInteger(r);
                    var s = ModuleBytecodeIo.ReadBigInteger(r);
                    // Lexer emits raw INT/FLOAT digit strings as Token.Value
                    // (string). The visitor parses them per-visit via
                    // BigNumber.Parse. The BigNumber tag is reserved for
                    // future direct numeric token encoding; currently
                    // round-trips through the string form by formatting
                    // unscaled with scale zero.
                    value = u.ToString();
                    _ = s;
                    break;
                }
                default:
                    throw new InvalidDataException($"rac: unknown token value tag 0x{tag:X2}");
            }
            return new Token(type, value, ps, pe);
        }

        // --- TypeDescriptor ---------------------------------------------
        private static void WriteOptionalTypeDescriptor(RacBinaryWriter w, TypeDescriptor? td)
        {
            if (td == null) { w.WriteU8(0); return; }
            w.WriteU8(1);
            WriteTypeDescriptor(w, td);
        }

        private static TypeDescriptor? ReadOptionalTypeDescriptor(RacBinaryReader r)
        {
            if (r.ReadU8() == 0) return null;
            return ReadTypeDescriptor(r);
        }

        private static void WriteTypeDescriptor(RacBinaryWriter w, TypeDescriptor td)
        {
            // Refuse exotic shapes — keep v1.1 honest.
            if (td.IsFunctionType || td.IsUnionType)
                throw new ModuleBytecodeUnsupportedException(
                    $"TypeDescriptor function/union shapes not supported in v1.1");
            if (td.Lifetime != null)
                throw new ModuleBytecodeUnsupportedException(
                    $"TypeDescriptor lifetimes not supported in v1.1");

            w.WriteString(td.Name);
            w.WriteU8(td.IsTypeParameter ? (byte)1 : (byte)0);
            w.WriteString(td.TypeParameterName ?? "");
            w.WriteU8(td.IsRefType ? (byte)1 : (byte)0);
            w.WriteU8(td.IsMutableRef ? (byte)1 : (byte)0);
            if (td.RefElementType == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteTypeDescriptor(w, td.RefElementType); }
            w.WriteI32(td.GenericArgs?.Count ?? 0);
            if (td.GenericArgs != null)
                foreach (var g in td.GenericArgs) WriteTypeDescriptor(w, g);
        }

        private static TypeDescriptor ReadTypeDescriptor(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isParam = r.ReadU8() != 0;
            string paramName = r.ReadString() ?? "";
            bool isRef = r.ReadU8() != 0;
            bool isMut = r.ReadU8() != 0;
            TypeDescriptor? refElem = null;
            if (r.ReadU8() != 0) refElem = ReadTypeDescriptor(r);
            int gn = r.ReadI32();
            List<TypeDescriptor>? generics = null;
            if (gn > 0)
            {
                generics = new List<TypeDescriptor>(gn);
                for (int i = 0; i < gn; i++) generics.Add(ReadTypeDescriptor(r));
            }
            // TypeParameter form has its own factory.
            if (isParam)
            {
                return TypeDescriptor.TypeParameter(paramName);
            }
            return new TypeDescriptor(name, generics, isRef, refElem, isMut, null);
        }

        // --- ModuleSpecifier --------------------------------------------
        private static void WriteModuleSpecifier(RacBinaryWriter w, ModuleSpecifier ms)
        {
            w.WriteI32((int)ms.Kind);
            // RawPath populated for StringLiteral, null for Dotted.
            w.WriteString(ms.RawPath ?? "");
            int segCount = ms.Segments?.Count ?? 0;
            w.WriteI32(segCount);
            if (ms.Segments != null)
                foreach (var s in ms.Segments) w.WriteString(s);
        }

        private static ModuleSpecifier ReadModuleSpecifier(RacBinaryReader r)
        {
            var kind = (ModuleSpecifierKind)r.ReadI32();
            string raw = r.ReadString() ?? "";
            int segCount = r.ReadI32();
            var segs = new List<string>(segCount);
            for (int i = 0; i < segCount; i++) segs.Add(r.ReadString() ?? "");
            switch (kind)
            {
                case ModuleSpecifierKind.StringLiteral:
                    return ModuleSpecifier.FromStringLiteral(raw);
                case ModuleSpecifierKind.Dotted:
                    return ModuleSpecifier.FromDotted(segs);
                default:
                    throw new InvalidDataException($"rac: unknown ModuleSpecifierKind {kind}");
            }
        }
    }
}

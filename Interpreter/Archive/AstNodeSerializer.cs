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
using RaLanguage.Parser.Nodes.Asm;
using RaLanguage.Parser.Nodes.Async;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;
using RaLanguage.Types.Formatting;
using ClassesOperatorDefinitionNode = RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode;

namespace RaLanguage.Interpreter.Archive
{
    // v1.2 (#1) Direct-bytecode payload covers every AstNodeType the parser
    // produces — 96 kinds at the time of writing. Each node is written as:
    //
    //   u8 tag                 0xFF == null, otherwise (AstNodeType + 1)
    //   Position start / end
    //   u8 hasAnnotations + List<AnnotationApplicationNode>?
    //   per-NodeType payload (see Write/ReadXxx methods)
    //
    // The serialiser refuses no AstNodeType: a missing case in the switch
    // is the only path to ModuleBytecodeUnsupportedException. Add a case
    // to BOTH switches whenever a new AstNodeType lands.
    public static class AstNodeSerializer
    {
        private const byte NullTag = 0xFF;

        // v4 (#pre-compiled children): thread-local pool + version
        // handed in by ModuleBytecodeIo.Serialize / Deserialize so
        // FunctionDefinitionNode (and the sibling method nodes) can
        // recurse into ModuleBytecodeIo.WriteInlineRaFunction /
        // ReadInlineRaFunction without each call site re-threading
        // the pool. Cleared on the way out of the top-level call.
        [ThreadStatic] internal static SharedConstPoolBuilder? WriterPool;
        [ThreadStatic] internal static SharedConstPool? ReaderPool;
        [ThreadStatic] internal static ushort WriterVersion;
        [ThreadStatic] internal static ushort ReaderVersion;

        public static void WriteNode(RacBinaryWriter w, AstNode? node)
        {
            if (node == null)
            {
                w.WriteU8(NullTag);
                return;
            }
            int t = (int)node.NodeType + 1;
            if (t < 0 || t > 254)
                throw new ModuleBytecodeUnsupportedException(
                    $"AstNodeType {node.NodeType} out of byte range");
            w.WriteU8((byte)t);
            ModuleBytecodeIo.WritePosition(w, node.PositionStart);
            ModuleBytecodeIo.WritePosition(w, node.PositionEnd);

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

        // --- Payload dispatch -------------------------------------------------
        private static void WritePayload(RacBinaryWriter w, AstNode node)
        {
            switch (node)
            {
                // ---- Primitives ----
                case NumberNode n: WriteToken(w, n.Tok); return;
                case StringNode s:
                    w.WriteI32(s.Parts.Count);
                    foreach (var p in s.Parts) WriteNode(w, p);
                    return;
                case StringTextNode st: w.WriteString(st.Text); return;
                case NullNode nul: WriteToken(w, nul.Token); return;
                case BooleanNode b: WriteToken(w, b.Token); return;
                case ListNode lst:
                    w.WriteI32(lst.ElementNodes.Count);
                    foreach (var e in lst.ElementNodes) WriteNode(w, e);
                    return;
                case SetNode setN:
                    w.WriteI32(setN.ElementNodes.Count);
                    foreach (var e in setN.ElementNodes) WriteNode(w, e);
                    return;
                case TupleNode tup:
                    w.WriteI32(tup.ElementNodes.Count);
                    foreach (var e in tup.ElementNodes) WriteNode(w, e);
                    return;
                case MapNode mp:
                    w.WriteI32(mp.Pairs.Count);
                    foreach (var (k, v) in mp.Pairs) { WriteNode(w, k); WriteNode(w, v); }
                    return;
                case FormattedInterpolationNode fi:
                    WriteNode(w, fi.Expression);
                    WriteFormatSpec(w, fi.FormatSpec);
                    w.WriteString(fi.RawSpec);
                    return;
                case RegexLiteralNode rg:
                    w.WriteString(rg.Pattern);
                    w.WriteString(rg.Flags);
                    return;

                // ---- Variables ----
                case VariableAccessNode va: WriteToken(w, va.VarNameTok); return;
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
                case VariableDeleteNode vdel:
                    w.WriteI32(vdel.Tokens.Count);
                    foreach (var t in vdel.Tokens) WriteToken(w, t);
                    return;
                case ListAccessNode la: WriteNode(w, la.Target); WriteNode(w, la.Index); return;
                case ListAssignmentNode las:
                    WriteNode(w, las.Target);
                    WriteToken(w, las.AssignmentToken);
                    WriteNode(w, las.Value);
                    return;

                // ---- Operations ----
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
                case BorrowNode brw:
                    WriteNode(w, brw.Target);
                    w.WriteU8(brw.IsMutable ? (byte)1 : (byte)0);
                    w.WriteString(brw.Lifetime ?? "");
                    w.WriteU8(brw.Lifetime != null ? (byte)1 : (byte)0);
                    return;
                case DereferenceNode dn: WriteNode(w, dn.Target); return;
                case DereferenceAssignmentNode dna:
                    WriteNode(w, dna.RefTarget);
                    WriteToken(w, dna.AssignmentToken);
                    WriteNode(w, dna.ValueNode);
                    return;
                case CastNode c: WriteNode(w, c.Expression); WriteTypeDescriptor(w, c.TargetType); return;
                case IsTypeNode ist:
                    WriteNode(w, ist.Expression);
                    WriteTypeDescriptor(w, ist.TestedType);
                    w.WriteU8(ist.Negated ? (byte)1 : (byte)0);
                    return;
                case NullCoalescingNode nc:
                    WriteNode(w, nc.Left);
                    WriteNode(w, nc.Right);
                    WriteToken(w, nc.Operator);
                    return;
                case PassNode: return;
                case PipelineNode pp:
                    WriteNode(w, pp.LeftNode);
                    WriteNode(w, pp.RightNode);
                    WriteToken(w, pp.PipeToken);
                    return;
                case RangeNode rng:
                    WriteNode(w, rng.Start);
                    WriteNode(w, rng.End);
                    WriteToken(w, rng.Operator);
                    if (rng.Step == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rng.Step); }
                    return;
                case SpreadNode spr:
                    WriteToken(w, spr.SpreadToken);
                    WriteNode(w, spr.Expression);
                    return;
                case TernaryNode tern:
                    WriteNode(w, tern.Condition);
                    WriteNode(w, tern.TrueExpression);
                    WriteNode(w, tern.FalseExpression);
                    WriteToken(w, tern.OperatorToken);
                    return;
                case WithExpressionNode wexp:
                    WriteNode(w, wexp.Receiver);
                    w.WriteI32(wexp.Updates.Count);
                    foreach (var (nameTok, val) in wexp.Updates) { WriteToken(w, nameTok); WriteNode(w, val); }
                    return;

                // ---- Statements ----
                case IfNode ifn:
                    w.WriteI32(ifn.Cases.Count);
                    foreach (var (cond, body, srn) in ifn.Cases)
                    { WriteNode(w, cond); WriteNode(w, body); w.WriteU8(srn ? (byte)1 : (byte)0); }
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
                    { WriteNode(w, cond); WriteNode(w, body); w.WriteU8(srn ? (byte)1 : (byte)0); }
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
                case SuperForNode sfor:
                    w.WriteI32(sfor.InitializationNodes.Count);
                    foreach (var node2 in sfor.InitializationNodes) WriteNode(w, node2);
                    w.WriteI32(sfor.ConditionNodes.Count);
                    foreach (var node2 in sfor.ConditionNodes) WriteNode(w, node2);
                    w.WriteI32(sfor.StepNodes.Count);
                    foreach (var node2 in sfor.StepNodes) WriteNode(w, node2);
                    WriteNode(w, sfor.BodyNode);
                    w.WriteU8(sfor.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;
                case SwitchNode sw:
                    WriteNode(w, sw.Expression);
                    w.WriteI32(sw.Cases.Count);
                    foreach (var ca in sw.Cases) WriteNode(w, ca);
                    return;
                case SwitchCaseNode sc:
                    w.WriteI32(sc.Labels.Count);
                    foreach (var lbl in sc.Labels) WriteNode(w, lbl);
                    w.WriteU8(sc.IsDefault ? (byte)1 : (byte)0);
                    w.WriteU8((byte)sc.Separator);
                    if (sc.Body == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, sc.Body); }
                    return;
                case RetryNode rt:
                    WriteNode(w, rt.CountNode);
                    WriteNode(w, rt.BodyNode);
                    if (rt.DelayNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rt.DelayNode); }
                    if (rt.ElseNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rt.ElseNode); }
                    return;
                case ThrowNode th: WriteNode(w, th.Expression); return;

                // ---- Iterations ----
                case BreakNode:
                case ContinueNode: return;
                case YieldNode yn: WriteNode(w, yn.Expression); return;

                // ---- Functions ----
                case FunctionDefinitionNode fd: WriteFunctionDefinition(w, fd); return;
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
                case ReturnNode ret:
                    if (ret.NodeToReturn == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, ret.NodeToReturn); }
                    return;
                case DelegateDefinitionNode dd:
                    WriteToken(w, dd.NameTok);
                    w.WriteI32(dd.GenericTypeParams.Count);
                    foreach (var g in dd.GenericTypeParams) w.WriteString(g);
                    WriteWhereConstraintList(w, dd.WhereConstraints);
                    WriteTypeDescriptor(w, dd.SignatureType);
                    w.WriteU8(dd.IsPublic ? (byte)1 : (byte)0);
                    return;

                // ---- Special ----
                case ScopeNode sco:
                    w.WriteI32(sco.Nodes.Count);
                    foreach (var ch in sco.Nodes) WriteNode(w, ch);
                    return;
                case TypeofNode tn: WriteNode(w, tn.Node); return;
                case NameofNode nn: WriteToken(w, nn.Token); return;
                case TryNode tr:
                    WriteNode(w, tr.TryBody);
                    if (tr.CatchVarTok == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteToken(w, tr.CatchVarTok.Value); }
                    if (tr.CatchBody == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, tr.CatchBody); }
                    if (tr.FinallyBody == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, tr.FinallyBody); }
                    return;
                case LabelNode lab:
                    WriteToken(w, lab.Token);
                    WriteNode(w, lab.Statements);
                    return;
                case GotoNode gt: WriteToken(w, gt.VarName); return;

                // ---- Structs / Classes / Records / Self ----
                case MemberAccessNode ma:
                    WriteNode(w, ma.TargetNode);
                    WriteToken(w, ma.MemberTok);
                    return;
                case MemberAssignmentNode masg:
                    WriteNode(w, masg.TargetNode);
                    WriteToken(w, masg.AssignmentToken);
                    WriteNode(w, masg.ValueNode);
                    return;
                case SelfNode: return;
                case SuperNode: return;
                case StructDefinitionNode sdef:
                    WriteToken(w, sdef.NameTok);
                    w.WriteU8(sdef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteI32(sdef.Fields.Count);
                    foreach (var f in sdef.Fields) WriteNode(w, f);
                    w.WriteI32(sdef.Methods.Count);
                    foreach (var m in sdef.Methods) WriteNode(w, m);
                    w.WriteI32(sdef.Operators.Count);
                    foreach (var op in sdef.Operators) WriteNode(w, op);
                    WriteStringList(w, sdef.GenericTypeParams);
                    WriteWhereConstraintList(w, sdef.WhereConstraints);
                    w.WriteI32(sdef.Properties.Count);
                    foreach (var p in sdef.Properties) WriteNode(w, p);
                    w.WriteI32(sdef.Events.Count);
                    foreach (var ev in sdef.Events) WriteNode(w, ev);
                    return;
                case StructFieldDefinitionNode sfd:
                    w.WriteU8(sfd.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(sfd.IsStatic ? (byte)1 : (byte)0);
                    w.WriteU8(sfd.IsAbstract ? (byte)1 : (byte)0);
                    w.WriteU8(sfd.IsOverride ? (byte)1 : (byte)0);
                    WriteToken(w, sfd.NameTok);
                    WriteOptionalTypeDescriptor(w, sfd.FieldType);
                    if (sfd.DefaultValueNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, sfd.DefaultValueNode); }
                    w.WriteU8((byte)sfd.DeclarationType);
                    return;
                case StructMethodDefinitionNode smd:
                    WriteStructMethodPayload(w, smd);
                    return;
                case ClassDefinitionNode cdef:
                    WriteToken(w, cdef.NameTok);
                    w.WriteU8(cdef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(cdef.IsAbstract ? (byte)1 : (byte)0);
                    w.WriteU8(cdef.IsStatic ? (byte)1 : (byte)0);
                    WriteOptionalTypeDescriptor(w, cdef.BaseType);
                    w.WriteI32(cdef.ImplementedInterfaces.Count);
                    foreach (var ti in cdef.ImplementedInterfaces) WriteTypeDescriptor(w, ti);
                    w.WriteI32(cdef.WithTraits.Count);
                    foreach (var tr2 in cdef.WithTraits) WriteTypeDescriptor(w, tr2);
                    w.WriteI32(cdef.Fields.Count);
                    foreach (var f in cdef.Fields) WriteNode(w, f);
                    w.WriteI32(cdef.Methods.Count);
                    foreach (var m in cdef.Methods) WriteNode(w, m);
                    w.WriteI32(cdef.Operators.Count);
                    foreach (var op in cdef.Operators) WriteNode(w, op);
                    w.WriteI32(cdef.Properties.Count);
                    foreach (var p in cdef.Properties) WriteNode(w, p);
                    w.WriteI32(cdef.Events.Count);
                    foreach (var ev in cdef.Events) WriteNode(w, ev);
                    WriteStringList(w, cdef.GenericTypeParams);
                    WriteWhereConstraintList(w, cdef.WhereConstraints);
                    return;
                case ClassesOperatorDefinitionNode cop:
                    w.WriteU8(cop.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(cop.IsOverride ? (byte)1 : (byte)0);
                    w.WriteU8(cop.IsStatic ? (byte)1 : (byte)0);
                    WriteToken(w, cop.OperatorTok);
                    WriteToken(w, cop.ArgNameTok);
                    WriteOptionalTypeDescriptor(w, cop.ArgType);
                    WriteOptionalTypeDescriptor(w, cop.ReturnType);
                    WriteNode(w, cop.BodyNode);
                    w.WriteU8(cop.ShouldAutoReturn ? (byte)1 : (byte)0);
                    WriteStringList(w, cop.GenericTypeParams);
                    WriteWhereConstraintList(w, cop.WhereConstraints);
                    w.WriteI32(cop.FrameId);
                    WriteBindingIdArray(w, cop.ParamBindings);
                    // v4 (#pre-compiled children): optional CompiledBody.
                    WriteOptionalInlineRaFunction(w, cop.CompiledBody);
                    return;
                case ExtensionDefinitionNode ext:
                    WriteTypeDescriptor(w, ext.TargetType);
                    w.WriteU8(ext.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(ext.IsSealed ? (byte)1 : (byte)0);
                    w.WriteI32(ext.Methods.Count);
                    foreach (var m in ext.Methods) WriteNode(w, m);
                    w.WriteI32(ext.Properties.Count);
                    foreach (var p in ext.Properties) WriteNode(w, p);
                    w.WriteI32(ext.Operators.Count);
                    foreach (var op in ext.Operators) WriteNode(w, op);
                    w.WriteI32(ext.Events.Count);
                    foreach (var ev in ext.Events) WriteNode(w, ev);
                    w.WriteI32(ext.Indexers.Count);
                    foreach (var (m, isSet) in ext.Indexers)
                    {
                        WriteNode(w, m);
                        w.WriteU8(isSet ? (byte)1 : (byte)0);
                    }
                    w.WriteI32(ext.Fields.Count);
                    foreach (var fd in ext.Fields)
                    {
                        WriteNode(w, fd.Field);
                        w.WriteU8(fd.IsStaticField ? (byte)1 : (byte)0);
                        w.WriteU8(fd.IsLazy ? (byte)1 : (byte)0);
                    }
                    return;

                // ---- Records ----
                case RecordDefinitionNode rdef:
                    WriteToken(w, rdef.NameTok);
                    w.WriteU8(rdef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(rdef.IsRefRecord ? (byte)1 : (byte)0);
                    w.WriteU8(rdef.IsAbstract ? (byte)1 : (byte)0);
                    WriteOptionalTypeDescriptor(w, rdef.BaseType);
                    if (rdef.BaseArgs == null) w.WriteU8(0);
                    else
                    {
                        w.WriteU8(1);
                        w.WriteI32(rdef.BaseArgs.Count);
                        foreach (var ba in rdef.BaseArgs) WriteNode(w, ba);
                    }
                    w.WriteI32(rdef.PrimaryFields.Count);
                    foreach (var pf in rdef.PrimaryFields) WriteNode(w, pf);
                    w.WriteI32(rdef.Methods.Count);
                    foreach (var m in rdef.Methods) WriteNode(w, m);
                    w.WriteI32(rdef.Operators.Count);
                    foreach (var op in rdef.Operators) WriteNode(w, op);
                    w.WriteI32(rdef.Properties.Count);
                    foreach (var p in rdef.Properties) WriteNode(w, p);
                    w.WriteI32(rdef.Events.Count);
                    foreach (var ev in rdef.Events) WriteNode(w, ev);
                    WriteStringList(w, rdef.GenericTypeParams);
                    WriteWhereConstraintList(w, rdef.WhereConstraints);
                    w.WriteU8(rdef.AutoEquals ? (byte)1 : (byte)0);
                    w.WriteU8(rdef.AutoToString ? (byte)1 : (byte)0);
                    return;
                case RecordPrimaryFieldNode rpf:
                    WriteToken(w, rpf.NameTok);
                    WriteOptionalTypeDescriptor(w, rpf.FieldType);
                    if (rpf.DefaultValueNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rpf.DefaultValueNode); }
                    w.WriteU8(rpf.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(rpf.IsMutable ? (byte)1 : (byte)0);
                    return;

                // ---- Enums ----
                case EnumDefinitionNode edef:
                    WriteToken(w, edef.NameTok);
                    w.WriteI32(edef.Variants.Count);
                    foreach (var v in edef.Variants) WriteEnumVariantSpec(w, v);
                    WriteStringList(w, edef.GenericTypeParams);
                    WriteWhereConstraintList(w, edef.WhereConstraints);
                    return;
                case EnumAccessNode ea:
                    WriteNode(w, ea.EnumNode);
                    WriteToken(w, ea.MemberTok);
                    return;

                // ---- Interfaces ----
                case InterfaceDefinitionNode idef:
                    WriteToken(w, idef.NameTok);
                    w.WriteU8(idef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteI32(idef.Methods.Count);
                    foreach (var m in idef.Methods) WriteNode(w, m);
                    w.WriteI32(idef.Fields.Count);
                    foreach (var f in idef.Fields) WriteNode(w, f);
                    w.WriteI32(idef.Properties.Count);
                    foreach (var p in idef.Properties) WriteNode(w, p);
                    w.WriteI32(idef.Events.Count);
                    foreach (var ev in idef.Events) WriteNode(w, ev);
                    WriteStringList(w, idef.GenericTypeParams);
                    WriteWhereConstraintList(w, idef.WhereConstraints);
                    return;
                case InterfaceMethodSignatureNode ims:
                    WriteToken(w, ims.NameTok);
                    w.WriteI32(ims.ArgNameToks.Count);
                    foreach (var t in ims.ArgNameToks) WriteToken(w, t);
                    w.WriteI32(ims.ArgTypes.Count);
                    foreach (var td in ims.ArgTypes) WriteOptionalTypeDescriptor(w, td);
                    WriteOptionalTypeDescriptor(w, ims.ReturnType);
                    return;

                // ---- Traits ----
                case TraitDefinitionNode tdef:
                    WriteToken(w, tdef.NameTok);
                    w.WriteU8(tdef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteI32(tdef.Methods.Count);
                    foreach (var m in tdef.Methods) WriteNode(w, m);
                    w.WriteI32(tdef.Fields.Count);
                    foreach (var f in tdef.Fields) WriteNode(w, f);
                    w.WriteI32(tdef.Properties.Count);
                    foreach (var p in tdef.Properties) WriteNode(w, p);
                    w.WriteI32(tdef.Events.Count);
                    foreach (var ev in tdef.Events) WriteNode(w, ev);
                    WriteStringList(w, tdef.GenericTypeParams);
                    WriteWhereConstraintList(w, tdef.WhereConstraints);
                    return;
                case TraitMethodDefinitionNode tmd:
                    WriteTraitMethodPayload(w, tmd);
                    return;
                case CallableSignatureNode csn:
                    w.WriteI32(csn.ArgNameToks.Count);
                    foreach (var t in csn.ArgNameToks) WriteToken(w, t);
                    w.WriteI32(csn.ArgTypes.Count);
                    foreach (var td in csn.ArgTypes) WriteOptionalTypeDescriptor(w, td);
                    w.WriteI32(csn.IsRefParams.Count);
                    foreach (var b in csn.IsRefParams) w.WriteU8(b ? (byte)1 : (byte)0);
                    w.WriteI32(csn.ParamDefaults.Count);
                    foreach (var n2 in csn.ParamDefaults)
                    {
                        if (n2 == null) w.WriteU8(0);
                        else { w.WriteU8(1); WriteNode(w, n2); }
                    }
                    w.WriteU8(csn.HasVarArgs ? (byte)1 : (byte)0);
                    if (csn.VarArgNameTok == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteToken(w, csn.VarArgNameTok.Value); }
                    WriteOptionalTypeDescriptor(w, csn.VarArgType);
                    WriteOptionalTypeDescriptor(w, csn.ReturnType);
                    return;

                // ---- Properties ----
                case PropertyDefinitionNode pdef:
                    WriteToken(w, pdef.NameTok);
                    WriteOptionalTypeDescriptor(w, pdef.PropertyType);
                    if (pdef.DefaultValueNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, pdef.DefaultValueNode); }
                    w.WriteI32(pdef.Accessors.Count);
                    foreach (var ac in pdef.Accessors) WriteNode(w, ac);
                    w.WriteU8(pdef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(pdef.IsStatic ? (byte)1 : (byte)0);
                    w.WriteU8(pdef.IsAbstract ? (byte)1 : (byte)0);
                    w.WriteU8(pdef.IsOverride ? (byte)1 : (byte)0);
                    w.WriteU8(pdef.IsLazy ? (byte)1 : (byte)0);
                    return;
                case PropertyAccessorNode pac:
                    WriteToken(w, pac.KindTok);
                    w.WriteU8((byte)pac.Kind);
                    w.WriteU8((byte)pac.Visibility);
                    if (pac.BodyNode == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, pac.BodyNode); }
                    return;

                // ---- Events ----
                case EventDefinitionNode edef2:
                    WriteToken(w, edef2.NameTok);
                    w.WriteI32(edef2.PayloadParams.Count);
                    foreach (var pp in edef2.PayloadParams) WriteEventPayloadParam(w, pp);
                    w.WriteI32(edef2.Accessors.Count);
                    foreach (var ac in edef2.Accessors) WriteNode(w, ac);
                    w.WriteU8(edef2.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsStatic ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsAbstract ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsOverride ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsCancellable ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsTolerant ? (byte)1 : (byte)0);
                    w.WriteU8(edef2.IsAsync ? (byte)1 : (byte)0);
                    return;
                case EventAccessorNode eac:
                    WriteToken(w, eac.KindTok);
                    w.WriteU8((byte)eac.Kind);
                    w.WriteU8((byte)eac.Visibility);
                    return;

                // ---- Annotations ----
                case AnnotationDefinitionNode adef:
                    WriteToken(w, adef.NameTok);
                    w.WriteU8(adef.IsPublic ? (byte)1 : (byte)0);
                    w.WriteI32(adef.Parameters.Count);
                    foreach (var p in adef.Parameters) WriteAnnotationParameter(w, p);
                    return;
                case AnnotationApplicationNode aap:
                    WriteToken(w, aap.NameTok);
                    w.WriteI32(aap.PositionalArgs.Count);
                    foreach (var p in aap.PositionalArgs) WriteNode(w, p);
                    w.WriteI32(aap.NamedArgs.Count);
                    foreach (var (nameTok2, val) in aap.NamedArgs)
                    { WriteToken(w, nameTok2); WriteNode(w, val); }
                    return;

                // ---- Async ----
                case AwaitNode aw: WriteNode(w, aw.Expression); return;
                case SpawnNode spn: WriteNode(w, spn.Expression); return;
                case EmitNode em: WriteNode(w, em.Expression); return;
                case ForAwaitNode faw:
                    WriteToken(w, faw.VarNameToken);
                    WriteNode(w, faw.StreamNode);
                    WriteNode(w, faw.BodyNode);
                    w.WriteU8(faw.ShouldReturnNull ? (byte)1 : (byte)0);
                    return;

                // ---- Namespaces ----
                case NamespaceDeclarationNode nd:
                    w.WriteI32(nd.Segments.Count);
                    foreach (var seg in nd.Segments) WriteToken(w, seg);
                    WriteNode(w, nd.Body);
                    w.WriteU8(nd.IsFileScoped ? (byte)1 : (byte)0);
                    return;
                case UsingNamespaceNode un2:
                    w.WriteI32(un2.Segments.Count);
                    foreach (var seg in un2.Segments) WriteToken(w, seg);
                    if (un2.AliasTok == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteToken(w, un2.AliasTok.Value); }
                    return;

                // ---- Imports ----
                case ImportAllNode iall: WriteModuleSpecifier(w, iall.Specifier); return;
                case ImportSelectiveNode isel:
                    WriteModuleSpecifier(w, isel.Specifier);
                    w.WriteI32(isel.SymbolNames.Count);
                    foreach (var t in isel.SymbolNames) WriteToken(w, t);
                    return;
                case ImportAliasNode ial:
                    WriteModuleSpecifier(w, ial.Specifier);
                    WriteToken(w, ial.AliasTok);
                    return;

                // ---- Asm ----
                case AsmBlockNode asm:
                    w.WriteI32(asm.Parts.Count);
                    foreach (var p in asm.Parts) WriteNode(w, p);
                    w.WriteI32(asm.ReturnTypes.Count);
                    foreach (var s2 in asm.ReturnTypes) w.WriteString(s2);
                    return;
                case AsmTextPartNode atp: w.WriteString(atp.Text); return;
                case AsmInterpPartNode aip:
                    WriteNode(w, aip.Expr);
                    if (aip.TypeHint == null) w.WriteU8(0);
                    else { w.WriteU8(1); w.WriteString(aip.TypeHint); }
                    return;

                // ---- Patterns (only DestructuringDeclaration / Match / TryUnwrap hit this) ----
                case DestructuringDeclarationNode ddn:
                    WritePattern(w, ddn.Pattern);
                    WriteNode(w, ddn.Initializer);
                    w.WriteU8((byte)ddn.Kind);
                    WriteOptionalTypeDescriptor(w, ddn.DeclaredType);
                    w.WriteU8(ddn.IsPublic ? (byte)1 : (byte)0);
                    w.WriteU8(ddn.IsStatic ? (byte)1 : (byte)0);
                    return;
                case MatchNode mn:
                    WriteNode(w, mn.Scrutinee);
                    w.WriteI32(mn.Arms.Count);
                    foreach (var arm in mn.Arms) WriteMatchArm(w, arm);
                    return;
                case TryUnwrapNode tu: WriteNode(w, tu.Target); return;

                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"AstNode payload writer for {node.NodeType} ({node.GetType().Name}) not implemented");
            }
        }

        private static AstNode? ReadPayload(RacBinaryReader r, AstNodeType nt, Position ps, Position pe)
        {
            switch (nt)
            {
                // ---- Primitives ----
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
                case AstNodeType.Null: return new NullNode(ReadToken(r));
                case AstNodeType.Boolean: return new BooleanNode(ReadToken(r));
                case AstNodeType.List:
                {
                    int n = r.ReadI32();
                    var items = new List<AstNode>(n);
                    for (int i = 0; i < n; i++) { var c = ReadNode(r); if (c != null) items.Add(c); }
                    return new ListNode(items, ps, pe);
                }
                case AstNodeType.Set:
                {
                    int n = r.ReadI32();
                    var items = new List<AstNode>(n);
                    for (int i = 0; i < n; i++) { var c = ReadNode(r); if (c != null) items.Add(c); }
                    return new SetNode(items, ps, pe);
                }
                case AstNodeType.Tuple:
                {
                    int n = r.ReadI32();
                    var items = new List<AstNode>(n);
                    for (int i = 0; i < n; i++) { var c = ReadNode(r); if (c != null) items.Add(c); }
                    return new TupleNode(items, ps, pe);
                }
                case AstNodeType.Map:
                {
                    int n = r.ReadI32();
                    var pairs = new List<(AstNode, AstNode)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var k = ReadNode(r)!;
                        var v = ReadNode(r)!;
                        pairs.Add((k, v));
                    }
                    return new MapNode(pairs, ps, pe);
                }
                case AstNodeType.FormattedInterpolation:
                {
                    var expr = ReadNode(r)!;
                    var spec = ReadFormatSpec(r);
                    var raw = r.ReadString() ?? "";
                    return new FormattedInterpolationNode(expr, spec, raw, ps, pe);
                }
                case AstNodeType.RegexLiteral:
                {
                    var pat = r.ReadString() ?? "";
                    var fl = r.ReadString() ?? "";
                    return new RegexLiteralNode(pat, fl, ps, pe);
                }

                // ---- Variables ----
                case AstNodeType.VariableAccess: return new VariableAccessNode(ReadToken(r));
                case AstNodeType.VariableAssignment:
                {
                    var name = ReadToken(r);
                    var op = ReadToken(r);
                    var val = ReadNode(r)!;
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
                case AstNodeType.VariableDelete:
                {
                    int n = r.ReadI32();
                    var toks = new List<Token>(n);
                    for (int i = 0; i < n; i++) toks.Add(ReadToken(r));
                    return new VariableDeleteNode(toks);
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

                // ---- Operations ----
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
                case AstNodeType.Borrow:
                {
                    var tgt = ReadNode(r)!;
                    bool isMut = r.ReadU8() != 0;
                    var lt = r.ReadString() ?? "";
                    bool hasLt = r.ReadU8() != 0;
                    return new BorrowNode(tgt, isMut, ps, pe, hasLt ? lt : null);
                }
                case AstNodeType.Dereference:
                {
                    var inner = ReadNode(r)!;
                    return new DereferenceNode(inner, ps, pe);
                }
                case AstNodeType.DereferenceAssignment:
                {
                    var refT = ReadNode(r)!;
                    var op = ReadToken(r);
                    var v = ReadNode(r)!;
                    return new DereferenceAssignmentNode(refT, op, v, ps, pe);
                }
                case AstNodeType.Cast:
                {
                    var inner = ReadNode(r)!;
                    var td = ReadTypeDescriptor(r);
                    return new CastNode(inner, td);
                }
                case AstNodeType.IsType:
                {
                    var inner = ReadNode(r)!;
                    var td = ReadTypeDescriptor(r);
                    bool neg = r.ReadU8() != 0;
                    return new IsTypeNode(inner, td, neg);
                }
                case AstNodeType.NullCoalescing:
                {
                    var l = ReadNode(r)!;
                    var rn = ReadNode(r)!;
                    var op = ReadToken(r);
                    return new NullCoalescingNode(l, rn, op);
                }
                case AstNodeType.Pass: return new PassNode(ps, pe);
                case AstNodeType.Pipeline:
                {
                    var l = ReadNode(r)!;
                    var rn = ReadNode(r)!;
                    var pt = ReadToken(r);
                    return new PipelineNode(l, rn, pt);
                }
                case AstNodeType.Range:
                {
                    var s = ReadNode(r)!;
                    var e = ReadNode(r)!;
                    var op = ReadToken(r);
                    AstNode? step = null;
                    if (r.ReadU8() != 0) step = ReadNode(r);
                    return new RangeNode(s, e, op, step);
                }
                case AstNodeType.Spread:
                {
                    var st = ReadToken(r);
                    var ex = ReadNode(r)!;
                    return new SpreadNode(st, ex);
                }
                case AstNodeType.Ternary:
                {
                    var cond = ReadNode(r)!;
                    var t = ReadNode(r)!;
                    var f = ReadNode(r)!;
                    var op = ReadToken(r);
                    return new TernaryNode(cond, t, f, op);
                }
                case AstNodeType.WithExpression:
                {
                    var recv = ReadNode(r)!;
                    int n = r.ReadI32();
                    var ups = new List<(Token, AstNode)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var nt2 = ReadToken(r);
                        var v = ReadNode(r)!;
                        ups.Add((nt2, v));
                    }
                    return new WithExpressionNode(recv, ups);
                }

                // ---- Statements ----
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
                case AstNodeType.SuperFor:
                {
                    int ni = r.ReadI32();
                    var inits = new List<AstNode>(ni);
                    for (int i = 0; i < ni; i++) inits.Add(ReadNode(r)!);
                    int nc = r.ReadI32();
                    var conds = new List<AstNode>(nc);
                    for (int i = 0; i < nc; i++) conds.Add(ReadNode(r)!);
                    int ns = r.ReadI32();
                    var steps = new List<AstNode>(ns);
                    for (int i = 0; i < ns; i++) steps.Add(ReadNode(r)!);
                    var body = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new SuperForNode(inits, conds, steps, body, srn);
                }
                case AstNodeType.Switch:
                {
                    var expr = ReadNode(r)!;
                    int n = r.ReadI32();
                    var cases = new List<SwitchCaseNode>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var ca = ReadNode(r) as SwitchCaseNode;
                        if (ca == null) throw new InvalidDataException("rac: non-SwitchCase in Switch.Cases");
                        cases.Add(ca);
                    }
                    return new SwitchNode(expr, cases, ps, pe);
                }
                case AstNodeType.SwitchCase:
                {
                    int n = r.ReadI32();
                    var labels = new List<AstNode>(n);
                    for (int i = 0; i < n; i++) labels.Add(ReadNode(r)!);
                    bool isDef = r.ReadU8() != 0;
                    var sep = (SwitchCaseSeparator)r.ReadU8();
                    AstNode? body = null;
                    if (r.ReadU8() != 0) body = ReadNode(r);
                    return new SwitchCaseNode(labels, isDef, sep, body, ps, pe);
                }
                case AstNodeType.Retry:
                {
                    var cnt = ReadNode(r)!;
                    var body = ReadNode(r)!;
                    AstNode? delay = null;
                    if (r.ReadU8() != 0) delay = ReadNode(r);
                    AstNode? els = null;
                    if (r.ReadU8() != 0) els = ReadNode(r);
                    return new RetryNode(cnt, body, delay, els);
                }
                case AstNodeType.Throw:
                {
                    var expr = ReadNode(r)!;
                    return new ThrowNode(expr, ps, pe);
                }

                // ---- Iterations ----
                case AstNodeType.Break: return new BreakNode(ps, pe);
                case AstNodeType.Continue: return new ContinueNode(ps, pe);
                case AstNodeType.Yield:
                {
                    var ex = ReadNode(r)!;
                    return new YieldNode(ex, ps, pe);
                }

                // ---- Functions ----
                case AstNodeType.FunctionDefinition: return ReadFunctionDefinition(r);
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
                case AstNodeType.Return:
                {
                    AstNode? inner = null;
                    if (r.ReadU8() != 0) inner = ReadNode(r);
                    return new ReturnNode(inner, ps, pe);
                }
                case AstNodeType.DelegateDefinition:
                {
                    var name = ReadToken(r);
                    int ng = r.ReadI32();
                    var generics = new List<string>(ng);
                    for (int i = 0; i < ng; i++) generics.Add(r.ReadString() ?? "");
                    var wc = ReadWhereConstraintList(r);
                    var sig = ReadTypeDescriptor(r);
                    bool isPub = r.ReadU8() != 0;
                    return new DelegateDefinitionNode(name, generics, wc, sig, isPub);
                }

                // ---- Special ----
                case AstNodeType.Scope:
                {
                    int n = r.ReadI32();
                    var children = new List<AstNode>(n);
                    for (int i = 0; i < n; i++) { var c = ReadNode(r); if (c != null) children.Add(c); }
                    return new ScopeNode(children, ps, pe);
                }
                case AstNodeType.Typeof:
                {
                    var inner = ReadNode(r)!;
                    return new TypeofNode(inner);
                }
                case AstNodeType.Nameof: return new NameofNode(ReadToken(r));
                case AstNodeType.Try:
                {
                    var tryB = ReadNode(r)!;
                    Token? cv = null;
                    if (r.ReadU8() != 0) cv = ReadToken(r);
                    AstNode? cb = null;
                    if (r.ReadU8() != 0) cb = ReadNode(r);
                    AstNode? fb = null;
                    if (r.ReadU8() != 0) fb = ReadNode(r);
                    return new TryNode(tryB, cv, cb, fb);
                }
                case AstNodeType.Label:
                {
                    var t = ReadToken(r);
                    var s = ReadNode(r)!;
                    return new LabelNode(t, s);
                }
                case AstNodeType.Goto:
                {
                    var t = ReadToken(r);
                    return new GotoNode(ps, t);
                }

                // ---- Structs / Classes / Records / Self ----
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
                case AstNodeType.Self: return new SelfNode(ps, pe);
                case AstNodeType.Super: return new SuperNode(ps, pe);
                case AstNodeType.StructDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    int nf = r.ReadI32();
                    var fields = new List<StructFieldDefinitionNode>(nf);
                    for (int i = 0; i < nf; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructFieldDefinitionNode sf) fields.Add(sf);
                        else throw new InvalidDataException("rac: expected StructFieldDefinitionNode");
                    }
                    int nm = r.ReadI32();
                    var methods = new List<StructMethodDefinitionNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructMethodDefinitionNode sm) methods.Add(sm);
                        else throw new InvalidDataException("rac: expected StructMethodDefinitionNode");
                    }
                    int no = r.ReadI32();
                    var ops = new List<ClassesOperatorDefinitionNode>(no);
                    for (int i = 0; i < no; i++)
                    {
                        var node = ReadNode(r);
                        if (node is ClassesOperatorDefinitionNode od) ops.Add(od);
                        else throw new InvalidDataException("rac: expected OperatorDefinitionNode");
                    }
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    return new StructDefinitionNode(name, isPub, fields, methods, ops, generics, wc, props, evs);
                }
                case AstNodeType.StructFieldDefinition:
                {
                    bool isPub = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    bool isAbs = r.ReadU8() != 0;
                    bool isOver = r.ReadU8() != 0;
                    var name = ReadToken(r);
                    var td = ReadOptionalTypeDescriptor(r);
                    AstNode? def = null;
                    if (r.ReadU8() != 0) def = ReadNode(r);
                    var dt = (VariableDeclarationType)r.ReadU8();
                    return new StructFieldDefinitionNode(isPub, name, td, def, isStat, isAbs, isOver, dt);
                }
                case AstNodeType.StructMethodDefinition: return ReadStructMethod(r);
                case AstNodeType.ClassDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    bool isAbs = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    var baseT = ReadOptionalTypeDescriptor(r);
                    int ni = r.ReadI32();
                    var ifs = new List<TypeDescriptor>(ni);
                    for (int i = 0; i < ni; i++) ifs.Add(ReadTypeDescriptor(r));
                    int nt2 = r.ReadI32();
                    var traits = new List<TypeDescriptor>(nt2);
                    for (int i = 0; i < nt2; i++) traits.Add(ReadTypeDescriptor(r));
                    int nf = r.ReadI32();
                    var fields = new List<StructFieldDefinitionNode>(nf);
                    for (int i = 0; i < nf; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructFieldDefinitionNode sf) fields.Add(sf);
                        else throw new InvalidDataException("rac: expected StructFieldDefinitionNode");
                    }
                    int nm = r.ReadI32();
                    var methods = new List<FunctionDefinitionNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is FunctionDefinitionNode fdn) methods.Add(fdn);
                        else throw new InvalidDataException("rac: expected FunctionDefinitionNode");
                    }
                    int no = r.ReadI32();
                    var ops = new List<ClassesOperatorDefinitionNode>(no);
                    for (int i = 0; i < no; i++)
                    {
                        var node = ReadNode(r);
                        if (node is ClassesOperatorDefinitionNode od) ops.Add(od);
                        else throw new InvalidDataException("rac: expected OperatorDefinitionNode");
                    }
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    return new ClassDefinitionNode(name, isPub, isAbs, isStat, baseT, ifs, traits,
                        fields, methods, ops, generics, wc, props, evs);
                }
                case AstNodeType.OperatorDefinition:
                {
                    bool isPub = r.ReadU8() != 0;
                    bool isOver = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    var opTok = ReadToken(r);
                    var argTok = ReadToken(r);
                    var argT = ReadOptionalTypeDescriptor(r);
                    var retT = ReadOptionalTypeDescriptor(r);
                    var body = ReadNode(r)!;
                    bool sar = r.ReadU8() != 0;
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    var node = new ClassesOperatorDefinitionNode(isPub, isOver, isStat,
                        opTok, argTok, argT, retT, body, sar, generics, wc);
                    node.FrameId = r.ReadI32();
                    node.ParamBindings = ReadBindingIdArray(r);
                    var compiled = ReadOptionalInlineRaFunction(r);
                    if (compiled != null)
                    {
                        node.CompiledBody = compiled;
                        node.IrCompileTried = true;
                    }
                    return node;
                }
                case AstNodeType.ExtensionDefinition:
                {
                    var tgt = ReadTypeDescriptor(r);
                    bool isPub = r.ReadU8() != 0;
                    bool isSealed = r.ReadU8() != 0;
                    int nm = r.ReadI32();
                    var methods = new List<FunctionDefinitionNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is FunctionDefinitionNode fdn) methods.Add(fdn);
                        else throw new InvalidDataException("rac: expected FunctionDefinitionNode");
                    }
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int no = r.ReadI32();
                    var ops = new List<ClassesOperatorDefinitionNode>(no);
                    for (int i = 0; i < no; i++)
                    {
                        var node = ReadNode(r);
                        if (node is ClassesOperatorDefinitionNode od) ops.Add(od);
                        else throw new InvalidDataException("rac: expected OperatorDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    int nx = r.ReadI32();
                    var idx = new List<(FunctionDefinitionNode, bool)>(nx);
                    for (int i = 0; i < nx; i++)
                    {
                        var node = ReadNode(r);
                        if (node is not FunctionDefinitionNode fdn)
                            throw new InvalidDataException("rac: expected FunctionDefinitionNode in indexers");
                        bool isSet = r.ReadU8() != 0;
                        idx.Add((fdn, isSet));
                    }
                    int nfd = r.ReadI32();
                    var efs = new List<ExtensionFieldDeclaration>(nfd);
                    for (int i = 0; i < nfd; i++)
                    {
                        var node = ReadNode(r);
                        if (node is not StructFieldDefinitionNode sf)
                            throw new InvalidDataException("rac: expected StructFieldDefinitionNode in extension fields");
                        bool sfStatic = r.ReadU8() != 0;
                        bool sfLazy = r.ReadU8() != 0;
                        efs.Add(new ExtensionFieldDeclaration(sf, sfStatic, sfLazy));
                    }
                    return new ExtensionDefinitionNode(tgt, isPub, methods, props, ops, evs, idx, efs, isSealed);
                }

                // ---- Records ----
                case AstNodeType.RecordDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    bool isRef = r.ReadU8() != 0;
                    bool isAbs = r.ReadU8() != 0;
                    var baseT = ReadOptionalTypeDescriptor(r);
                    List<AstNode>? baseArgs = null;
                    if (r.ReadU8() != 0)
                    {
                        int n = r.ReadI32();
                        baseArgs = new List<AstNode>(n);
                        for (int i = 0; i < n; i++) baseArgs.Add(ReadNode(r)!);
                    }
                    int npf = r.ReadI32();
                    var prim = new List<RecordPrimaryFieldNode>(npf);
                    for (int i = 0; i < npf; i++)
                    {
                        var node = ReadNode(r);
                        if (node is RecordPrimaryFieldNode pf) prim.Add(pf);
                        else throw new InvalidDataException("rac: expected RecordPrimaryFieldNode");
                    }
                    int nm = r.ReadI32();
                    var methods = new List<StructMethodDefinitionNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructMethodDefinitionNode sm) methods.Add(sm);
                        else throw new InvalidDataException("rac: expected StructMethodDefinitionNode");
                    }
                    int no = r.ReadI32();
                    var ops = new List<ClassesOperatorDefinitionNode>(no);
                    for (int i = 0; i < no; i++)
                    {
                        var node = ReadNode(r);
                        if (node is ClassesOperatorDefinitionNode od) ops.Add(od);
                        else throw new InvalidDataException("rac: expected OperatorDefinitionNode");
                    }
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    var rd = new RecordDefinitionNode(name, isPub, isRef, isAbs, baseT, baseArgs,
                        prim, methods, ops, generics, wc, props, evs);
                    rd.AutoEquals = r.ReadU8() != 0;
                    rd.AutoToString = r.ReadU8() != 0;
                    return rd;
                }
                case AstNodeType.RecordPrimaryField:
                {
                    var name = ReadToken(r);
                    var td = ReadOptionalTypeDescriptor(r);
                    AstNode? def = null;
                    if (r.ReadU8() != 0) def = ReadNode(r);
                    bool isPub = r.ReadU8() != 0;
                    bool isMut = r.ReadU8() != 0;
                    return new RecordPrimaryFieldNode(name, td, def, isPub, isMut);
                }

                // ---- Enums ----
                case AstNodeType.EnumDefinition:
                {
                    var name = ReadToken(r);
                    int nv = r.ReadI32();
                    var variants = new List<EnumVariantSpec>(nv);
                    for (int i = 0; i < nv; i++) variants.Add(ReadEnumVariantSpec(r));
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    return new EnumDefinitionNode(name, variants, generics, wc);
                }
                case AstNodeType.EnumAccess:
                {
                    var inner = ReadNode(r)!;
                    var memTok = ReadToken(r);
                    return new EnumAccessNode(inner, memTok);
                }

                // ---- Interfaces ----
                case AstNodeType.InterfaceDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    int nm = r.ReadI32();
                    var methods = new List<InterfaceMethodSignatureNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is InterfaceMethodSignatureNode im) methods.Add(im);
                        else throw new InvalidDataException("rac: expected InterfaceMethodSignatureNode");
                    }
                    int nf = r.ReadI32();
                    var fields = new List<StructFieldDefinitionNode>(nf);
                    for (int i = 0; i < nf; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructFieldDefinitionNode sf) fields.Add(sf);
                        else throw new InvalidDataException("rac: expected StructFieldDefinitionNode");
                    }
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    return new InterfaceDefinitionNode(name, isPub, methods, fields, generics, wc, props, evs);
                }
                case AstNodeType.InterfaceMethodSignature:
                {
                    var name = ReadToken(r);
                    int na = r.ReadI32();
                    var aToks = new List<Token>(na);
                    for (int i = 0; i < na; i++) aToks.Add(ReadToken(r));
                    int nty = r.ReadI32();
                    var aTypes = new List<TypeDescriptor?>(nty);
                    for (int i = 0; i < nty; i++) aTypes.Add(ReadOptionalTypeDescriptor(r));
                    var retT = ReadOptionalTypeDescriptor(r);
                    return new InterfaceMethodSignatureNode(name, aToks, aTypes, retT);
                }

                // ---- Traits ----
                case AstNodeType.TraitDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    int nm = r.ReadI32();
                    var methods = new List<TraitMethodDefinitionNode>(nm);
                    for (int i = 0; i < nm; i++)
                    {
                        var node = ReadNode(r);
                        if (node is TraitMethodDefinitionNode tm) methods.Add(tm);
                        else throw new InvalidDataException("rac: expected TraitMethodDefinitionNode");
                    }
                    int nf = r.ReadI32();
                    var fields = new List<StructFieldDefinitionNode>(nf);
                    for (int i = 0; i < nf; i++)
                    {
                        var node = ReadNode(r);
                        if (node is StructFieldDefinitionNode sf) fields.Add(sf);
                        else throw new InvalidDataException("rac: expected StructFieldDefinitionNode");
                    }
                    int np = r.ReadI32();
                    var props = new List<PropertyDefinitionNode>(np);
                    for (int i = 0; i < np; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyDefinitionNode pd) props.Add(pd);
                        else throw new InvalidDataException("rac: expected PropertyDefinitionNode");
                    }
                    int ne = r.ReadI32();
                    var evs = new List<EventDefinitionNode>(ne);
                    for (int i = 0; i < ne; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventDefinitionNode ed) evs.Add(ed);
                        else throw new InvalidDataException("rac: expected EventDefinitionNode");
                    }
                    var generics = ReadStringList(r);
                    var wc = ReadWhereConstraintList(r);
                    return new TraitDefinitionNode(name, isPub, methods, fields, generics, wc, props, evs);
                }
                case AstNodeType.TraitMethodDefinition: return ReadTraitMethod(r);
                case AstNodeType.CallableSignature:
                {
                    int na = r.ReadI32();
                    var aToks = new List<Token>(na);
                    for (int i = 0; i < na; i++) aToks.Add(ReadToken(r));
                    int nty = r.ReadI32();
                    var aTypes = new List<TypeDescriptor?>(nty);
                    for (int i = 0; i < nty; i++) aTypes.Add(ReadOptionalTypeDescriptor(r));
                    int nrp = r.ReadI32();
                    var refs = new List<bool>(nrp);
                    for (int i = 0; i < nrp; i++) refs.Add(r.ReadU8() != 0);
                    int npd = r.ReadI32();
                    var defs = new List<AstNode?>(npd);
                    for (int i = 0; i < npd; i++)
                    {
                        AstNode? d = null;
                        if (r.ReadU8() != 0) d = ReadNode(r);
                        defs.Add(d);
                    }
                    bool hasVar = r.ReadU8() != 0;
                    Token? vaTok = null;
                    if (r.ReadU8() != 0) vaTok = ReadToken(r);
                    var vaT = ReadOptionalTypeDescriptor(r);
                    var retT = ReadOptionalTypeDescriptor(r);
                    return new CallableSignatureNode(aToks, aTypes, refs, defs, hasVar, vaTok, vaT, retT);
                }

                // ---- Properties ----
                case AstNodeType.PropertyDefinition:
                {
                    var name = ReadToken(r);
                    var pt = ReadOptionalTypeDescriptor(r);
                    AstNode? def = null;
                    if (r.ReadU8() != 0) def = ReadNode(r);
                    int nac = r.ReadI32();
                    var accs = new List<PropertyAccessorNode>(nac);
                    for (int i = 0; i < nac; i++)
                    {
                        var node = ReadNode(r);
                        if (node is PropertyAccessorNode pa) accs.Add(pa);
                        else throw new InvalidDataException("rac: expected PropertyAccessorNode");
                    }
                    bool isPub = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    bool isAbs = r.ReadU8() != 0;
                    bool isOver = r.ReadU8() != 0;
                    bool isLazy = r.ReadU8() != 0;
                    return new PropertyDefinitionNode(name, pt, def, accs, isPub, isStat, isAbs, isOver, isLazy);
                }
                case AstNodeType.PropertyAccessor:
                {
                    var kindTok = ReadToken(r);
                    var kind = (PropertyAccessorKind)r.ReadU8();
                    var vis = (PropertyAccessorVisibility)r.ReadU8();
                    AstNode? body = null;
                    if (r.ReadU8() != 0) body = ReadNode(r);
                    return new PropertyAccessorNode(kindTok, kind, vis, body);
                }

                // ---- Events ----
                case AstNodeType.EventDefinition:
                {
                    var name = ReadToken(r);
                    int npp = r.ReadI32();
                    var payload = new List<EventPayloadParam>(npp);
                    for (int i = 0; i < npp; i++) payload.Add(ReadEventPayloadParam(r));
                    int nac = r.ReadI32();
                    var accs = new List<EventAccessorNode>(nac);
                    for (int i = 0; i < nac; i++)
                    {
                        var node = ReadNode(r);
                        if (node is EventAccessorNode ea) accs.Add(ea);
                        else throw new InvalidDataException("rac: expected EventAccessorNode");
                    }
                    bool isPub = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    bool isAbs = r.ReadU8() != 0;
                    bool isOver = r.ReadU8() != 0;
                    bool isCancel = r.ReadU8() != 0;
                    bool isTol = r.ReadU8() != 0;
                    bool isAsy = r.ReadU8() != 0;
                    return new EventDefinitionNode(name, payload, accs, isPub, isStat, isAbs, isOver,
                        isCancel, isTol, isAsy);
                }
                case AstNodeType.EventAccessor:
                {
                    var kindTok = ReadToken(r);
                    var kind = (EventAccessorKind)r.ReadU8();
                    var vis = (EventAccessorVisibility)r.ReadU8();
                    return new EventAccessorNode(kindTok, kind, vis);
                }

                // ---- Annotations ----
                case AstNodeType.AnnotationDefinition:
                {
                    var name = ReadToken(r);
                    bool isPub = r.ReadU8() != 0;
                    int np = r.ReadI32();
                    var pars = new List<AnnotationParameterNode>(np);
                    for (int i = 0; i < np; i++) pars.Add(ReadAnnotationParameter(r));
                    return new AnnotationDefinitionNode(name, isPub, pars);
                }
                case AstNodeType.AnnotationApplication:
                {
                    var name = ReadToken(r);
                    int pn = r.ReadI32();
                    var pos = new List<AstNode>(pn);
                    for (int i = 0; i < pn; i++) pos.Add(ReadNode(r)!);
                    int nn = r.ReadI32();
                    var named = new List<(Token, AstNode)>(nn);
                    for (int i = 0; i < nn; i++)
                    {
                        var nt2 = ReadToken(r);
                        var nv = ReadNode(r)!;
                        named.Add((nt2, nv));
                    }
                    return new AnnotationApplicationNode(name, pos, named, ps, pe);
                }

                // ---- Async ----
                case AstNodeType.Await: { var ex = ReadNode(r)!; return new AwaitNode(ex, ps, pe); }
                case AstNodeType.Spawn: { var ex = ReadNode(r)!; return new SpawnNode(ex, ps, pe); }
                case AstNodeType.Emit:  { var ex = ReadNode(r)!; return new EmitNode(ex, ps, pe); }
                case AstNodeType.ForAwait:
                {
                    var vn = ReadToken(r);
                    var st = ReadNode(r)!;
                    var bd = ReadNode(r)!;
                    bool srn = r.ReadU8() != 0;
                    return new ForAwaitNode(vn, st, bd, srn);
                }

                // ---- Namespaces ----
                case AstNodeType.NamespaceDeclaration:
                {
                    int ns = r.ReadI32();
                    var segs = new List<Token>(ns);
                    for (int i = 0; i < ns; i++) segs.Add(ReadToken(r));
                    var body = ReadNode(r)!;
                    bool isFs = r.ReadU8() != 0;
                    return new NamespaceDeclarationNode(segs, body, isFs, ps, pe);
                }
                case AstNodeType.UsingNamespace:
                {
                    int ns = r.ReadI32();
                    var segs = new List<Token>(ns);
                    for (int i = 0; i < ns; i++) segs.Add(ReadToken(r));
                    Token? alias = null;
                    if (r.ReadU8() != 0) alias = ReadToken(r);
                    return new UsingNamespaceNode(segs, alias, ps, pe);
                }

                // ---- Imports ----
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

                // ---- Asm ----
                case AstNodeType.AsmBlock:
                {
                    int np = r.ReadI32();
                    var parts = new List<AstNode>(np);
                    for (int i = 0; i < np; i++) parts.Add(ReadNode(r)!);
                    int nrt = r.ReadI32();
                    var rets = new List<string>(nrt);
                    for (int i = 0; i < nrt; i++) rets.Add(r.ReadString() ?? "");
                    var node = new AsmBlockNode(parts, ps, pe);
                    node.ReturnTypes = rets;
                    return node;
                }
                case AstNodeType.AsmTextPart: return new AsmTextPartNode(r.ReadString() ?? "", ps, pe);
                case AstNodeType.AsmInterpPart:
                {
                    var ex = ReadNode(r)!;
                    string? hint = null;
                    if (r.ReadU8() != 0) hint = r.ReadString();
                    return new AsmInterpPartNode(ex, hint, ps, pe);
                }

                // ---- Patterns + Match ----
                case AstNodeType.DestructuringDeclaration:
                {
                    var pat = ReadPattern(r);
                    var init = ReadNode(r)!;
                    var kind = (VariableDeclarationType)r.ReadU8();
                    var dt = ReadOptionalTypeDescriptor(r);
                    bool isPub = r.ReadU8() != 0;
                    bool isStat = r.ReadU8() != 0;
                    return new DestructuringDeclarationNode(pat, init, kind, dt, ps, pe, isPub, isStat);
                }
                case AstNodeType.Match:
                {
                    var sc = ReadNode(r)!;
                    int n = r.ReadI32();
                    var arms = new List<MatchArmNode>(n);
                    for (int i = 0; i < n; i++) arms.Add(ReadMatchArm(r));
                    return new MatchNode(sc, arms, ps, pe);
                }
                case AstNodeType.TryUnwrap:
                {
                    var t = ReadNode(r)!;
                    return new TryUnwrapNode(t, ps, pe);
                }

                default:
                    throw new InvalidDataException(
                        $"rac: ModuleBytecode contains node type {nt} not supported by this loader");
            }
        }

        // --- FunctionDefinition --------------------------------------------------
        private static void WriteFunctionDefinition(RacBinaryWriter w, FunctionDefinitionNode fd)
        {
            // Resolver outputs.
            w.WriteI32(fd.FrameId);
            WriteBindingIdArray(w, fd.ParamBindings);
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
            // HasVarArgs / VarArgNameTok / VarArgType / VarArgAnnotations
            w.WriteU8(fd.HasVarArgs ? (byte)1 : (byte)0);
            if (fd.VarArgNameTok == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteToken(w, fd.VarArgNameTok.Value); }
            WriteOptionalTypeDescriptor(w, fd.VarArgType);
            if (fd.VarArgAnnotations == null || fd.VarArgAnnotations.Count == 0) { w.WriteU8(0); }
            else
            {
                w.WriteU8(1);
                w.WriteI32(fd.VarArgAnnotations.Count);
                foreach (var a in fd.VarArgAnnotations) WriteAnnotationApplication(w, a);
            }
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
            WriteStringList(w, fd.GenericTypeParams);
            // WhereConstraints (v1.2: full support)
            WriteWhereConstraintList(w, fd.WhereConstraints);
            // CaptureList (v1.2: full support; null vs empty distinguished)
            if (fd.CaptureList == null) w.WriteU8(0);
            else
            {
                w.WriteU8(1);
                w.WriteI32(fd.CaptureList.Count);
                foreach (var c in fd.CaptureList) WriteCaptureSpec(w, c);
            }
            // v4 (#pre-compiled children): optional inline RaFunction
            // for fd.CompiledBody. Older writers (writing v3 / v1)
            // skip this field; readers gate on ReaderVersion.
            WriteOptionalInlineRaFunction(w, fd.CompiledBody);
            // v5 (constructors): factory / named-constructor metadata.
            if (WriterVersion >= ModuleBytecodeIo.PayloadVersion_V5)
            {
                byte cbits = 0;
                if (fd.IsFactory) cbits |= 0x01;
                if (fd.ConstructorName != null) cbits |= 0x02;
                w.WriteU8(cbits);
                if (fd.ConstructorName != null) w.WriteString(fd.ConstructorName);
            }
        }

        private static FunctionDefinitionNode ReadFunctionDefinition(RacBinaryReader r)
        {
            int frameId = r.ReadI32();
            var paramBindings = ReadBindingIdArray(r);
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
            List<AnnotationApplicationNode>? varArgAnns = null;
            if (r.ReadU8() != 0)
            {
                int an = r.ReadI32();
                varArgAnns = new List<AnnotationApplicationNode>(an);
                for (int j = 0; j < an; j++) varArgAnns.Add(ReadAnnotationApplication(r));
            }
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
            var generics = ReadStringList(r);
            var whereC = ReadWhereConstraintList(r);
            List<CaptureSpec>? captureList = null;
            if (r.ReadU8() != 0)
            {
                int n = r.ReadI32();
                captureList = new List<CaptureSpec>(n);
                for (int i = 0; i < n; i++) captureList.Add(ReadCaptureSpec(r));
            }

            var fd = new FunctionDefinitionNode(
                nameTok, argToks, argTypes, isRef, defaults,
                hasVar, varArgTok, varArgType, retType, body, sar,
                generics, pub, ctor, over, abs, stat,
                whereC, paramAnns, captureList);
            fd.IsAsync = async;
            fd.IsAsyncStream = asyncStream;
            fd.FrameId = frameId;
            fd.ParamBindings = paramBindings;
            fd.VarArgAnnotations = varArgAnns;
            var compiled = ReadOptionalInlineRaFunction(r);
            if (compiled != null)
            {
                fd.CompiledBody = compiled;
                // Latch GetOrCompileBody so it returns the cached
                // body without re-attempting IR compile.
                fd.IrCompileTried = true;
            }
            // v5 (constructors): factory / named-constructor metadata.
            if (ReaderVersion >= ModuleBytecodeIo.PayloadVersion_V5)
            {
                byte cbits = r.ReadU8();
                fd.IsFactory = (cbits & 0x01) != 0;
                if ((cbits & 0x02) != 0) fd.ConstructorName = r.ReadString();
            }
            return fd;
        }

        // --- StructMethodDefinition --------------------------------------------
        private static void WriteStructMethodPayload(RacBinaryWriter w, StructMethodDefinitionNode smd)
        {
            w.WriteU8(smd.IsPublic ? (byte)1 : (byte)0);
            w.WriteU8(smd.IsConstructor ? (byte)1 : (byte)0);
            WriteToken(w, smd.NameTok);
            w.WriteI32(smd.ArgNameToks.Count);
            foreach (var t in smd.ArgNameToks) WriteToken(w, t);
            w.WriteI32(smd.ArgTypes.Count);
            foreach (var td in smd.ArgTypes) WriteOptionalTypeDescriptor(w, td);
            w.WriteI32(smd.IsRefParams.Count);
            foreach (var b in smd.IsRefParams) w.WriteU8(b ? (byte)1 : (byte)0);
            w.WriteI32(smd.ParamDefaults.Count);
            foreach (var d in smd.ParamDefaults)
            {
                if (d == null) w.WriteU8(0);
                else { w.WriteU8(1); WriteNode(w, d); }
            }
            w.WriteU8(smd.HasVarArgs ? (byte)1 : (byte)0);
            if (smd.VarArgNameTok == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteToken(w, smd.VarArgNameTok.Value); }
            WriteOptionalTypeDescriptor(w, smd.VarArgType);
            WriteOptionalTypeDescriptor(w, smd.ReturnType);
            WriteNode(w, smd.BodyNode);
            w.WriteU8(smd.ShouldAutoReturn ? (byte)1 : (byte)0);
            w.WriteU8(smd.IsAsync ? (byte)1 : (byte)0);
            w.WriteU8(smd.IsAsyncStream ? (byte)1 : (byte)0);
            w.WriteI32(smd.FrameId);
            WriteBindingIdArray(w, smd.ParamBindings);
            // v4 (#pre-compiled children): optional CompiledBody.
            WriteOptionalInlineRaFunction(w, smd.CompiledBody);
        }

        private static StructMethodDefinitionNode ReadStructMethod(RacBinaryReader r)
        {
            bool isPub = r.ReadU8() != 0;
            bool isCtor = r.ReadU8() != 0;
            var name = ReadToken(r);
            int na = r.ReadI32();
            var aToks = new List<Token>(na);
            for (int i = 0; i < na; i++) aToks.Add(ReadToken(r));
            int nat = r.ReadI32();
            var aTypes = new List<TypeDescriptor?>(nat);
            for (int i = 0; i < nat; i++) aTypes.Add(ReadOptionalTypeDescriptor(r));
            int nrp = r.ReadI32();
            var refs = new List<bool>(nrp);
            for (int i = 0; i < nrp; i++) refs.Add(r.ReadU8() != 0);
            int npd = r.ReadI32();
            var defs = new List<AstNode?>(npd);
            for (int i = 0; i < npd; i++)
            {
                AstNode? d = null;
                if (r.ReadU8() != 0) d = ReadNode(r);
                defs.Add(d);
            }
            bool hasVar = r.ReadU8() != 0;
            Token? vaTok = null;
            if (r.ReadU8() != 0) vaTok = ReadToken(r);
            var vaT = ReadOptionalTypeDescriptor(r);
            var retT = ReadOptionalTypeDescriptor(r);
            var body = ReadNode(r)!;
            bool sar = r.ReadU8() != 0;
            bool asy = r.ReadU8() != 0;
            bool asyS = r.ReadU8() != 0;
            int frame = r.ReadI32();
            var pb = ReadBindingIdArray(r);
            var node = new StructMethodDefinitionNode(isPub, isCtor, name, aToks, aTypes, refs, defs,
                hasVar, vaTok, vaT, retT, body, sar);
            node.IsAsync = asy;
            node.IsAsyncStream = asyS;
            node.FrameId = frame;
            node.ParamBindings = pb;
            var compiled = ReadOptionalInlineRaFunction(r);
            if (compiled != null)
            {
                node.CompiledBody = compiled;
                node.IrCompileTried = true;
            }
            return node;
        }

        // --- TraitMethodDefinition --------------------------------------------
        private static void WriteTraitMethodPayload(RacBinaryWriter w, TraitMethodDefinitionNode tmd)
        {
            // NameTok is nullable on TraitMethod but always populated by the
            // parser. Serialise as required to keep the wire layout simple.
            if (tmd.NameTok == null)
                throw new ModuleBytecodeUnsupportedException("TraitMethodDefinition without NameTok");
            WriteToken(w, tmd.NameTok.Value);
            w.WriteI32(tmd.ArgNameToks.Count);
            foreach (var t in tmd.ArgNameToks) WriteToken(w, t);
            w.WriteI32(tmd.ArgTypes.Count);
            foreach (var td in tmd.ArgTypes) WriteOptionalTypeDescriptor(w, td);
            w.WriteI32(tmd.IsRefParams.Count);
            foreach (var b in tmd.IsRefParams) w.WriteU8(b ? (byte)1 : (byte)0);
            w.WriteI32(tmd.ParamDefaults.Count);
            foreach (var d in tmd.ParamDefaults)
            {
                if (d == null) w.WriteU8(0);
                else { w.WriteU8(1); WriteNode(w, d); }
            }
            w.WriteU8(tmd.HasVarArgs ? (byte)1 : (byte)0);
            if (tmd.VarArgNameTok == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteToken(w, tmd.VarArgNameTok.Value); }
            WriteOptionalTypeDescriptor(w, tmd.VarArgType);
            WriteOptionalTypeDescriptor(w, tmd.ReturnType);
            if (tmd.BodyNode == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteNode(w, tmd.BodyNode); }
            w.WriteU8(tmd.ShouldAutoReturn ? (byte)1 : (byte)0);
            w.WriteU8(tmd.IsAbstract ? (byte)1 : (byte)0);
            w.WriteU8(tmd.IsAsync ? (byte)1 : (byte)0);
            w.WriteU8(tmd.IsAsyncStream ? (byte)1 : (byte)0);
            w.WriteI32(tmd.FrameId);
            WriteBindingIdArray(w, tmd.ParamBindings);
            // v4 (#pre-compiled children): optional CompiledBody.
            WriteOptionalInlineRaFunction(w, tmd.CompiledBody);
        }

        private static TraitMethodDefinitionNode ReadTraitMethod(RacBinaryReader r)
        {
            var name = ReadToken(r);
            int na = r.ReadI32();
            var aToks = new List<Token>(na);
            for (int i = 0; i < na; i++) aToks.Add(ReadToken(r));
            int nat = r.ReadI32();
            var aTypes = new List<TypeDescriptor?>(nat);
            for (int i = 0; i < nat; i++) aTypes.Add(ReadOptionalTypeDescriptor(r));
            int nrp = r.ReadI32();
            var refs = new List<bool>(nrp);
            for (int i = 0; i < nrp; i++) refs.Add(r.ReadU8() != 0);
            int npd = r.ReadI32();
            var defs = new List<AstNode?>(npd);
            for (int i = 0; i < npd; i++)
            {
                AstNode? d = null;
                if (r.ReadU8() != 0) d = ReadNode(r);
                defs.Add(d);
            }
            bool hasVar = r.ReadU8() != 0;
            Token? vaTok = null;
            if (r.ReadU8() != 0) vaTok = ReadToken(r);
            var vaT = ReadOptionalTypeDescriptor(r);
            var retT = ReadOptionalTypeDescriptor(r);
            AstNode? body = null;
            if (r.ReadU8() != 0) body = ReadNode(r);
            bool sar = r.ReadU8() != 0;
            bool isAbs = r.ReadU8() != 0;
            bool asy = r.ReadU8() != 0;
            bool asyS = r.ReadU8() != 0;
            int frame = r.ReadI32();
            var pb = ReadBindingIdArray(r);
            var node = new TraitMethodDefinitionNode(name, aToks, aTypes, refs, defs,
                hasVar, vaTok, vaT, retT, body, sar, isAbs);
            node.IsAsync = asy;
            node.IsAsyncStream = asyS;
            node.FrameId = frame;
            node.ParamBindings = pb;
            var compiled = ReadOptionalInlineRaFunction(r);
            if (compiled != null)
            {
                node.CompiledBody = compiled;
                node.IrCompileTried = true;
            }
            return node;
        }

        // --- Helper-struct serialisers -----------------------------------------
        private static void WriteEnumVariantSpec(RacBinaryWriter w, EnumVariantSpec v)
        {
            WriteToken(w, v.MemberTok);
            if (v.ValueNode == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteNode(w, v.ValueNode); }
            if (v.PayloadTypes == null) w.WriteU8(0);
            else
            {
                w.WriteU8(1);
                w.WriteI32(v.PayloadTypes.Count);
                foreach (var td in v.PayloadTypes) WriteTypeDescriptor(w, td);
            }
        }

        private static EnumVariantSpec ReadEnumVariantSpec(RacBinaryReader r)
        {
            var t = ReadToken(r);
            AstNode? val = null;
            if (r.ReadU8() != 0) val = ReadNode(r);
            List<TypeDescriptor>? payload = null;
            if (r.ReadU8() != 0)
            {
                int n = r.ReadI32();
                payload = new List<TypeDescriptor>(n);
                for (int i = 0; i < n; i++) payload.Add(ReadTypeDescriptor(r));
            }
            return new EnumVariantSpec(t, val, payload);
        }

        private static void WriteEventPayloadParam(RacBinaryWriter w, EventPayloadParam p)
        {
            WriteToken(w, p.NameTok);
            WriteOptionalTypeDescriptor(w, p.Type);
        }

        private static EventPayloadParam ReadEventPayloadParam(RacBinaryReader r)
        {
            var t = ReadToken(r);
            var td = ReadOptionalTypeDescriptor(r);
            return new EventPayloadParam(t, td);
        }

        private static void WriteAnnotationParameter(RacBinaryWriter w, AnnotationParameterNode p)
        {
            WriteToken(w, p.NameTok);
            WriteOptionalTypeDescriptor(w, p.DeclaredType);
            if (p.DefaultValueNode == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteNode(w, p.DefaultValueNode); }
            w.WriteU8(p.IsVarArgs ? (byte)1 : (byte)0);
        }

        private static AnnotationParameterNode ReadAnnotationParameter(RacBinaryReader r)
        {
            var t = ReadToken(r);
            var td = ReadOptionalTypeDescriptor(r);
            AstNode? def = null;
            if (r.ReadU8() != 0) def = ReadNode(r);
            bool isVar = r.ReadU8() != 0;
            return new AnnotationParameterNode(t, td, def, isVar);
        }

        private static void WriteCaptureSpec(RacBinaryWriter w, CaptureSpec c)
        {
            WriteToken(w, c.NameTok);
            w.WriteU8((byte)c.Mode);
            w.WriteU8(c.IsMutableBorrow ? (byte)1 : (byte)0);
        }

        private static CaptureSpec ReadCaptureSpec(RacBinaryReader r)
        {
            var t = ReadToken(r);
            var mode = (CaptureMode)r.ReadU8();
            bool isMut = r.ReadU8() != 0;
            return new CaptureSpec(t, mode, isMut);
        }

        private static void WriteWhereConstraintList(RacBinaryWriter w, List<WhereConstraintNode> list)
        {
            w.WriteI32(list.Count);
            foreach (var c in list)
            {
                WriteToken(w, c.ParameterNameTok);
                WriteTypeDescriptor(w, c.ConstraintType);
            }
        }

        private static List<WhereConstraintNode> ReadWhereConstraintList(RacBinaryReader r)
        {
            int n = r.ReadI32();
            var list = new List<WhereConstraintNode>(n);
            for (int i = 0; i < n; i++)
            {
                var t = ReadToken(r);
                var td = ReadTypeDescriptor(r);
                list.Add(new WhereConstraintNode(t, td));
            }
            return list;
        }

        private static void WriteStringList(RacBinaryWriter w, List<string> list)
        {
            w.WriteI32(list.Count);
            foreach (var s in list) w.WriteString(s);
        }

        private static List<string> ReadStringList(RacBinaryReader r)
        {
            int n = r.ReadI32();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString() ?? "");
            return list;
        }

        private static void WriteBindingIdArray(RacBinaryWriter w, BindingId[]? arr)
        {
            if (arr == null) { w.WriteI32(-1); return; }
            w.WriteI32(arr.Length);
            for (int i = 0; i < arr.Length; i++) w.WriteI32(arr[i].Raw);
        }

        // === v4 inline RaFunction (pre-compiled child bodies) ===
        //
        // Encoded as u8 presence (0 = absent, 1 = present) followed by
        // the ModuleBytecodeIo SerializeRaFunction body (no RAFB
        // magic / version — that lives once at the outer payload).
        // The pool comes from AstNodeSerializer.WriterPool /
        // ReaderPool, set by the outer ModuleBytecodeIo wrapper.
        //
        // v3 writers (now retired) emitted no presence byte at this
        // position; v3 readers also skip the read entirely. The
        // ReaderVersion gate keeps both wire forms compatible.
        private static void WriteOptionalInlineRaFunction(RacBinaryWriter w,
            RaLanguage.Interpreter.IR.RaFunction? fn)
        {
            // Only emit when the outer wire is v4+. v1 / v3 writers
            // omit the field for backward compat.
            if (WriterVersion < ModuleBytecodeIo.PayloadVersion_V4)
                return;
            if (fn == null) { w.WriteU8(0); return; }
            w.WriteU8(1);
            ModuleBytecodeIo.WriteInlineRaFunction(w, fn, WriterPool);
        }

        private static RaLanguage.Interpreter.IR.RaFunction? ReadOptionalInlineRaFunction(RacBinaryReader r)
        {
            if (ReaderVersion < ModuleBytecodeIo.PayloadVersion_V4)
                return null;
            byte present = r.ReadU8();
            if (present == 0) return null;
            return ModuleBytecodeIo.ReadInlineRaFunction(r, ReaderPool);
        }

        private static BindingId[]? ReadBindingIdArray(RacBinaryReader r)
        {
            int n = r.ReadI32();
            if (n < 0) return null;
            var arr = new BindingId[n];
            for (int i = 0; i < n; i++) arr[i] = new BindingId(r.ReadI32());
            return arr;
        }

        // --- FormatSpec ---------------------------------------------------------
        private static void WriteFormatSpec(RacBinaryWriter w, FormatSpec spec)
        {
            w.WriteU8((byte)spec.Kind);
            w.WriteI32(spec.Precision);
            byte flags = 0;
            if (spec.HasPrecision) flags |= 0x01;
            if (spec.AlternateForm) flags |= 0x02;
            if (spec.UpperCase) flags |= 0x04;
            w.WriteU8(flags);
        }

        private static FormatSpec ReadFormatSpec(RacBinaryReader r)
        {
            var kind = (FormatKind)r.ReadU8();
            int prec = r.ReadI32();
            byte flags = r.ReadU8();
            bool hasP = (flags & 0x01) != 0;
            bool alt = (flags & 0x02) != 0;
            bool upper = (flags & 0x04) != 0;
            return new FormatSpec(kind, alt, hasP, prec, upper);
        }

        // --- Pattern serializer (PatternNode is not an AstNode) ----------------
        private const byte PatTag_Null = 0x00;
        private const byte PatTag_Wildcard = 0x01;
        private const byte PatTag_Literal = 0x02;
        private const byte PatTag_Variable = 0x03;
        private const byte PatTag_Variant = 0x04;
        private const byte PatTag_Tuple = 0x05;
        private const byte PatTag_List = 0x06;
        private const byte PatTag_Struct = 0x07;
        private const byte PatTag_Rest = 0x08;
        private const byte PatTag_Type = 0x09;
        private const byte PatTag_Or = 0x0A;
        private const byte PatTag_Range = 0x0B;
        private const byte PatTag_Relational = 0x0C;
        private const byte PatTag_Alias = 0x0D;
        private const byte PatTag_Map = 0x0E;
        private const byte PatTag_Not = 0x0F;
        private const byte PatTag_And = 0x10;

        private static void WritePattern(RacBinaryWriter w, PatternNode? p)
        {
            // Wire form: u8 presence (0 = null, 1 = present), then if
            // present positions + tag + payload. Keeps null encoding cheap
            // and avoids an ambiguity with the tag-only null sentinel
            // before positions get a chance to be written.
            if (p == null) { w.WriteU8(0); return; }
            w.WriteU8(1);
            ModuleBytecodeIo.WritePosition(w, p.PositionStart);
            ModuleBytecodeIo.WritePosition(w, p.PositionEnd);
            switch (p)
            {
                case WildcardPatternNode:
                    w.WriteU8(PatTag_Wildcard);
                    return;
                case LiteralPatternNode lp:
                    w.WriteU8(PatTag_Literal);
                    WriteNode(w, lp.Expression);
                    return;
                case VariablePatternNode vp:
                    w.WriteU8(PatTag_Variable);
                    w.WriteString(vp.Name);
                    return;
                case VariantPatternNode varp:
                    w.WriteU8(PatTag_Variant);
                    if (varp.EnumName == null) w.WriteU8(0);
                    else { w.WriteU8(1); w.WriteString(varp.EnumName); }
                    w.WriteString(varp.VariantName);
                    if (varp.SubPatterns == null) w.WriteI32(-1);
                    else
                    {
                        w.WriteI32(varp.SubPatterns.Count);
                        foreach (var sp in varp.SubPatterns) WritePattern(w, sp);
                    }
                    return;
                case TuplePatternNode tp:
                    w.WriteU8(PatTag_Tuple);
                    w.WriteI32(tp.Elements.Count);
                    foreach (var e in tp.Elements) WritePattern(w, e);
                    return;
                case ListPatternNode lp2:
                    w.WriteU8(PatTag_List);
                    w.WriteI32(lp2.Elements.Count);
                    foreach (var e in lp2.Elements) WritePattern(w, e);
                    if (lp2.Rest == null) w.WriteU8(0);
                    else { w.WriteU8(1); WritePattern(w, lp2.Rest); }
                    w.WriteI32(lp2.RestIndex);
                    return;
                case StructPatternNode sp:
                    w.WriteU8(PatTag_Struct);
                    w.WriteString(sp.StructName);
                    w.WriteI32(sp.Fields.Count);
                    foreach (var (field, fieldPat) in sp.Fields)
                    {
                        w.WriteString(field);
                        if (fieldPat == null) w.WriteU8(0);
                        else { w.WriteU8(1); WritePattern(w, fieldPat); }
                    }
                    return;
                case RestPatternNode rp:
                    w.WriteU8(PatTag_Rest);
                    if (rp.BindName == null) w.WriteU8(0);
                    else { w.WriteU8(1); w.WriteString(rp.BindName); }
                    return;
                case TypePatternNode tp2:
                    w.WriteU8(PatTag_Type);
                    WriteTypeDescriptor(w, tp2.TestedType);
                    if (tp2.BinderName == null) w.WriteU8(0);
                    else { w.WriteU8(1); w.WriteString(tp2.BinderName); }
                    return;
                case OrPatternNode op:
                    w.WriteU8(PatTag_Or);
                    w.WriteI32(op.Alternatives.Count);
                    foreach (var a in op.Alternatives) WritePattern(w, a);
                    return;
                case RangePatternNode rp2:
                    w.WriteU8(PatTag_Range);
                    if (rp2.Lo == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rp2.Lo); }
                    if (rp2.Hi == null) w.WriteU8(0);
                    else { w.WriteU8(1); WriteNode(w, rp2.Hi); }
                    w.WriteU8(rp2.IsInclusive ? (byte)1 : (byte)0);
                    return;
                case RelationalPatternNode relp:
                    w.WriteU8(PatTag_Relational);
                    w.WriteI32((int)relp.Op);
                    WriteNode(w, relp.Operand);
                    return;
                case AliasPatternNode ap:
                    w.WriteU8(PatTag_Alias);
                    WritePattern(w, ap.Inner);
                    w.WriteString(ap.BinderName);
                    return;
                case MapPatternNode mp:
                    w.WriteU8(PatTag_Map);
                    w.WriteI32(mp.Entries.Count);
                    foreach (var (k, v) in mp.Entries)
                    {
                        WriteNode(w, k);
                        WritePattern(w, v);
                    }
                    w.WriteU8(mp.HasOpenRest ? (byte)1 : (byte)0);
                    return;
                case NotPatternNode np:
                    w.WriteU8(PatTag_Not);
                    WritePattern(w, np.Inner);
                    return;
                case AndPatternNode andp:
                    w.WriteU8(PatTag_And);
                    w.WriteI32(andp.Conjuncts.Count);
                    foreach (var c in andp.Conjuncts) WritePattern(w, c);
                    return;
                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"Pattern node {p.GetType().Name} not supported");
            }
        }

        private static PatternNode ReadPattern(RacBinaryReader r)
        {
            var p = ReadPatternOptional(r);
            if (p == null) throw new InvalidDataException("rac: null PatternNode where non-null required");
            return p;
        }

        private static PatternNode? ReadPatternOptional(RacBinaryReader r)
        {
            byte present = r.ReadU8();
            if (present == 0) return null;
            var ps = ModuleBytecodeIo.ReadPosition(r);
            var pe = ModuleBytecodeIo.ReadPosition(r);
            byte tag = r.ReadU8();
            switch (tag)
            {
                case PatTag_Wildcard: return new WildcardPatternNode(ps, pe);
                case PatTag_Literal:
                {
                    var expr = ReadNode(r)!;
                    return new LiteralPatternNode(expr, ps, pe);
                }
                case PatTag_Variable:
                {
                    string name = r.ReadString() ?? "";
                    return new VariablePatternNode(name, ps, pe);
                }
                case PatTag_Variant:
                {
                    string? en = null;
                    if (r.ReadU8() != 0) en = r.ReadString();
                    string vn = r.ReadString() ?? "";
                    int sc = r.ReadI32();
                    List<PatternNode>? subs = null;
                    if (sc >= 0)
                    {
                        subs = new List<PatternNode>(sc);
                        for (int i = 0; i < sc; i++) subs.Add(ReadPattern(r));
                    }
                    return new VariantPatternNode(en, vn, subs, ps, pe);
                }
                case PatTag_Tuple:
                {
                    int n = r.ReadI32();
                    var els = new List<PatternNode>(n);
                    for (int i = 0; i < n; i++) els.Add(ReadPattern(r));
                    return new TuplePatternNode(els, ps, pe);
                }
                case PatTag_List:
                {
                    int n = r.ReadI32();
                    var els = new List<PatternNode>(n);
                    for (int i = 0; i < n; i++) els.Add(ReadPattern(r));
                    RestPatternNode? rest = null;
                    if (r.ReadU8() != 0)
                    {
                        var rp = ReadPattern(r);
                        rest = (RestPatternNode)rp;
                    }
                    int ri = r.ReadI32();
                    return new ListPatternNode(els, rest, ri, ps, pe);
                }
                case PatTag_Struct:
                {
                    string sn = r.ReadString() ?? "";
                    int n = r.ReadI32();
                    var fields = new List<(string, PatternNode?)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        string fn = r.ReadString() ?? "";
                        PatternNode? fp = null;
                        if (r.ReadU8() != 0) fp = ReadPattern(r);
                        fields.Add((fn, fp));
                    }
                    return new StructPatternNode(sn, fields, ps, pe);
                }
                case PatTag_Rest:
                {
                    string? bn = null;
                    if (r.ReadU8() != 0) bn = r.ReadString();
                    return new RestPatternNode(bn, ps, pe);
                }
                case PatTag_Type:
                {
                    var td = ReadTypeDescriptor(r);
                    string? bn = null;
                    if (r.ReadU8() != 0) bn = r.ReadString();
                    return new TypePatternNode(td, bn, ps, pe);
                }
                case PatTag_Or:
                {
                    int n = r.ReadI32();
                    var alts = new List<PatternNode>(n);
                    for (int i = 0; i < n; i++) alts.Add(ReadPattern(r));
                    return new OrPatternNode(alts, ps, pe);
                }
                case PatTag_Range:
                {
                    AstNode? lo = null;
                    if (r.ReadU8() != 0) lo = ReadNode(r);
                    AstNode? hi = null;
                    if (r.ReadU8() != 0) hi = ReadNode(r);
                    bool isInc = r.ReadU8() != 0;
                    return new RangePatternNode(lo, hi, isInc, ps, pe);
                }
                case PatTag_Relational:
                {
                    var op = (TokenType)r.ReadI32();
                    var opnd = ReadNode(r)!;
                    return new RelationalPatternNode(op, opnd, ps, pe);
                }
                case PatTag_Alias:
                {
                    var inner = ReadPattern(r);
                    string bn = r.ReadString() ?? "";
                    return new AliasPatternNode(inner, bn, ps, pe);
                }
                case PatTag_Map:
                {
                    int n = r.ReadI32();
                    var entries = new List<(AstNode, PatternNode)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var k = ReadNode(r)!;
                        var v = ReadPattern(r);
                        entries.Add((k, v));
                    }
                    bool open = r.ReadU8() != 0;
                    return new MapPatternNode(entries, open, ps, pe);
                }
                case PatTag_Not:
                {
                    var inner = ReadPattern(r);
                    return new NotPatternNode(inner, ps, pe);
                }
                case PatTag_And:
                {
                    int n = r.ReadI32();
                    var conj = new List<PatternNode>(n);
                    for (int i = 0; i < n; i++) conj.Add(ReadPattern(r));
                    return new AndPatternNode(conj, ps, pe);
                }
                default:
                    throw new InvalidDataException($"rac: unknown pattern tag 0x{tag:X2}");
            }
        }

        // --- Match arm ---------------------------------------------------------
        private static void WriteMatchArm(RacBinaryWriter w, MatchArmNode arm)
        {
            ModuleBytecodeIo.WritePosition(w, arm.PositionStart);
            ModuleBytecodeIo.WritePosition(w, arm.PositionEnd);
            WritePattern(w, arm.Pattern);
            if (arm.Guard == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteNode(w, arm.Guard); }
            WriteNode(w, arm.Body);
        }

        private static MatchArmNode ReadMatchArm(RacBinaryReader r)
        {
            var ps = ModuleBytecodeIo.ReadPosition(r);
            var pe = ModuleBytecodeIo.ReadPosition(r);
            var pat = ReadPattern(r);
            AstNode? guard = null;
            if (r.ReadU8() != 0) guard = ReadNode(r);
            var body = ReadNode(r)!;
            return new MatchArmNode(pat, guard, body, ps, pe);
        }

        // --- Annotations -------------------------------------------------------
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

        // --- Tokens ------------------------------------------------------------
        //
        // Token.Value tags:
        //   0 = null, 1 = string, 2 = Keyword, 3 = i64 (boxed long/int),
        //   4 = BigNumber, 5 = double, 6 = float, 7 = bool, 8 = char,
        //   9 = Token (nested — produced by the parser's catch-pattern
        //       rewrite, which carries a wrapped Token? whose `.Value`
        //       is the inner Token, not its scalar payload. The
        //       interpreter tolerates the wrap because every consumer
        //       reads the binder through `Value?.ToString()`; the
        //       serialiser therefore round-trips it verbatim).
        private const byte TokValueTag_Null = 0;
        private const byte TokValueTag_String = 1;
        private const byte TokValueTag_Keyword = 2;
        private const byte TokValueTag_I64 = 3;
        private const byte TokValueTag_BigNumber = 4;
        private const byte TokValueTag_Double = 5;
        private const byte TokValueTag_Float = 6;
        private const byte TokValueTag_Bool = 7;
        private const byte TokValueTag_Char = 8;
        private const byte TokValueTag_Token = 9;

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
                case double d:
                    w.WriteU8(TokValueTag_Double);
                    w.WriteU64((ulong)BitConverter.DoubleToInt64Bits(d));
                    return;
                case float f:
                    w.WriteU8(TokValueTag_Float);
                    w.WriteU32((uint)BitConverter.SingleToInt32Bits(f));
                    return;
                case bool b:
                    w.WriteU8(TokValueTag_Bool);
                    w.WriteU8(b ? (byte)1 : (byte)0);
                    return;
                case char c:
                    w.WriteU8(TokValueTag_Char);
                    w.WriteU32(c);
                    return;
                case Token inner:
                    w.WriteU8(TokValueTag_Token);
                    WriteToken(w, inner);
                    return;
                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"Token.Value type {t.Value.GetType().Name} not supported (type={t.Type}, value={t.Value}, pos={t.PositionStart.Ln}:{t.PositionStart.Col})");
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
                    // BigNumber.Parse. Round-trip via the string form.
                    value = u.ToString();
                    _ = s;
                    break;
                }
                case TokValueTag_Double: value = BitConverter.Int64BitsToDouble(unchecked((long)r.ReadU64())); break;
                case TokValueTag_Float: value = BitConverter.Int32BitsToSingle(unchecked((int)r.ReadU32())); break;
                case TokValueTag_Bool: value = r.ReadU8() != 0; break;
                case TokValueTag_Char: value = (char)r.ReadU32(); break;
                case TokValueTag_Token: value = ReadToken(r); break;
                default:
                    throw new InvalidDataException($"rac: unknown token value tag 0x{tag:X2}");
            }
            return new Token(type, value, ps, pe);
        }

        // --- TypeDescriptor ----------------------------------------------------
        //
        // Shape tag layout (u8):
        //   0x00 — Plain (Name + GenericArgs + Ref bits)
        //   0x01 — TypeParameter (TypeParameterName)
        //   0x02 — FunctionType (FunctionParamTypes + FunctionReturnType)
        //   0x03 — UnionType (UnionMembers)
        // Following the tag, payload-specific fields. Lifetime is encoded as
        // an optional string at the end of the Plain payload.
        private const byte TdTag_Plain = 0x00;
        private const byte TdTag_TypeParam = 0x01;
        private const byte TdTag_Function = 0x02;
        private const byte TdTag_Union = 0x03;

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

        // ---- L5: flat one-shot definition descriptors (RaFunction.TypeDefs) ----
        // Polymorphic pool: i32 count, then per entry a u8 kind tag + payload.
        // No AST — payloads are plain strings / ints / TypeDescriptors, so the
        // `.rac` carries definitions without an AstNodeSerializer exec round-trip.
        internal static void SerializeTypeDefs(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.TypeDef[] defs)
        {
            int n = defs?.Length ?? 0;
            w.WriteI32(n);
            for (int i = 0; i < n; i++)
            {
                var d = defs![i];
                w.WriteU8((byte)d.Kind);
                switch (d.Kind)
                {
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Enum:
                        WriteEnumDef(w, (RaLanguage.Interpreter.IR.Defs.EnumDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Delegate:
                        WriteDelegateDef(w, (RaLanguage.Interpreter.IR.Defs.DelegateDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Using:
                        WriteUsingDef(w, (RaLanguage.Interpreter.IR.Defs.UsingDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Struct:
                        WriteStructDef(w, (RaLanguage.Interpreter.IR.Defs.StructDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Record:
                        WriteRecordDef(w, (RaLanguage.Interpreter.IR.Defs.RecordDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Class:
                        WriteClassDef(w, (RaLanguage.Interpreter.IR.Defs.ClassDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Trait:
                        WriteTraitDef(w, (RaLanguage.Interpreter.IR.Defs.TraitDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Extension:
                        WriteExtensionDef(w, (RaLanguage.Interpreter.IR.Defs.ExtensionDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Interface:
                        WriteInterfaceDef(w, (RaLanguage.Interpreter.IR.Defs.InterfaceDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Annotation:
                        WriteAnnotationDef(w, (RaLanguage.Interpreter.IR.Defs.AnnotationDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Import:
                        WriteImportDef(w, (RaLanguage.Interpreter.IR.Defs.ImportDef)d);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Namespace:
                        WriteNamespaceDef(w, (RaLanguage.Interpreter.IR.Defs.NamespaceDef)d);
                        break;
                    default:
                        throw new System.IO.InvalidDataException($"rac: unknown TypeDef kind {(byte)d.Kind} on write");
                }
            }
        }

        internal static RaLanguage.Interpreter.IR.Defs.TypeDef[] DeserializeTypeDefs(RacBinaryReader r)
        {
            int n = r.ReadI32();
            if (n < 0 || n > 4_000_000)
                throw new System.IO.InvalidDataException($"rac: TypeDefs count {n} out of range");
            if (n == 0) return System.Array.Empty<RaLanguage.Interpreter.IR.Defs.TypeDef>();
            var defs = new RaLanguage.Interpreter.IR.Defs.TypeDef[n];
            for (int i = 0; i < n; i++)
            {
                byte kind = r.ReadU8();
                switch ((RaLanguage.Interpreter.IR.Defs.TypeDefKind)kind)
                {
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Enum:
                        defs[i] = ReadEnumDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Delegate:
                        defs[i] = ReadDelegateDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Using:
                        defs[i] = ReadUsingDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Struct:
                        defs[i] = ReadStructDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Record:
                        defs[i] = ReadRecordDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Class:
                        defs[i] = ReadClassDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Trait:
                        defs[i] = ReadTraitDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Extension:
                        defs[i] = ReadExtensionDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Interface:
                        defs[i] = ReadInterfaceDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Annotation:
                        defs[i] = ReadAnnotationDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Import:
                        defs[i] = ReadImportDef(r);
                        break;
                    case RaLanguage.Interpreter.IR.Defs.TypeDefKind.Namespace:
                        defs[i] = ReadNamespaceDef(r);
                        break;
                    default:
                        throw new System.IO.InvalidDataException($"rac: unknown TypeDef kind {kind} on read");
                }
            }
            return defs;
        }

        private static void WriteEnumDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.EnumDef def)
        {
            w.WriteString(def.Name);
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteI32(def.Variants.Length);
            foreach (var v in def.Variants)
            {
                w.WriteString(v.Name);
                w.WriteI32(v.Ordinal);
                w.WriteString(v.Value.ToString()); // Int128 as decimal text (one-shot path, not hot)
                w.WriteI32(v.PayloadTypes.Length);
                foreach (var p in v.PayloadTypes) WriteTypeDescriptor(w, p);
            }
        }

        private static RaLanguage.Interpreter.IR.Defs.EnumDef ReadEnumDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            var generics = ReadStringList(r).ToArray();
            int vn = r.ReadI32();
            if (vn < 0 || vn > 4_000_000)
                throw new System.IO.InvalidDataException($"rac: enum variant count {vn} out of range");
            var variants = new RaLanguage.Interpreter.IR.Defs.EnumVariantDef[vn];
            for (int i = 0; i < vn; i++)
            {
                string vName = r.ReadString() ?? "";
                int ordinal = r.ReadI32();
                System.Int128 value = System.Int128.Parse(r.ReadString() ?? "0");
                int pn = r.ReadI32();
                if (pn < 0 || pn > 4_000_000)
                    throw new System.IO.InvalidDataException($"rac: enum payload count {pn} out of range");
                var payloads = new TypeDescriptor[pn];
                for (int j = 0; j < pn; j++) payloads[j] = ReadTypeDescriptor(r);
                variants[i] = new RaLanguage.Interpreter.IR.Defs.EnumVariantDef(vName, ordinal, value, payloads);
            }
            return new RaLanguage.Interpreter.IR.Defs.EnumDef(name, generics, variants);
        }

        private static void WriteDelegateDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.DelegateDef def)
        {
            w.WriteString(def.Name);
            WriteTypeDescriptor(w, def.Signature);
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
        }

        private static RaLanguage.Interpreter.IR.Defs.DelegateDef ReadDelegateDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            TypeDescriptor signature = ReadTypeDescriptor(r);
            var generics = ReadStringList(r).ToArray();
            bool isPublic = r.ReadU8() != 0;
            return new RaLanguage.Interpreter.IR.Defs.DelegateDef(name, signature, generics, isPublic);
        }

        private static void WriteUsingDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.UsingDef def)
        {
            WriteStringList(w, new List<string>(def.Segments));
            // alias: u8 present-flag + string (empty/none collapses to flag 0)
            if (string.IsNullOrEmpty(def.Alias)) w.WriteU8(0);
            else { w.WriteU8(1); w.WriteString(def.Alias); }
        }

        private static RaLanguage.Interpreter.IR.Defs.UsingDef ReadUsingDef(RacBinaryReader r)
        {
            var segments = ReadStringList(r).ToArray();
            string? alias = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            return new RaLanguage.Interpreter.IR.Defs.UsingDef(segments, alias);
        }

        private static void WriteOptTd(RacBinaryWriter w, TypeDescriptor? td)
        {
            if (td == null) w.WriteU8(0); else { w.WriteU8(1); WriteTypeDescriptor(w, td); }
        }
        private static TypeDescriptor? ReadOptTd(RacBinaryReader r) => r.ReadU8() != 0 ? ReadTypeDescriptor(r) : null;

        private static void WriteStructDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.StructDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
            WriteStringList(w, new List<string>(def.Generics));

            w.WriteI32(def.Fields.Length);
            foreach (var fld in def.Fields)
            {
                w.WriteString(fld.Name);
                WriteOptTd(w, fld.FieldType);
                byte fflags = (byte)((fld.IsPublic ? 1 : 0) | (fld.IsStatic ? 2 : 0)
                    | (fld.IsAbstract ? 4 : 0) | (fld.IsOverride ? 8 : 0));
                w.WriteU8(fflags);
                w.WriteI32(fld.DeclKind);
                if (fld.DefaultConst == null) w.WriteU8(0);
                else { w.WriteU8(1); ModuleBytecodeIo.SerializeConst(w, fld.DefaultConst, WriterPool); }
            }

            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteMethodDef(w, m);
            WriteOperatorDefs(w, def.Operators);
            WriteWhereDefs(w, def.Wheres);
            WritePropertyDefs(w, def.Properties);
            WriteEventDefs(w, def.Events);
        }

        // Shared by struct + record (both carry StructMethodDef). Signature
        // metadata + the precompiled body RaFunction (const pool shared via the
        // thread-local writer pool).
        private static void WriteMethodDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.StructMethodDef m)
        {
            w.WriteString(m.Name);
            byte mflags = (byte)((m.IsPublic ? 1 : 0) | (m.IsConstructor ? 2 : 0)
                | (m.IsAsync ? 4 : 0) | (m.IsAsyncStream ? 8 : 0)
                | (m.HasVarArgs ? 16 : 0) | (m.ShouldAutoReturn ? 32 : 0));
            w.WriteU8(mflags);
            WriteStringList(w, new List<string>(m.ArgNames));
            w.WriteI32(m.ArgTypes.Length);
            foreach (var t in m.ArgTypes) WriteOptTd(w, t);
            w.WriteI32(m.IsRefParams.Length);
            foreach (var rp in m.IsRefParams) w.WriteU8(rp ? (byte)1 : (byte)0);
            if (m.VarArgName == null) w.WriteU8(0); else { w.WriteU8(1); w.WriteString(m.VarArgName); }
            WriteOptTd(w, m.VarArgType);
            WriteOptTd(w, m.ReturnType);
            w.WriteI32(m.FrameId);
            ModuleBytecodeIo.SerializeRaFunction(w, m.Body, WriterPool);
        }

        private static RaLanguage.Interpreter.IR.Defs.StructMethodDef ReadMethodDef(RacBinaryReader r)
        {
            string mName = r.ReadString() ?? "";
            byte mflags = r.ReadU8();
            var argNames = ReadStringList(r).ToArray();
            int atn = r.ReadI32();
            if (atn < 0 || atn > 4_000_000) throw new System.IO.InvalidDataException($"rac: method arg-type count {atn} out of range");
            var argTypes = new TypeDescriptor?[atn];
            for (int j = 0; j < atn; j++) argTypes[j] = ReadOptTd(r);
            int rpn = r.ReadI32();
            if (rpn < 0 || rpn > 4_000_000) throw new System.IO.InvalidDataException($"rac: method ref-param count {rpn} out of range");
            var refParams = new bool[rpn];
            for (int j = 0; j < rpn; j++) refParams[j] = r.ReadU8() != 0;
            string? varArgName = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            var varArgType = ReadOptTd(r);
            var returnType = ReadOptTd(r);
            int frameId = r.ReadI32();
            var body = ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool);
            return new RaLanguage.Interpreter.IR.Defs.StructMethodDef(
                mName, (mflags & 1) != 0, (mflags & 2) != 0, (mflags & 4) != 0, (mflags & 8) != 0,
                argNames, argTypes, refParams, (mflags & 16) != 0, varArgName, varArgType, returnType,
                (mflags & 32) != 0, frameId, body);
        }

        // L10 v7: an OperatorDef (operator overload — dispatch keys on OpTokenType,
        // NOT the symbol text). Shared by struct / record / class lowering.
        private static void WriteOperatorDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.OperatorDef op)
        {
            w.WriteI32((int)op.OpTokenType);
            w.WriteString(op.Symbol);
            byte oflags = (byte)((op.IsPublic ? 1 : 0) | (op.IsOverride ? 2 : 0)
                | (op.IsStatic ? 4 : 0) | (op.ShouldAutoReturn ? 8 : 0));
            w.WriteU8(oflags);
            w.WriteString(op.ArgName);
            WriteOptTd(w, op.ArgType);
            WriteOptTd(w, op.ReturnType);
            WriteStringList(w, new List<string>(op.Generics));
            w.WriteI32(op.FrameId);
            ModuleBytecodeIo.SerializeRaFunction(w, op.Body, WriterPool);
        }

        private static RaLanguage.Interpreter.IR.Defs.OperatorDef ReadOperatorDef(RacBinaryReader r)
        {
            var opType = (RaLanguage.Lexer.Tokens.TokenType)r.ReadI32();
            string symbol = r.ReadString() ?? "";
            byte oflags = r.ReadU8();
            string argName = r.ReadString() ?? "";
            var argType = ReadOptTd(r);
            var returnType = ReadOptTd(r);
            var generics = ReadStringList(r).ToArray();
            int frameId = r.ReadI32();
            var body = ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool);
            return new RaLanguage.Interpreter.IR.Defs.OperatorDef(
                opType, symbol, (oflags & 1) != 0, (oflags & 2) != 0, (oflags & 4) != 0,
                argName, argType, returnType, (oflags & 8) != 0, generics, frameId, body);
        }

        // Trailing OperatorDef[] pool — written only by v7+ writers, read only by
        // v7+ readers. v6 archives keep no operators (empty), loading unchanged.
        private static void WriteOperatorDefs(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.OperatorDef[] ops)
        {
            if (WriterVersion < ModuleBytecodeIo.PayloadVersion_V7) return;
            w.WriteI32(ops.Length);
            foreach (var op in ops) WriteOperatorDef(w, op);
        }

        private static RaLanguage.Interpreter.IR.Defs.OperatorDef[] ReadOperatorDefs(RacBinaryReader r)
        {
            if (ReaderVersion < ModuleBytecodeIo.PayloadVersion_V7)
                return System.Array.Empty<RaLanguage.Interpreter.IR.Defs.OperatorDef>();
            int on = r.ReadI32();
            if (on < 0 || on > 4_000_000) throw new System.IO.InvalidDataException($"rac: operator count {on} out of range");
            var ops = new RaLanguage.Interpreter.IR.Defs.OperatorDef[on];
            for (int i = 0; i < on; i++) ops[i] = ReadOperatorDef(r);
            return ops;
        }

        // L10 v8: a WhereConstraintDef (generic `where T: Bound`). No body — the
        // param name + bound TypeDescriptor. Shared by struct/record/class.
        private static void WriteWhereDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.WhereConstraintDef wc)
        {
            w.WriteString(wc.ParameterName);
            WriteOptTd(w, wc.ConstraintType);
        }

        private static RaLanguage.Interpreter.IR.Defs.WhereConstraintDef ReadWhereDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            var ct = ReadOptTd(r) ?? new TypeDescriptor("any");
            return new RaLanguage.Interpreter.IR.Defs.WhereConstraintDef(name, ct);
        }

        // Trailing WhereConstraintDef[] pool — v8+ only. v7 archives keep none.
        private static void WriteWhereDefs(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.WhereConstraintDef[] wheres)
        {
            if (WriterVersion < ModuleBytecodeIo.PayloadVersion_V8) return;
            w.WriteI32(wheres.Length);
            foreach (var wc in wheres) WriteWhereDef(w, wc);
        }

        private static RaLanguage.Interpreter.IR.Defs.WhereConstraintDef[] ReadWhereDefs(RacBinaryReader r)
        {
            if (ReaderVersion < ModuleBytecodeIo.PayloadVersion_V8)
                return System.Array.Empty<RaLanguage.Interpreter.IR.Defs.WhereConstraintDef>();
            int wn = r.ReadI32();
            if (wn < 0 || wn > 4_000_000) throw new System.IO.InvalidDataException($"rac: where-constraint count {wn} out of range");
            var wheres = new RaLanguage.Interpreter.IR.Defs.WhereConstraintDef[wn];
            for (int i = 0; i < wn; i++) wheres[i] = ReadWhereDef(r);
            return wheres;
        }

        // L10 v8: an AUTO PropertyDef — name + type + flags + const default + the
        // accessor list (Kind/Visibility, no body). Shared by struct/record/class.
        private static void WritePropertyDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.PropertyDef p)
        {
            w.WriteString(p.Name);
            WriteOptTd(w, p.PropertyType);
            byte pflags = (byte)((p.IsPublic ? 1 : 0) | (p.IsStatic ? 2 : 0)
                | (p.IsAbstract ? 4 : 0) | (p.IsOverride ? 8 : 0) | (p.IsLazy ? 16 : 0));
            w.WriteU8(pflags);
            if (p.DefaultConst == null) w.WriteU8(0);
            else { w.WriteU8(1); ModuleBytecodeIo.SerializeConst(w, p.DefaultConst, WriterPool); }
            w.WriteI32(p.Accessors.Length);
            foreach (var a in p.Accessors)
            {
                w.WriteI32(a.Kind);
                w.WriteI32(a.Visibility);
                if (a.Body == null) w.WriteU8(0);
                else { w.WriteU8(1); ModuleBytecodeIo.SerializeRaFunction(w, a.Body, WriterPool); }
            }
        }

        private static RaLanguage.Interpreter.IR.Defs.PropertyDef ReadPropertyDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            var ptype = ReadOptTd(r);
            byte pflags = r.ReadU8();
            RaLanguage.Interpreter.Values.RuntimeValue? defConst =
                r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeConst(r, ReaderPool) : null;
            int an = r.ReadI32();
            if (an < 0 || an > 4_000_000) throw new System.IO.InvalidDataException($"rac: property accessor count {an} out of range");
            var accessors = new RaLanguage.Interpreter.IR.Defs.PropertyAccessorDef[an];
            for (int i = 0; i < an; i++)
            {
                int k = r.ReadI32();
                int v = r.ReadI32();
                RaLanguage.Interpreter.IR.RaFunction? body =
                    r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool) : null;
                accessors[i] = new RaLanguage.Interpreter.IR.Defs.PropertyAccessorDef(k, v, body);
            }
            return new RaLanguage.Interpreter.IR.Defs.PropertyDef(
                name, ptype, (pflags & 1) != 0, (pflags & 2) != 0, (pflags & 4) != 0,
                (pflags & 8) != 0, (pflags & 16) != 0, defConst, accessors);
        }

        // Trailing PropertyDef[] pool — v8+ only. v7 archives keep none.
        private static void WritePropertyDefs(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.PropertyDef[] props)
        {
            if (WriterVersion < ModuleBytecodeIo.PayloadVersion_V8) return;
            w.WriteI32(props.Length);
            foreach (var p in props) WritePropertyDef(w, p);
        }

        private static RaLanguage.Interpreter.IR.Defs.PropertyDef[] ReadPropertyDefs(RacBinaryReader r)
        {
            if (ReaderVersion < ModuleBytecodeIo.PayloadVersion_V8)
                return System.Array.Empty<RaLanguage.Interpreter.IR.Defs.PropertyDef>();
            int pn = r.ReadI32();
            if (pn < 0 || pn > 4_000_000) throw new System.IO.InvalidDataException($"rac: property count {pn} out of range");
            var props = new RaLanguage.Interpreter.IR.Defs.PropertyDef[pn];
            for (int i = 0; i < pn; i++) props[i] = ReadPropertyDef(r);
            return props;
        }

        // L10 v8: an EventDef — flat metadata (events have no accessor bodies):
        // name + flags + payload params + accessor (Kind/Visibility) list.
        private static void WriteEventDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.EventDef ev)
        {
            w.WriteString(ev.Name);
            int flags = (ev.IsPublic ? 1 : 0) | (ev.IsStatic ? 2 : 0) | (ev.IsAbstract ? 4 : 0)
                | (ev.IsOverride ? 8 : 0) | (ev.IsCancellable ? 16 : 0) | (ev.IsTolerant ? 32 : 0)
                | (ev.IsAsync ? 64 : 0);
            w.WriteU8((byte)flags);
            w.WriteI32(ev.PayloadParams.Length);
            foreach (var pp in ev.PayloadParams) { w.WriteString(pp.Name); WriteOptTd(w, pp.Type); }
            w.WriteI32(ev.Accessors.Length);
            foreach (var a in ev.Accessors) { w.WriteI32(a.Kind); w.WriteI32(a.Visibility); }
        }

        private static RaLanguage.Interpreter.IR.Defs.EventDef ReadEventDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            int flags = r.ReadU8();
            int ppn = r.ReadI32();
            if (ppn < 0 || ppn > 4_000_000) throw new System.IO.InvalidDataException($"rac: event payload count {ppn} out of range");
            var payload = new RaLanguage.Interpreter.IR.Defs.EventPayloadParamDef[ppn];
            for (int i = 0; i < ppn; i++) { string pn = r.ReadString() ?? ""; var pt = ReadOptTd(r); payload[i] = new RaLanguage.Interpreter.IR.Defs.EventPayloadParamDef(pn, pt); }
            int an = r.ReadI32();
            if (an < 0 || an > 4_000_000) throw new System.IO.InvalidDataException($"rac: event accessor count {an} out of range");
            var accessors = new RaLanguage.Interpreter.IR.Defs.EventAccessorDef[an];
            for (int i = 0; i < an; i++) { int k = r.ReadI32(); int v = r.ReadI32(); accessors[i] = new RaLanguage.Interpreter.IR.Defs.EventAccessorDef(k, v); }
            return new RaLanguage.Interpreter.IR.Defs.EventDef(
                name, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
                (flags & 16) != 0, (flags & 32) != 0, (flags & 64) != 0, payload, accessors);
        }

        // Trailing EventDef[] pool — v8+ only. v7 archives keep none.
        private static void WriteEventDefs(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.EventDef[] events)
        {
            if (WriterVersion < ModuleBytecodeIo.PayloadVersion_V8) return;
            w.WriteI32(events.Length);
            foreach (var ev in events) WriteEventDef(w, ev);
        }

        private static RaLanguage.Interpreter.IR.Defs.EventDef[] ReadEventDefs(RacBinaryReader r)
        {
            if (ReaderVersion < ModuleBytecodeIo.PayloadVersion_V8)
                return System.Array.Empty<RaLanguage.Interpreter.IR.Defs.EventDef>();
            int en = r.ReadI32();
            if (en < 0 || en > 4_000_000) throw new System.IO.InvalidDataException($"rac: event count {en} out of range");
            var events = new RaLanguage.Interpreter.IR.Defs.EventDef[en];
            for (int i = 0; i < en; i++) events[i] = ReadEventDef(r);
            return events;
        }

        private static RaLanguage.Interpreter.IR.Defs.StructDef ReadStructDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isPublic = r.ReadU8() != 0;
            var generics = ReadStringList(r).ToArray();

            int fn = r.ReadI32();
            if (fn < 0 || fn > 4_000_000) throw new System.IO.InvalidDataException($"rac: struct field count {fn} out of range");
            var fields = new RaLanguage.Interpreter.IR.Defs.StructFieldDef[fn];
            for (int i = 0; i < fn; i++)
            {
                string fName = r.ReadString() ?? "";
                var fType = ReadOptTd(r);
                byte fflags = r.ReadU8();
                int declKind = r.ReadI32();
                RaLanguage.Interpreter.Values.RuntimeValue? defConst =
                    r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeConst(r, ReaderPool) : null;
                fields[i] = new RaLanguage.Interpreter.IR.Defs.StructFieldDef(
                    fName, fType, (fflags & 1) != 0, (fflags & 2) != 0, (fflags & 4) != 0, (fflags & 8) != 0,
                    declKind, defConst);
            }

            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: struct method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.StructMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadMethodDef(r);

            var operators = ReadOperatorDefs(r);
            var wheres = ReadWhereDefs(r);
            var properties = ReadPropertyDefs(r);
            var events = ReadEventDefs(r);
            return new RaLanguage.Interpreter.IR.Defs.StructDef(name, isPublic, generics, fields, methods, operators, wheres, properties, events);
        }

        private static void WriteRecordDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.RecordDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8((byte)((def.IsPublic ? 1 : 0) | (def.IsRefRecord ? 2 : 0)
                | (def.AutoEquals ? 4 : 0) | (def.AutoToString ? 8 : 0)));
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteI32(def.PrimaryFields.Length);
            foreach (var pf in def.PrimaryFields)
            {
                w.WriteString(pf.Name);
                WriteOptTd(w, pf.FieldType);
                w.WriteU8((byte)((pf.IsPublic ? 1 : 0) | (pf.IsMutable ? 2 : 0)));
                if (pf.DefaultConst == null) w.WriteU8(0);
                else { w.WriteU8(1); ModuleBytecodeIo.SerializeConst(w, pf.DefaultConst, WriterPool); }
            }
            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteMethodDef(w, m);
            WriteOperatorDefs(w, def.Operators);
            WriteWhereDefs(w, def.Wheres);
            WritePropertyDefs(w, def.Properties);
            WriteEventDefs(w, def.Events);
        }

        private static RaLanguage.Interpreter.IR.Defs.RecordDef ReadRecordDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            byte rflags = r.ReadU8();
            var generics = ReadStringList(r).ToArray();
            int pfn = r.ReadI32();
            if (pfn < 0 || pfn > 4_000_000) throw new System.IO.InvalidDataException($"rac: record primary-field count {pfn} out of range");
            var primaryFields = new RaLanguage.Interpreter.IR.Defs.RecordPrimaryFieldDef[pfn];
            for (int i = 0; i < pfn; i++)
            {
                string pName = r.ReadString() ?? "";
                var pType = ReadOptTd(r);
                byte pflags = r.ReadU8();
                RaLanguage.Interpreter.Values.RuntimeValue? pDef =
                    r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeConst(r, ReaderPool) : null;
                primaryFields[i] = new RaLanguage.Interpreter.IR.Defs.RecordPrimaryFieldDef(
                    pName, pType, (pflags & 1) != 0, (pflags & 2) != 0, pDef);
            }
            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: record method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.StructMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadMethodDef(r);
            var operators = ReadOperatorDefs(r);
            var wheres = ReadWhereDefs(r);
            var properties = ReadPropertyDefs(r);
            var events = ReadEventDefs(r);
            return new RaLanguage.Interpreter.IR.Defs.RecordDef(
                name, (rflags & 1) != 0, (rflags & 2) != 0, (rflags & 4) != 0, (rflags & 8) != 0,
                generics, primaryFields, methods, operators, wheres, properties, events);
        }

        private static void WriteFieldDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.StructFieldDef fld)
        {
            w.WriteString(fld.Name);
            WriteOptTd(w, fld.FieldType);
            byte fflags = (byte)((fld.IsPublic ? 1 : 0) | (fld.IsStatic ? 2 : 0)
                | (fld.IsAbstract ? 4 : 0) | (fld.IsOverride ? 8 : 0));
            w.WriteU8(fflags);
            w.WriteI32(fld.DeclKind);
            if (fld.DefaultConst == null) w.WriteU8(0);
            else { w.WriteU8(1); ModuleBytecodeIo.SerializeConst(w, fld.DefaultConst, WriterPool); }
        }

        private static RaLanguage.Interpreter.IR.Defs.StructFieldDef ReadFieldDef(RacBinaryReader r)
        {
            string fName = r.ReadString() ?? "";
            var fType = ReadOptTd(r);
            byte fflags = r.ReadU8();
            int declKind = r.ReadI32();
            RaLanguage.Interpreter.Values.RuntimeValue? defConst =
                r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeConst(r, ReaderPool) : null;
            return new RaLanguage.Interpreter.IR.Defs.StructFieldDef(
                fName, fType, (fflags & 1) != 0, (fflags & 2) != 0, (fflags & 4) != 0, (fflags & 8) != 0,
                declKind, defConst);
        }

        private static void WriteClassMethodDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.ClassMethodDef m)
        {
            w.WriteString(m.Name);
            int flags = (m.IsPublic ? 1 : 0) | (m.IsConstructor ? 2 : 0) | (m.IsAsync ? 4 : 0)
                | (m.IsAsyncStream ? 8 : 0) | (m.HasVarArgs ? 16 : 0) | (m.ShouldAutoReturn ? 32 : 0)
                | (m.IsOverride ? 64 : 0) | (m.IsStatic ? 128 : 0);
            w.WriteU8((byte)flags);
            WriteStringList(w, new List<string>(m.ArgNames));
            w.WriteI32(m.ArgTypes.Length);
            foreach (var t in m.ArgTypes) WriteOptTd(w, t);
            w.WriteI32(m.IsRefParams.Length);
            foreach (var rp in m.IsRefParams) w.WriteU8(rp ? (byte)1 : (byte)0);
            if (m.VarArgName == null) w.WriteU8(0); else { w.WriteU8(1); w.WriteString(m.VarArgName); }
            WriteOptTd(w, m.VarArgType);
            WriteOptTd(w, m.ReturnType);
            w.WriteI32(m.FrameId);
            ModuleBytecodeIo.SerializeRaFunction(w, m.Body, WriterPool);
            // v8: method-level generic type params (generic methods) + factory/named-ctor metadata.
            if (WriterVersion >= ModuleBytecodeIo.PayloadVersion_V8)
            {
                WriteStringList(w, new List<string>(m.Generics));
                w.WriteU8(m.IsFactory ? (byte)1 : (byte)0);
                if (m.ConstructorName == null) w.WriteU8(0);
                else { w.WriteU8(1); w.WriteString(m.ConstructorName); }
            }
        }

        private static RaLanguage.Interpreter.IR.Defs.ClassMethodDef ReadClassMethodDef(RacBinaryReader r)
        {
            string mName = r.ReadString() ?? "";
            int flags = r.ReadU8();
            var argNames = ReadStringList(r).ToArray();
            int atn = r.ReadI32();
            if (atn < 0 || atn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class method arg-type count {atn} out of range");
            var argTypes = new TypeDescriptor?[atn];
            for (int j = 0; j < atn; j++) argTypes[j] = ReadOptTd(r);
            int rpn = r.ReadI32();
            if (rpn < 0 || rpn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class method ref-param count {rpn} out of range");
            var refParams = new bool[rpn];
            for (int j = 0; j < rpn; j++) refParams[j] = r.ReadU8() != 0;
            string? varArgName = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            var varArgType = ReadOptTd(r);
            var returnType = ReadOptTd(r);
            int frameId = r.ReadI32();
            var body = ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool);
            var generics = System.Array.Empty<string>();
            bool isFactory = false;
            string? constructorName = null;
            if (ReaderVersion >= ModuleBytecodeIo.PayloadVersion_V8)
            {
                generics = ReadStringList(r).ToArray();
                isFactory = r.ReadU8() != 0;
                constructorName = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            }
            return new RaLanguage.Interpreter.IR.Defs.ClassMethodDef(
                mName, (flags & 1) != 0, (flags & 2) != 0, (flags & 64) != 0, (flags & 128) != 0,
                (flags & 4) != 0, (flags & 8) != 0, argNames, argTypes, refParams, (flags & 16) != 0,
                varArgName, varArgType, returnType, (flags & 32) != 0, frameId, body, generics, isFactory, constructorName);
        }

        private static void WriteClassDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.ClassDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteI32(def.Fields.Length);
            foreach (var fld in def.Fields) WriteFieldDef(w, fld);
            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteClassMethodDef(w, m);
            WriteOperatorDefs(w, def.Operators);
            WriteWhereDefs(w, def.Wheres);
            WritePropertyDefs(w, def.Properties);
            WriteEventDefs(w, def.Events);
            // L10 v8 inheritance: base + interfaces + traits.
            if (WriterVersion >= ModuleBytecodeIo.PayloadVersion_V8)
            {
                WriteOptTd(w, def.BaseType);
                w.WriteI32(def.Interfaces.Length);
                foreach (var td in def.Interfaces) WriteOptTd(w, td);
                w.WriteI32(def.Traits.Length);
                foreach (var td in def.Traits) WriteOptTd(w, td);
            }
        }

        private static RaLanguage.Interpreter.IR.Defs.ClassDef ReadClassDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isPublic = r.ReadU8() != 0;
            var generics = ReadStringList(r).ToArray();
            int fn = r.ReadI32();
            if (fn < 0 || fn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class field count {fn} out of range");
            var fields = new RaLanguage.Interpreter.IR.Defs.StructFieldDef[fn];
            for (int i = 0; i < fn; i++) fields[i] = ReadFieldDef(r);
            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.ClassMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadClassMethodDef(r);
            var operators = ReadOperatorDefs(r);
            var wheres = ReadWhereDefs(r);
            var properties = ReadPropertyDefs(r);
            var events = ReadEventDefs(r);
            TypeDescriptor? baseType = null;
            var interfaces = System.Array.Empty<TypeDescriptor>();
            var traits = System.Array.Empty<TypeDescriptor>();
            if (ReaderVersion >= ModuleBytecodeIo.PayloadVersion_V8)
            {
                baseType = ReadOptTd(r);
                int icn = r.ReadI32();
                if (icn < 0 || icn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class interface count {icn} out of range");
                interfaces = new TypeDescriptor[icn];
                for (int i = 0; i < icn; i++) interfaces[i] = ReadOptTd(r)!;
                int tcn = r.ReadI32();
                if (tcn < 0 || tcn > 4_000_000) throw new System.IO.InvalidDataException($"rac: class trait count {tcn} out of range");
                traits = new TypeDescriptor[tcn];
                for (int i = 0; i < tcn; i++) traits[i] = ReadOptTd(r)!;
            }
            return new RaLanguage.Interpreter.IR.Defs.ClassDef(name, isPublic, generics, fields, methods, operators, wheres, properties, events, baseType, interfaces, traits);
        }

        private static void WriteTraitMethodDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.TraitMethodDef m)
        {
            w.WriteString(m.Name);
            int flags = (m.IsAbstract ? 1 : 0) | (m.IsAsync ? 2 : 0) | (m.IsAsyncStream ? 4 : 0)
                | (m.HasVarArgs ? 8 : 0) | (m.ShouldAutoReturn ? 16 : 0);
            w.WriteU8((byte)flags);
            WriteStringList(w, new List<string>(m.ArgNames));
            w.WriteI32(m.ArgTypes.Length);
            foreach (var t in m.ArgTypes) WriteOptTd(w, t);
            w.WriteI32(m.IsRefParams.Length);
            foreach (var rp in m.IsRefParams) w.WriteU8(rp ? (byte)1 : (byte)0);
            if (m.VarArgName == null) w.WriteU8(0); else { w.WriteU8(1); w.WriteString(m.VarArgName); }
            WriteOptTd(w, m.VarArgType);
            WriteOptTd(w, m.ReturnType);
            w.WriteI32(m.FrameId);
            if (m.Body == null) w.WriteU8(0);
            else { w.WriteU8(1); ModuleBytecodeIo.SerializeRaFunction(w, m.Body, WriterPool); }
        }

        private static RaLanguage.Interpreter.IR.Defs.TraitMethodDef ReadTraitMethodDef(RacBinaryReader r)
        {
            string mName = r.ReadString() ?? "";
            int flags = r.ReadU8();
            var argNames = ReadStringList(r).ToArray();
            int atn = r.ReadI32();
            if (atn < 0 || atn > 4_000_000) throw new System.IO.InvalidDataException($"rac: trait method arg-type count {atn} out of range");
            var argTypes = new TypeDescriptor?[atn];
            for (int j = 0; j < atn; j++) argTypes[j] = ReadOptTd(r);
            int rpn = r.ReadI32();
            if (rpn < 0 || rpn > 4_000_000) throw new System.IO.InvalidDataException($"rac: trait method ref-param count {rpn} out of range");
            var refParams = new bool[rpn];
            for (int j = 0; j < rpn; j++) refParams[j] = r.ReadU8() != 0;
            string? varArgName = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            var varArgType = ReadOptTd(r);
            var returnType = ReadOptTd(r);
            int frameId = r.ReadI32();
            RaLanguage.Interpreter.IR.RaFunction? body =
                r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool) : null;
            return new RaLanguage.Interpreter.IR.Defs.TraitMethodDef(
                mName, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, argNames, argTypes, refParams,
                (flags & 8) != 0, varArgName, varArgType, returnType, (flags & 16) != 0, frameId, body);
        }

        private static void WriteTraitDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.TraitDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteI32(def.Fields.Length);
            foreach (var fld in def.Fields) WriteFieldDef(w, fld);
            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteTraitMethodDef(w, m);
        }

        private static RaLanguage.Interpreter.IR.Defs.TraitDef ReadTraitDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isPublic = r.ReadU8() != 0;
            var generics = ReadStringList(r).ToArray();
            int fn = r.ReadI32();
            if (fn < 0 || fn > 4_000_000) throw new System.IO.InvalidDataException($"rac: trait field count {fn} out of range");
            var fields = new RaLanguage.Interpreter.IR.Defs.StructFieldDef[fn];
            for (int i = 0; i < fn; i++) fields[i] = ReadFieldDef(r);
            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: trait method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.TraitMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadTraitMethodDef(r);
            return new RaLanguage.Interpreter.IR.Defs.TraitDef(name, isPublic, generics, fields, methods);
        }

        private static void WriteExtensionDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.ExtensionDef def)
        {
            WriteTypeDescriptor(w, def.TargetType);
            w.WriteU8((byte)((def.IsPublic ? 1 : 0) | (def.IsSealed ? 2 : 0)));
            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteClassMethodDef(w, m);
        }

        private static RaLanguage.Interpreter.IR.Defs.ExtensionDef ReadExtensionDef(RacBinaryReader r)
        {
            var targetType = ReadTypeDescriptor(r);
            byte flags = r.ReadU8();
            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: extension method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.ClassMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadClassMethodDef(r);
            return new RaLanguage.Interpreter.IR.Defs.ExtensionDef(targetType, (flags & 1) != 0, (flags & 2) != 0, methods);
        }

        // Interface methods are pure signatures: no body, no flags — just the
        // name + param names + param types + return type.
        private static void WriteInterfaceMethodDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.InterfaceMethodDef m)
        {
            w.WriteString(m.Name);
            WriteStringList(w, new List<string>(m.ArgNames));
            w.WriteI32(m.ArgTypes.Length);
            foreach (var t in m.ArgTypes) WriteOptTd(w, t);
            WriteOptTd(w, m.ReturnType);
        }

        private static RaLanguage.Interpreter.IR.Defs.InterfaceMethodDef ReadInterfaceMethodDef(RacBinaryReader r)
        {
            string mName = r.ReadString() ?? "";
            var argNames = ReadStringList(r).ToArray();
            int atn = r.ReadI32();
            if (atn < 0 || atn > 4_000_000) throw new System.IO.InvalidDataException($"rac: interface method arg-type count {atn} out of range");
            var argTypes = new TypeDescriptor?[atn];
            for (int j = 0; j < atn; j++) argTypes[j] = ReadOptTd(r);
            var returnType = ReadOptTd(r);
            return new RaLanguage.Interpreter.IR.Defs.InterfaceMethodDef(mName, argNames, argTypes, returnType);
        }

        private static void WriteInterfaceDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.InterfaceDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
            WriteStringList(w, new List<string>(def.Generics));
            w.WriteI32(def.Fields.Length);
            foreach (var fld in def.Fields) WriteFieldDef(w, fld);
            w.WriteI32(def.Methods.Length);
            foreach (var m in def.Methods) WriteInterfaceMethodDef(w, m);
        }

        private static RaLanguage.Interpreter.IR.Defs.InterfaceDef ReadInterfaceDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isPublic = r.ReadU8() != 0;
            var generics = ReadStringList(r).ToArray();
            int fn = r.ReadI32();
            if (fn < 0 || fn > 4_000_000) throw new System.IO.InvalidDataException($"rac: interface field count {fn} out of range");
            var fields = new RaLanguage.Interpreter.IR.Defs.StructFieldDef[fn];
            for (int i = 0; i < fn; i++) fields[i] = ReadFieldDef(r);
            int mn = r.ReadI32();
            if (mn < 0 || mn > 4_000_000) throw new System.IO.InvalidDataException($"rac: interface method count {mn} out of range");
            var methods = new RaLanguage.Interpreter.IR.Defs.InterfaceMethodDef[mn];
            for (int i = 0; i < mn; i++) methods[i] = ReadInterfaceMethodDef(r);
            return new RaLanguage.Interpreter.IR.Defs.InterfaceDef(name, isPublic, generics, fields, methods);
        }

        private static void WriteAnnotationParamDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.AnnotationParamDef p)
        {
            w.WriteString(p.Name);
            WriteOptTd(w, p.DeclaredType);
            w.WriteU8(p.IsVarArgs ? (byte)1 : (byte)0);
            if (p.DefaultConst == null) w.WriteU8(0);
            else { w.WriteU8(1); ModuleBytecodeIo.SerializeConst(w, p.DefaultConst, WriterPool); }
        }

        private static RaLanguage.Interpreter.IR.Defs.AnnotationParamDef ReadAnnotationParamDef(RacBinaryReader r)
        {
            string pName = r.ReadString() ?? "";
            var declTd = ReadOptTd(r);
            bool isVarArgs = r.ReadU8() != 0;
            RaLanguage.Interpreter.Values.RuntimeValue? defConst =
                r.ReadU8() != 0 ? ModuleBytecodeIo.DeserializeConst(r, ReaderPool) : null;
            return new RaLanguage.Interpreter.IR.Defs.AnnotationParamDef(pName, declTd, defConst, isVarArgs);
        }

        private static void WriteAnnotationDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.AnnotationDef def)
        {
            w.WriteString(def.Name);
            w.WriteU8(def.IsPublic ? (byte)1 : (byte)0);
            w.WriteI32(def.Parameters.Length);
            foreach (var p in def.Parameters) WriteAnnotationParamDef(w, p);
        }

        private static RaLanguage.Interpreter.IR.Defs.AnnotationDef ReadAnnotationDef(RacBinaryReader r)
        {
            string name = r.ReadString() ?? "";
            bool isPublic = r.ReadU8() != 0;
            int pn = r.ReadI32();
            if (pn < 0 || pn > 4_000_000) throw new System.IO.InvalidDataException($"rac: annotation param count {pn} out of range");
            var ps = new RaLanguage.Interpreter.IR.Defs.AnnotationParamDef[pn];
            for (int i = 0; i < pn; i++) ps[i] = ReadAnnotationParamDef(r);
            return new RaLanguage.Interpreter.IR.Defs.AnnotationDef(name, isPublic, ps);
        }

        private static void WriteImportDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.ImportDef def)
        {
            w.WriteU8((byte)def.ImportKind);
            w.WriteU8((byte)((def.SpecIsDotted ? 1 : 0) | (def.IsWildcard ? 2 : 0)));
            if (def.RawPath == null) w.WriteU8(0); else { w.WriteU8(1); w.WriteString(def.RawPath); }
            WriteStringList(w, new List<string>(def.Segments));
            WriteStringList(w, new List<string>(def.SymbolNames));
            if (def.Alias == null) w.WriteU8(0); else { w.WriteU8(1); w.WriteString(def.Alias); }
        }

        private static RaLanguage.Interpreter.IR.Defs.ImportDef ReadImportDef(RacBinaryReader r)
        {
            var importKind = (RaLanguage.Interpreter.IR.Defs.ImportDefKind)r.ReadU8();
            byte flags = r.ReadU8();
            string? rawPath = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            var segments = ReadStringList(r).ToArray();
            var symbolNames = ReadStringList(r).ToArray();
            string? alias = r.ReadU8() != 0 ? (r.ReadString() ?? "") : null;
            return new RaLanguage.Interpreter.IR.Defs.ImportDef(
                importKind, (flags & 1) != 0, rawPath, segments, (flags & 2) != 0, symbolNames, alias);
        }

        private static void WriteNamespaceDef(RacBinaryWriter w, RaLanguage.Interpreter.IR.Defs.NamespaceDef def)
        {
            WriteStringList(w, new List<string>(def.Segments));
            w.WriteU8(def.IsFileScoped ? (byte)1 : (byte)0);
            w.WriteI32(def.Bodies.Length);
            foreach (var b in def.Bodies) ModuleBytecodeIo.SerializeRaFunction(w, b, WriterPool);
        }

        private static RaLanguage.Interpreter.IR.Defs.NamespaceDef ReadNamespaceDef(RacBinaryReader r)
        {
            var segments = ReadStringList(r).ToArray();
            bool isFileScoped = r.ReadU8() != 0;
            int bn = r.ReadI32();
            if (bn < 0 || bn > 4_000_000) throw new System.IO.InvalidDataException($"rac: namespace body count {bn} out of range");
            var bodies = new RaLanguage.Interpreter.IR.RaFunction[bn];
            for (int i = 0; i < bn; i++) bodies[i] = ModuleBytecodeIo.DeserializeRaFunction(r, ReaderPool);
            return new RaLanguage.Interpreter.IR.Defs.NamespaceDef(segments, isFileScoped, bodies);
        }

        private static void WriteTypeDescriptor(RacBinaryWriter w, TypeDescriptor td)
        {
            if (td.IsTypeParameter)
            {
                w.WriteU8(TdTag_TypeParam);
                w.WriteString(td.TypeParameterName);
                return;
            }
            if (td.IsUnionType)
            {
                w.WriteU8(TdTag_Union);
                var members = td.UnionMembers ?? new List<TypeDescriptor>();
                w.WriteI32(members.Count);
                foreach (var m in members) WriteTypeDescriptor(w, m);
                return;
            }
            if (td.IsFunctionType)
            {
                w.WriteU8(TdTag_Function);
                var ps = td.FunctionParamTypes ?? new List<TypeDescriptor>();
                w.WriteI32(ps.Count);
                foreach (var p in ps) WriteTypeDescriptor(w, p);
                if (td.FunctionReturnType == null) w.WriteU8(0);
                else { w.WriteU8(1); WriteTypeDescriptor(w, td.FunctionReturnType); }
                return;
            }
            // Plain.
            w.WriteU8(TdTag_Plain);
            w.WriteString(td.Name);
            w.WriteU8(td.IsRefType ? (byte)1 : (byte)0);
            w.WriteU8(td.IsMutableRef ? (byte)1 : (byte)0);
            if (td.RefElementType == null) w.WriteU8(0);
            else { w.WriteU8(1); WriteTypeDescriptor(w, td.RefElementType); }
            w.WriteI32(td.GenericArgs?.Count ?? 0);
            if (td.GenericArgs != null)
                foreach (var g in td.GenericArgs) WriteTypeDescriptor(w, g);
            if (td.Lifetime == null) w.WriteU8(0);
            else { w.WriteU8(1); w.WriteString(td.Lifetime); }
        }

        private static TypeDescriptor ReadTypeDescriptor(RacBinaryReader r)
        {
            byte tag = r.ReadU8();
            switch (tag)
            {
                case TdTag_TypeParam:
                {
                    string n = r.ReadString() ?? "";
                    return TypeDescriptor.TypeParameter(n);
                }
                case TdTag_Union:
                {
                    int n = r.ReadI32();
                    var members = new List<TypeDescriptor>(n);
                    for (int i = 0; i < n; i++) members.Add(ReadTypeDescriptor(r));
                    return TypeDescriptor.Union(members);
                }
                case TdTag_Function:
                {
                    int n = r.ReadI32();
                    var ps = new List<TypeDescriptor>(n);
                    for (int i = 0; i < n; i++) ps.Add(ReadTypeDescriptor(r));
                    TypeDescriptor? ret = null;
                    if (r.ReadU8() != 0) ret = ReadTypeDescriptor(r);
                    return TypeDescriptor.FunctionType(ps, ret);
                }
                case TdTag_Plain:
                {
                    string name = r.ReadString() ?? "";
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
                    string? lt = null;
                    if (r.ReadU8() != 0) lt = r.ReadString();
                    return new TypeDescriptor(name, generics, isRef, refElem, isMut, lt);
                }
                default:
                    throw new InvalidDataException($"rac: unknown TypeDescriptor tag 0x{tag:X2}");
            }
        }

        // --- ModuleSpecifier ---------------------------------------------------
        private static void WriteModuleSpecifier(RacBinaryWriter w, ModuleSpecifier ms)
        {
            w.WriteI32((int)ms.Kind);
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

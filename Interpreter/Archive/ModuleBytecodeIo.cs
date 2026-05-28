using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaLanguage.Errors;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using IrEncoding = RaLanguage.Interpreter.IR.Encoding;

namespace RaLanguage.Interpreter.Archive
{
    // v1.1 ModuleBytecode payload — direct serialised RaFunction tree.
    //
    // Wire layout (all little-endian):
    //
    //   "RAFB" u32 magic
    //   formatVersion: u16  = 1
    //   reserved:      u16
    //
    //   Module root RaFunction:
    //     SerializeRaFunction(root)
    //
    // RaFunction wire form (see SerializeRaFunction):
    //   Name              : string
    //   FrameId           : i32
    //   LocalCount        : i32
    //   Arity             : i32
    //   ParamFlags        : u8
    //   SlotCount         : i32
    //   UsesUnboxedSlots  : bool (u8)
    //   HasImports        : bool (u8)
    //
    //   Code              : i32 length + u32 * length
    //   Consts            : i32 length + per-entry tagged value
    //   Names             : i32 length + string * length
    //   EhTable           : i32 length + ExceptionHandler * length
    //   Upvalues          : i32 length + UpvalueSpec * length
    //   SlotNames         : i32 length + string? * length
    //   PcSpans           : u8 hasPc + (i32 count + i32*count + SourceSpan*count)?
    //   DeclSlotByAstRef  : i32 length + i32 * length
    //   MutatedNames      : u8 hasSet + (i32 count + string*count)?
    //
    //   Eight typed AST-ref pools (CastRefs, MemberAccessRefs, ...):
    //     i32 length + SerializeAstNode * length
    //   AstRefs pool       : i32 length + SerializeAstNode (polymorphic) * length
    //   DefineRefs pool    : i32 length + SerializeAstNode (polymorphic) * length
    //   FuncDefRefs pool   : i32 length + FunctionDefinitionNode * length
    //
    // Per-PC inline caches (LoadGlobalIc, EnumAccessIc, CastIc, MemberAccessIc,
    // CallMethodIc) are NOT serialised — they re-prime on the first execution
    // after load, exactly as a freshly-compiled function does. Analysis bundle
    // is NOT serialised either; rebuilt lazily on load by IrAnalysisBundle.Build
    // when a consumer asks for it.
    //
    // Any unsupported AST node kind / RuntimeValue subtype encountered during
    // serialisation raises ModuleBytecodeUnsupportedException — the packager
    // catches it and falls back to source-only mode for that module. Older
    // v1.0 archives keep loading unchanged (no ModuleBytecode section).
    public static class ModuleBytecodeIo
    {
        // "RAFB" — Ra Archive Function Bytecode.
        public const uint MagicHead = (uint)'R' | ((uint)'A' << 8) | ((uint)'F' << 16) | ((uint)'B' << 24);
        // v1: inline-only const pool (legacy).
        // v2: const pool tags may reference an archive-level SharedConstPool
        //     (kind 0x07). Hybrid encoding — singleton refs stay inline.
        // v3: direct-bytecode-payload-only loader. Every AstNodeType
        //     handled by AstNodeSerializer.
        // v4: pre-compiled nested function bodies. Each
        //     FunctionDefinitionNode (and the matching Struct /
        //     Trait / Operator method nodes) carries its compiled
        //     RaFunction inline, so the runtime's
        //     FunctionDefinitionHelper.GetOrCompileBody short-circuits
        //     on the first execution — no AST→IR work at runtime.
        //     v3 payloads keep loading unchanged; their callees pay
        //     the lazy compile once on first invocation, exactly as
        //     before.
        public const ushort PayloadVersion = 4;
        public const ushort PayloadVersion_V1 = 1;
        public const ushort PayloadVersion_V2 = 2;
        public const ushort PayloadVersion_V3 = 3;
        public const ushort PayloadVersion_V4 = 4;

        public static byte[] Serialize(RaFunction root, SharedConstPoolBuilder? sharedPool = null)
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(MagicHead);
            // v4 (#pre-compiled children) is now the only wire form
            // this writer emits. The shared pool stays optional —
            // when present, const tags reference it; otherwise every
            // const inlines. Older wire versions (v1 / v2 / v3) are
            // still ACCEPTED by Deserialize for backward read.
            bool emitPool = sharedPool != null && sharedPool.Finalised && sharedPool.Pooled > 0;
            ushort ver = PayloadVersion_V4;
            w.WriteU16(ver);
            w.WriteU16(0);
            // Stash the writer pool + version in thread-local state so
            // AstNodeSerializer.WriteFunctionDefinition can call back
            // into WriteInlineRaFunction with the right context.
            AstNodeSerializer.WriterPool = emitPool ? sharedPool : null;
            AstNodeSerializer.WriterVersion = ver;
            try
            {
                SerializeRaFunction(w, root, emitPool ? sharedPool : null);
            }
            finally
            {
                AstNodeSerializer.WriterPool = null;
                AstNodeSerializer.WriterVersion = 0;
            }
            return ms.ToArray();
        }

        public static RaFunction Deserialize(ReadOnlySpan<byte> payload, SharedConstPool? sharedPool = null)
        {
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            uint magic = r.ReadU32();
            if (magic != MagicHead)
                throw new InvalidDataException("rac: ModuleBytecode magic mismatch");
            ushort ver = r.ReadU16();
            if (ver != PayloadVersion_V1 && ver != PayloadVersion_V2
                && ver != PayloadVersion_V3 && ver != PayloadVersion_V4)
                throw new InvalidDataException($"rac: ModuleBytecode version {ver} not supported");
            ushort reserved = r.ReadU16();
            if (reserved != 0)
                throw new InvalidDataException("rac: ModuleBytecode reserved must be zero");
            AstNodeSerializer.ReaderPool = ver != PayloadVersion_V1 ? sharedPool : null;
            AstNodeSerializer.ReaderVersion = ver;
            try
            {
                return DeserializeRaFunction(r, ver != PayloadVersion_V1 ? sharedPool : null);
            }
            finally
            {
                AstNodeSerializer.ReaderPool = null;
                AstNodeSerializer.ReaderVersion = 0;
            }
        }

        // === v4 nested-function helpers ===

        // Inline RaFunction = SerializeRaFunction body without the
        // RAFB magic + version preamble. Used by AstNodeSerializer
        // when serialising a FunctionDefinitionNode's pre-compiled
        // body. The thread-local pool comes from
        // AstNodeSerializer.WriterPool / ReaderPool set up by the
        // top-level Serialize / Deserialize wrapper.
        internal static void WriteInlineRaFunction(RacBinaryWriter w, RaFunction fn,
            SharedConstPoolBuilder? sharedPool)
        {
            SerializeRaFunction(w, fn, sharedPool);
        }

        internal static RaFunction ReadInlineRaFunction(RacBinaryReader r,
            SharedConstPool? sharedPool)
        {
            return DeserializeRaFunction(r, sharedPool);
        }

        private static void SerializeRaFunction(RacBinaryWriter w, RaFunction fn, SharedConstPoolBuilder? sharedPool)
        {
            w.WriteString(fn.Name);
            w.WriteI32(fn.FrameId);
            w.WriteI32(fn.LocalCount);
            w.WriteI32(fn.Arity);
            w.WriteU8(fn.ParamFlags);
            w.WriteI32(fn.SlotCount);
            w.WriteU8(fn.UsesUnboxedSlots ? (byte)1 : (byte)0);
            w.WriteU8(fn.HasImports ? (byte)1 : (byte)0);

            // Code
            w.WriteI32(fn.Code.Length);
            for (int i = 0; i < fn.Code.Length; i++) w.WriteU32(fn.Code[i]);

            // Consts pool — each entry tagged, optionally pool-referenced.
            w.WriteI32(fn.Consts.Length);
            for (int i = 0; i < fn.Consts.Length; i++) SerializeConst(w, fn.Consts[i], sharedPool);

            // Names pool
            w.WriteI32(fn.Names.Length);
            for (int i = 0; i < fn.Names.Length; i++) w.WriteString(fn.Names[i]);

            // EH table
            w.WriteI32(fn.EhTable.Length);
            for (int i = 0; i < fn.EhTable.Length; i++)
            {
                var eh = fn.EhTable[i];
                w.WriteI32(eh.StartPc);
                w.WriteI32(eh.EndPc);
                w.WriteI32(eh.CatchPc);
                w.WriteI32(eh.FinallyPc);
                w.WriteU8(eh.CatchSlot);
                w.WriteI32(eh.ScopeDepth);
            }

            // Upvalues
            w.WriteI32(fn.Upvalues.Length);
            for (int i = 0; i < fn.Upvalues.Length; i++)
            {
                var uv = fn.Upvalues[i];
                w.WriteU8(uv.IsLocal ? (byte)1 : (byte)0);
                w.WriteU16(uv.Index);
            }

            // SlotNames
            w.WriteI32(fn.SlotNames.Length);
            for (int i = 0; i < fn.SlotNames.Length; i++) w.WriteString(fn.SlotNames[i]);

            // PcSpans
            bool hasPc = fn.PcSpansPc != null && fn.PcSpansSpan != null;
            w.WriteU8(hasPc ? (byte)1 : (byte)0);
            if (hasPc)
            {
                int n = fn.PcSpansPc!.Length;
                w.WriteI32(n);
                for (int i = 0; i < n; i++) w.WriteI32(fn.PcSpansPc[i]);
                for (int i = 0; i < n; i++) SerializeSourceSpan(w, fn.PcSpansSpan![i]);
            }

            // DeclSlotByAstRef
            w.WriteI32(fn.DeclSlotByAstRef.Length);
            for (int i = 0; i < fn.DeclSlotByAstRef.Length; i++) w.WriteI32(fn.DeclSlotByAstRef[i]);

            // MutatedNames
            if (fn.MutatedNames == null)
            {
                w.WriteU8(0);
            }
            else
            {
                w.WriteU8(1);
                w.WriteI32(fn.MutatedNames.Count);
                foreach (var n in fn.MutatedNames) w.WriteString(n);
            }

            // AST-ref pools. AstRefs / DefineRefs are polymorphic; the rest
            // are typed, but every entry still routes through the
            // polymorphic dispatcher for uniformity. The dispatcher writes
            // a leading u8 tag that lets the reader rebuild the concrete
            // subclass.
            SerializeNodeArray(w, fn.AstRefs);
            SerializeNodeArray<Parser.Nodes.Operations.CastNode>(w, fn.CastRefs);
            SerializeNodeArray<Parser.Nodes.Structs.MemberAccessNode>(w, fn.MemberAccessRefs);
            SerializeNodeArray<Parser.Nodes.Structs.MemberAssignmentNode>(w, fn.MemberAssignRefs);
            SerializeNodeArray<Parser.Nodes.Variables.ListAssignmentNode>(w, fn.ListAssignRefs);
            SerializeNodeArray<Parser.Nodes.Enums.EnumAccessNode>(w, fn.EnumAccessRefs);
            SerializeNodeArray<Parser.Nodes.Special.TypeofNode>(w, fn.TypeofRefs);
            SerializeNodeArray<Parser.Nodes.Special.NameofNode>(w, fn.NameofRefs);
            SerializeNodeArray<Parser.Nodes.Operations.DereferenceNode>(w, fn.DerefRefs);
            SerializeNodeArray<Parser.Nodes.Classes.SuperNode>(w, fn.SuperRefs);
            SerializeNodeArray<Parser.Nodes.Functions.FunctionDefinitionNode>(w, fn.FuncDefRefs);
            SerializeNodeArray(w, fn.DefineRefs);
        }

        private static RaFunction DeserializeRaFunction(RacBinaryReader r, SharedConstPool? sharedPool)
        {
            string name = r.ReadString() ?? "";
            var fn = new RaFunction(name);
            fn.FrameId = r.ReadI32();
            fn.LocalCount = r.ReadI32();
            fn.Arity = r.ReadI32();
            fn.ParamFlags = r.ReadU8();
            fn.SlotCount = r.ReadI32();
            fn.UsesUnboxedSlots = r.ReadU8() != 0;
            fn.HasImports = r.ReadU8() != 0;

            int codeLen = r.ReadI32();
            VerifyArrayLen(codeLen);
            fn.Code = new uint[codeLen];
            for (int i = 0; i < codeLen; i++) fn.Code[i] = r.ReadU32();

            int constLen = r.ReadI32();
            VerifyArrayLen(constLen);
            fn.Consts = new RuntimeValue?[constLen];
            for (int i = 0; i < constLen; i++) fn.Consts[i] = DeserializeConst(r, sharedPool);

            int nameLen = r.ReadI32();
            VerifyArrayLen(nameLen);
            fn.Names = new string[nameLen];
            for (int i = 0; i < nameLen; i++) fn.Names[i] = r.ReadString() ?? "";

            int ehLen = r.ReadI32();
            VerifyArrayLen(ehLen);
            fn.EhTable = new ExceptionHandler[ehLen];
            for (int i = 0; i < ehLen; i++)
            {
                int s = r.ReadI32();
                int e = r.ReadI32();
                int cpc = r.ReadI32();
                int fpc = r.ReadI32();
                byte cslot = r.ReadU8();
                int depth = r.ReadI32();
                fn.EhTable[i] = new ExceptionHandler(s, e, cpc, fpc, cslot, depth);
            }

            int upvLen = r.ReadI32();
            VerifyArrayLen(upvLen);
            fn.Upvalues = new UpvalueSpec[upvLen];
            for (int i = 0; i < upvLen; i++)
            {
                bool isLocal = r.ReadU8() != 0;
                ushort idx = r.ReadU16();
                fn.Upvalues[i] = new UpvalueSpec(isLocal, idx);
            }

            int snLen = r.ReadI32();
            VerifyArrayLen(snLen);
            fn.SlotNames = new string?[snLen];
            for (int i = 0; i < snLen; i++) fn.SlotNames[i] = r.ReadString();
            // Re-derive NameToSlot — the lookup that the runtime needs
            // when AssignBinding refreshes a slot binding. Mirrors
            // FinalizeFn in IrCompiler.
            if (snLen > 0)
            {
                fn.NameToSlot = new Dictionary<string, int>(snLen);
                for (int i = 0; i < snLen; i++)
                {
                    var slotName = fn.SlotNames[i];
                    if (!string.IsNullOrEmpty(slotName)) fn.NameToSlot[slotName!] = i;
                }
            }

            bool hasPc = r.ReadU8() != 0;
            if (hasPc)
            {
                int n = r.ReadI32();
                VerifyArrayLen(n);
                fn.PcSpansPc = new int[n];
                for (int i = 0; i < n; i++) fn.PcSpansPc[i] = r.ReadI32();
                fn.PcSpansSpan = new SourceSpan[n];
                for (int i = 0; i < n; i++) fn.PcSpansSpan[i] = DeserializeSourceSpan(r);
            }

            int declLen = r.ReadI32();
            VerifyArrayLen(declLen);
            fn.DeclSlotByAstRef = new int[declLen];
            for (int i = 0; i < declLen; i++) fn.DeclSlotByAstRef[i] = r.ReadI32();

            bool hasMutated = r.ReadU8() != 0;
            if (hasMutated)
            {
                int mn = r.ReadI32();
                VerifyArrayLen(mn);
                fn.MutatedNames = new HashSet<string>(mn);
                for (int i = 0; i < mn; i++)
                {
                    var s = r.ReadString();
                    if (s != null) fn.MutatedNames.Add(s);
                }
            }

            fn.AstRefs = DeserializeNodeArray<Parser.Nodes.AstNode>(r);
            fn.CastRefs = DeserializeNodeArray<Parser.Nodes.Operations.CastNode>(r);
            fn.MemberAccessRefs = DeserializeNodeArray<Parser.Nodes.Structs.MemberAccessNode>(r);
            fn.MemberAssignRefs = DeserializeNodeArray<Parser.Nodes.Structs.MemberAssignmentNode>(r);
            fn.ListAssignRefs = DeserializeNodeArray<Parser.Nodes.Variables.ListAssignmentNode>(r);
            fn.EnumAccessRefs = DeserializeNodeArray<Parser.Nodes.Enums.EnumAccessNode>(r);
            fn.TypeofRefs = DeserializeNodeArray<Parser.Nodes.Special.TypeofNode>(r);
            fn.NameofRefs = DeserializeNodeArray<Parser.Nodes.Special.NameofNode>(r);
            fn.DerefRefs = DeserializeNodeArray<Parser.Nodes.Operations.DereferenceNode>(r);
            fn.SuperRefs = DeserializeNodeArray<Parser.Nodes.Classes.SuperNode>(r);
            fn.FuncDefRefs = DeserializeNodeArray<Parser.Nodes.Functions.FunctionDefinitionNode>(r);
            fn.DefineRefs = DeserializeNodeArray<Parser.Nodes.AstNode>(r);

            // IC tables. Sized to Code.Length so the dispatch hot-path
            // indexes without bounds checks. Slots are zero-initialised
            // and re-prime on the first execution at that PC.
            if (fn.Code.Length > 0)
            {
                fn.LoadGlobalIc = new LoadGlobalIcSlot[fn.Code.Length];
                bool needEnum = false, needCast = false, needMem = false, needCall = false;
                for (int ip = 0; ip < fn.Code.Length; ip++)
                {
                    var op = IrEncoding.DecodeOp(fn.Code[ip]);
                    switch (op)
                    {
                        case Opcode.EnumAccess: needEnum = true; break;
                        case Opcode.Cast: needCast = true; break;
                        case Opcode.GetMember: needMem = true; break;
                        case Opcode.Call:
                        case Opcode.TailCall: needCall = true; break;
                    }
                }
                if (needEnum) fn.EnumAccessIc = new EnumAccessIcSlot[fn.Code.Length];
                if (needCast) fn.CastIc = new CastIcSlot[fn.Code.Length];
                if (needMem) fn.MemberAccessIc = new MemberAccessIcSlot[fn.Code.Length];
                if (needCall) fn.CallMethodIc = new CallMethodIcSlot[fn.Code.Length];
            }

            return fn;
        }

        // --- Const pool ------------------------------------------------------
        //
        // Inline tags cover every RuntimeValue subtype that the IR
        // compiler may emit into a function's `Consts[]`. Pool-ref tags
        // (v2+) carry a u32 index into the archive-level
        // SharedConstPool — currently only the values the
        // SharedConstPoolBuilder dedups (String, Number, Integer, Long,
        // Double, Float) participate in pooling. The remaining
        // primitive subtypes always inline.
        private const byte ConstTag_Null      = 0x00;
        private const byte ConstTag_Number    = 0x01;
        private const byte ConstTag_String    = 0x02;
        private const byte ConstTag_Bool      = 0x03;
        private const byte ConstTag_Integer   = 0x04;
        private const byte ConstTag_Long      = 0x05;
        private const byte ConstTag_Double    = 0x06;
        private const byte ConstTag_Float     = 0x07;
        // v1.2 (#1 extension): full primitive coverage. All inline.
        private const byte ConstTag_Byte      = 0x08;
        private const byte ConstTag_Short     = 0x09;
        private const byte ConstTag_UShort    = 0x0A;
        private const byte ConstTag_UInt      = 0x0B;
        private const byte ConstTag_ULong     = 0x0C;
        private const byte ConstTag_Int128    = 0x0D;
        private const byte ConstTag_UInt128   = 0x0E;
        private const byte ConstTag_Decimal   = 0x0F;
        // v1.1 (#7) — pool refs. u32 index follows the tag.
        private const byte ConstTag_PoolString  = 0x10;
        private const byte ConstTag_PoolNumber  = 0x11;
        private const byte ConstTag_PoolInteger = 0x12;
        private const byte ConstTag_PoolLong    = 0x13;
        private const byte ConstTag_PoolDouble  = 0x14;
        private const byte ConstTag_PoolFloat   = 0x15;
        // v1.2 (#full primitive pool coverage). Only wider types
        // pool — Byte / Short / UShort / UInt always inline (their
        // pool refs would be larger than the inline payload).
        private const byte ConstTag_PoolULong   = 0x16;
        private const byte ConstTag_PoolInt128  = 0x17;
        private const byte ConstTag_PoolUInt128 = 0x18;
        private const byte ConstTag_PoolDecimal = 0x19;

        private static void SerializeConst(RacBinaryWriter w, RuntimeValue? v, SharedConstPoolBuilder? pool)
        {
            if (v == null) { w.WriteU8(ConstTag_Null); return; }
            switch (v)
            {
                case NumberValue n:
                {
                    int idx = pool?.ResolveNumber(n.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolNumber);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Number);
                        WriteBigInteger(w, n.Value.Unscaled);
                        WriteBigInteger(w, n.Value.Scale);
                    }
                    return;
                }
                case StringValue s:
                {
                    int idx = pool?.ResolveString(s.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolString);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_String);
                        w.WriteString(s.Value);
                    }
                    return;
                }
                case BooleanValue b:
                    // Bool stays inline: 1-byte payload vs 5-byte pool
                    // ref makes pooling a net loss.
                    w.WriteU8(ConstTag_Bool);
                    w.WriteU8(b.Value ? (byte)1 : (byte)0);
                    return;
                case IntegerValue iv:
                {
                    int idx = pool?.ResolveInteger(iv.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolInteger);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Integer);
                        w.WriteI32(iv.Value);
                    }
                    return;
                }
                case LongValue lv:
                {
                    int idx = pool?.ResolveLong(lv.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolLong);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Long);
                        w.WriteI64(lv.Value);
                    }
                    return;
                }
                case DoubleValue dv:
                {
                    int idx = pool?.ResolveDouble(dv.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolDouble);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Double);
                        w.WriteU64((ulong)BitConverter.DoubleToInt64Bits(dv.Value));
                    }
                    return;
                }
                case FloatValue fv:
                {
                    int idx = pool?.ResolveFloat(fv.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolFloat);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Float);
                        w.WriteU32((uint)BitConverter.SingleToInt32Bits(fv.Value));
                    }
                    return;
                }
                case NullValue:
                    w.WriteU8(ConstTag_Null);
                    return;
                case ByteValue byv:
                    w.WriteU8(ConstTag_Byte);
                    w.WriteU8(byv.Value);
                    return;
                case ShortValue shv:
                    w.WriteU8(ConstTag_Short);
                    // Two-byte little-endian, two's complement.
                    w.WriteU8((byte)(shv.Value & 0xFF));
                    w.WriteU8((byte)((shv.Value >> 8) & 0xFF));
                    return;
                case UnsignedShortValue ushv:
                    w.WriteU8(ConstTag_UShort);
                    w.WriteU8((byte)(ushv.Value & 0xFF));
                    w.WriteU8((byte)((ushv.Value >> 8) & 0xFF));
                    return;
                case UnsignedIntegerValue uiv:
                    w.WriteU8(ConstTag_UInt);
                    w.WriteU32(uiv.Value);
                    return;
                case UnsignedLongValue ulv:
                {
                    int idx = pool?.ResolveULong(ulv.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolULong);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_ULong);
                        w.WriteU64(ulv.Value);
                    }
                    return;
                }
                case Int128Value i128:
                {
                    int idx = pool?.ResolveInt128(i128.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolInt128);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Int128);
                        var bi = (System.Numerics.BigInteger)i128.Value;
                        WriteBigInteger(w, bi);
                    }
                    return;
                }
                case UnsignedInt128Value u128:
                {
                    int idx = pool?.ResolveUInt128(u128.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolUInt128);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_UInt128);
                        var bi = (System.Numerics.BigInteger)u128.Value;
                        WriteBigInteger(w, bi);
                    }
                    return;
                }
                case DecimalValue dec:
                {
                    int idx = pool?.ResolveDecimal(dec.Value) ?? -1;
                    if (idx >= 0)
                    {
                        w.WriteU8(ConstTag_PoolDecimal);
                        w.WriteU32((uint)idx);
                    }
                    else
                    {
                        w.WriteU8(ConstTag_Decimal);
                        int[] parts = decimal.GetBits(dec.Value);
                        w.WriteI32(parts[0]);
                        w.WriteI32(parts[1]);
                        w.WriteI32(parts[2]);
                        w.WriteI32(parts[3]);
                    }
                    return;
                }
                default:
                    throw new ModuleBytecodeUnsupportedException(
                        $"unsupported const RuntimeValue type {v.GetType().Name}");
            }
        }

        private static RuntimeValue? DeserializeConst(RacBinaryReader r, SharedConstPool? pool)
        {
            byte tag = r.ReadU8();
            switch (tag)
            {
                case ConstTag_Null:
                    return null;
                case ConstTag_Number:
                {
                    var u = ReadBigInteger(r);
                    var s = ReadBigInteger(r);
                    return NumberValue.OfBigNumber(new BigNumber(u, s));
                }
                case ConstTag_String:
                    return new StringValue(r.ReadString() ?? "");
                case ConstTag_Bool:
                    return BooleanValue.Of(r.ReadU8() != 0);
                case ConstTag_Integer:
                    return new IntegerValue(r.ReadI32());
                case ConstTag_Long:
                    return new LongValue(r.ReadI64());
                case ConstTag_Double:
                    return new DoubleValue(BitConverter.Int64BitsToDouble(unchecked((long)r.ReadU64())));
                case ConstTag_Float:
                    return new FloatValue(BitConverter.Int32BitsToSingle(unchecked((int)r.ReadU32())));
                case ConstTag_PoolString:
                    return new StringValue(LookupString(pool, r.ReadU32()));
                case ConstTag_PoolNumber:
                    return NumberValue.OfBigNumber(LookupNumber(pool, r.ReadU32()));
                case ConstTag_PoolInteger:
                    return new IntegerValue(LookupInteger(pool, r.ReadU32()));
                case ConstTag_PoolLong:
                    return new LongValue(LookupLong(pool, r.ReadU32()));
                case ConstTag_PoolDouble:
                    return new DoubleValue(LookupDouble(pool, r.ReadU32()));
                case ConstTag_PoolFloat:
                    return new FloatValue(LookupFloat(pool, r.ReadU32()));
                case ConstTag_Byte:
                    return new ByteValue(r.ReadU8());
                case ConstTag_Short:
                {
                    byte lo = r.ReadU8();
                    byte hi = r.ReadU8();
                    return new ShortValue(unchecked((short)((hi << 8) | lo)));
                }
                case ConstTag_UShort:
                {
                    byte lo = r.ReadU8();
                    byte hi = r.ReadU8();
                    return new UnsignedShortValue((ushort)((hi << 8) | lo));
                }
                case ConstTag_UInt:
                    return new UnsignedIntegerValue(r.ReadU32());
                case ConstTag_ULong:
                    return new UnsignedLongValue(r.ReadU64());
                case ConstTag_Int128:
                {
                    var bi = ReadBigInteger(r);
                    return new Int128Value((Int128)bi);
                }
                case ConstTag_UInt128:
                {
                    var bi = ReadBigInteger(r);
                    return new UnsignedInt128Value((UInt128)bi);
                }
                case ConstTag_Decimal:
                {
                    int p0 = r.ReadI32();
                    int p1 = r.ReadI32();
                    int p2 = r.ReadI32();
                    int p3 = r.ReadI32();
                    var dec = new decimal(new[] { p0, p1, p2, p3 });
                    return new DecimalValue(dec);
                }
                case ConstTag_PoolULong:
                    return new UnsignedLongValue(LookupULong(pool, r.ReadU32()));
                case ConstTag_PoolInt128:
                    return new Int128Value(LookupInt128(pool, r.ReadU32()));
                case ConstTag_PoolUInt128:
                    return new UnsignedInt128Value(LookupUInt128(pool, r.ReadU32()));
                case ConstTag_PoolDecimal:
                    return new DecimalValue(LookupDecimal(pool, r.ReadU32()));
                default:
                    throw new InvalidDataException($"rac: unknown const tag 0x{tag:X2}");
            }
        }

        private static string LookupString(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Strings.Count)
                throw new InvalidDataException($"rac: shared-pool string index {idx} out of range ({pool.Strings.Count})");
            return pool.Strings[i];
        }
        private static BigNumber LookupNumber(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Numbers.Count)
                throw new InvalidDataException($"rac: shared-pool number index {idx} out of range ({pool.Numbers.Count})");
            return pool.Numbers[i];
        }
        private static int LookupInteger(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Integers.Count)
                throw new InvalidDataException($"rac: shared-pool integer index {idx} out of range ({pool.Integers.Count})");
            return pool.Integers[i];
        }
        private static long LookupLong(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Longs.Count)
                throw new InvalidDataException($"rac: shared-pool long index {idx} out of range ({pool.Longs.Count})");
            return pool.Longs[i];
        }
        private static double LookupDouble(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Doubles.Count)
                throw new InvalidDataException($"rac: shared-pool double index {idx} out of range ({pool.Doubles.Count})");
            return pool.Doubles[i];
        }
        private static float LookupFloat(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Floats.Count)
                throw new InvalidDataException($"rac: shared-pool float index {idx} out of range ({pool.Floats.Count})");
            return pool.Floats[i];
        }
        private static ulong LookupULong(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.ULongs.Count)
                throw new InvalidDataException($"rac: shared-pool ulong index {idx} out of range ({pool.ULongs.Count})");
            return pool.ULongs[i];
        }
        private static Int128 LookupInt128(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Int128s.Count)
                throw new InvalidDataException($"rac: shared-pool int128 index {idx} out of range ({pool.Int128s.Count})");
            return pool.Int128s[i];
        }
        private static UInt128 LookupUInt128(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.UInt128s.Count)
                throw new InvalidDataException($"rac: shared-pool uint128 index {idx} out of range ({pool.UInt128s.Count})");
            return pool.UInt128s[i];
        }
        private static decimal LookupDecimal(SharedConstPool? pool, uint idx)
        {
            if (pool == null) throw new InvalidDataException("rac: const tag references SharedConstPool but archive has none");
            int i = (int)idx;
            if ((uint)i >= (uint)pool.Decimals.Count)
                throw new InvalidDataException($"rac: shared-pool decimal index {idx} out of range ({pool.Decimals.Count})");
            return pool.Decimals[i];
        }

        // --- Polymorphic AST node arrays ------------------------------------
        private static void SerializeNodeArray<T>(RacBinaryWriter w, T[] arr) where T : Parser.Nodes.AstNode
        {
            w.WriteI32(arr.Length);
            for (int i = 0; i < arr.Length; i++) AstNodeSerializer.WriteNode(w, arr[i]);
        }

        private static T[] DeserializeNodeArray<T>(RacBinaryReader r) where T : Parser.Nodes.AstNode
        {
            int n = r.ReadI32();
            VerifyArrayLen(n);
            var arr = new T[n];
            for (int i = 0; i < n; i++)
            {
                var node = AstNodeSerializer.ReadNode(r);
                if (node is T typed) arr[i] = typed;
                else if (node == null)
                    throw new InvalidDataException($"rac: null AST node in typed array {typeof(T).Name}");
                else
                    throw new InvalidDataException(
                        $"rac: AST node array {typeof(T).Name} got {node.GetType().Name} at index {i}");
            }
            return arr;
        }

        // --- Helpers --------------------------------------------------------
        internal static void WritePosition(RacBinaryWriter w, Position p)
        {
            w.WriteI32(p.Idx);
            w.WriteI32(p.Ln);
            w.WriteI32(p.Col);
            w.WriteString(p.Fn ?? "");
        }

        internal static Position ReadPosition(RacBinaryReader r)
        {
            int idx = r.ReadI32();
            int ln = r.ReadI32();
            int col = r.ReadI32();
            string fn = r.ReadString() ?? "";
            // Ftxt is intentionally empty after deserialisation — the
            // diagnostic renderer gates source-window rendering on
            // !string.IsNullOrEmpty(Ftxt), so the only loss is the
            // source-snippet caret rendering. File:line:col still prints.
            return new Position(idx, ln, col, fn, "");
        }

        private static void SerializeSourceSpan(RacBinaryWriter w, SourceSpan span)
        {
            WritePosition(w, span.Start);
            WritePosition(w, span.End);
        }

        private static SourceSpan DeserializeSourceSpan(RacBinaryReader r)
        {
            var s = ReadPosition(r);
            var e = ReadPosition(r);
            return new SourceSpan(s, e);
        }

        public static void WriteBigInteger(RacBinaryWriter w, BigInteger v)
        {
            byte[] bytes = v.ToByteArray();
            w.WriteI32(bytes.Length);
            w.WriteBytes(bytes);
        }

        public static BigInteger ReadBigInteger(RacBinaryReader r)
        {
            int len = r.ReadI32();
            if (len < 0 || len > 1_048_576)
                throw new InvalidDataException($"rac: bogus BigInteger length {len}");
            if (len == 0) return BigInteger.Zero;
            return new BigInteger(r.ReadBytes(len));
        }

        private static void VerifyArrayLen(int n)
        {
            if (n < 0 || n > 16_777_216)
                throw new InvalidDataException($"rac: bogus array length {n}");
        }
    }

    // Thrown when the serialiser meets an AST node / RuntimeValue subtype it
    // does not yet handle. The packager catches this and falls back to
    // source-only mode for the affected module (no ModuleBytecode section
    // emitted). The runner then sees BytecodeSectionIndex == -1 and resumes
    // the v1.0 source-driven load path.
    public sealed class ModuleBytecodeUnsupportedException : Exception
    {
        public ModuleBytecodeUnsupportedException(string msg) : base(msg) { }
    }
}

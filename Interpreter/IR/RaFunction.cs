using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.IR
{
    public struct LoadGlobalIcSlot
    {
        public SymbolTable? Table;
        public int Gen;
        public SymbolEntry? Entry;
    }

    // M27.3 per-PC inline cache for OP_ENUM_ACCESS. EnumAccessHelper.Apply on
    // `EnumType.Variant` performs: type-tag check, dictionary-keyed HasMember,
    // dictionary-keyed GetMember. The refIdx-bound EnumAccessNode pins the
    // member name string; once a given PC observes a particular EnumTypeValue
    // the resolved variant identity is stable for the lifetime of that
    // EnumTypeValue (variant tables are immutable post-construction). Cache
    // entry: (EnumTypeValue ref, resolved variant). Hit condition: identity
    // match on EnumType reference.
    public struct EnumAccessIcSlot
    {
        public Values.RuntimeValue? EnumType;
        public Values.RuntimeValue? Result;
        // M81 PIC overflow. Allocated lazily on second shape
        // observed. 2 extra entries → 3-shape PIC (primary + 2
        // overflow). LRU ring eviction at saturation, then fall
        // through to the uncached EnumAccessHelper path.
        public EnumAccessIcEntry[]? Pic;
    }

    // M81 single PIC overflow entry for EnumAccess.
    public struct EnumAccessIcEntry
    {
        public Values.RuntimeValue? EnumType;
        public Values.RuntimeValue? Result;
    }

    // M27.3 per-PC inline cache for OP_CAST. Caches the resolved target
    // RuntimeValueType + source-type fingerprint: when the same source type
    // hits twice at the same PC AND the target type is a simple primitive
    // tag, we can short-circuit the string-keyed targetType dispatch in
    // RuntimeValue.CastTo. `Primed` flags first-write vs subsequent reads.
    // `IsNoop` records "source already matches target" so cast becomes a
    // tagged Copy + SetPos. Other paths fall through to the virtual call.
    public struct CastIcSlot
    {
        public Values.RuntimeValueType SrcType;
        public bool IsNoop;
        public bool Primed;
        // M81 PIC overflow — same shape as primary minus Primed
        // (entries are by-construction primed when they appear).
        public CastIcEntry[]? Pic;
    }

    // M81 single PIC overflow entry for Cast. `Primed` distinguishes
    // empty entries from real `RuntimeValueType.Number == 0`
    // observations.
    public struct CastIcEntry
    {
        public Values.RuntimeValueType SrcType;
        public bool IsNoop;
        public bool Primed;
    }

    // M28.1 per-PC inline cache for OP_GET_MEMBER. MemberAccessHelper.Apply
    // dispatches on target.Type through a long if/else chain covering
    // EnumType, StructInstance, ClassInstance, ClassType, Namespace,
    // ModuleWrapper, Super, and the primitive-extension catch-all. The
    // observed type at any given source-position PC is almost always
    // stable across iterations — the chain repeats the same false comparisons
    // every visit.
    //
    // The slot caches (TargetType tag, Shape). Shape is the type-defining
    // object whose identity determines resolution: EnumTypeValue for
    // EnumType, ClassTypeValue for ClassInstance/ClassType/Super, the
    // StructDefinitionNode for StructInstance, NamespaceValue itself for
    // Namespace. On match we jump to a single branch keyed by BranchKind.
    //
    // For *stable* resolutions (EnumType variant, NamespaceMember,
    // ClassType static method/field, immutable module exports) the resolved
    // RuntimeValue is cached in CachedResult and returned directly — bypasses
    // every dictionary lookup. For *unstable* resolutions (per-instance
    // fields, method group wrappers that bind to the receiver) only the
    // dispatch branch is cached; resolution still calls the type-specific
    // accessor but skips the chain of type-tag checks. For ClassInstance
    // method group access we additionally cache the resolved
    // List<FunctionDefinitionNode> in CachedAux so the per-call inheritance
    // walk in ResolveInstanceMethods is amortised.
    public struct MemberAccessIcSlot
    {
        public Values.RuntimeValueType TargetType;
        public object? Shape;
        public byte BranchKind;
        public object? CachedAux;
        public Values.RuntimeValue? CachedResult;
        // M38: shape-indexed field offset on ClassInstance / StructInstance.
        // Populated alongside BranchKind == BR_CLASS_FIELD /
        // BR_STRUCT_FIELD. Negative when the target's runtime shape does
        // not expose the field via a stable slot (extension methods,
        // dynamic catch-all). The IC hit path reads
        // instance.FieldSlots[FieldIndex] directly — bypasses the
        // Dictionary<string, RuntimeValue>.TryGetValue lookup that the
        // unfused path takes.
        public int FieldIndex;
        // M42: PIC overflow. Allocated lazily when a second shape is
        // observed at this PC. Holds up to 2 extra cached resolutions
        // (in addition to the primary monomorphic fields above). On
        // primary-miss we scan Pic; on Pic-miss we evict the oldest
        // (LRU-style ring) and re-prime. Saturated 3-shape PIC stops
        // alloc and falls through to the uncached `Apply` dispatch
        // (megamorphic).
        public MemberAccessIcEntry[]? Pic;
    }

    // M42: single PIC entry. Mirrors the cacheable fields of
    // MemberAccessIcSlot without the Pic/overflow link.
    public struct MemberAccessIcEntry
    {
        public Values.RuntimeValueType TargetType;
        public object? Shape;
        public byte BranchKind;
        public object? CachedAux;
        public Values.RuntimeValue? CachedResult;
        public int FieldIndex;
    }

    // M28.2 per-PC inline cache shared with OP_CALL. The pattern
    // `obj.method(args)` lowers to GetMember (binds method group) + Call.
    // When the Call sees a BoundMethodGroupValue / BoundClassMethodGroupValue
    // / BoundStructMethodValue produced by a cached GetMember, the per-call
    // FunctionDefinitionNode resolution is also cacheable: same receiver
    // class definition + same arg arity → same chosen overload. Caches the
    // (receiver Definition, chosen FunctionDefinitionNode, IsStatic flag)
    // triple. Hit skips FunctionCallExecutor's group-resolution scan.
    public struct CallMethodIcSlot
    {
        public object? ReceiverShape;
        public int ArgCount;
        public object? ChosenMethod;
        public bool IsStatic;
        public bool Primed;
        // M81 PIC overflow — Pic[] holds 2 extra (shape, argCount,
        // method) triples. On primary miss, scan Pic; on PIC miss,
        // evict oldest entry (ring index advances) and prime.
        public CallMethodIcEntry[]? Pic;
    }

    // M81 single PIC overflow entry for CallMethod.
    public struct CallMethodIcEntry
    {
        public object? ReceiverShape;
        public int ArgCount;
        public object? ChosenMethod;
        public bool IsStatic;
    }

    // A compiled Ra function (or the top-level script body). Built once by
    // IrCompiler from a FunctionDefinitionNode (or a script root) and then
    // dispatched repeatedly by VmExecutor. See RA_VM_MIGRATION.md §3.5.
    //
    // Identity guarantee: a RaFunction's Code, Consts, Names, EhTable,
    // Upvalues, AstRefs and metadata are immutable after IrCompiler returns.
    // VmExecutor relies on this to avoid per-call locking.
    public sealed class RaFunction
    {
        public string Name;

        // Matches Resolver's FrameInfo.FrameId. Used by the VM only for
        // diagnostics — slot resolution is by direct array index, not by
        // BindingId at runtime.
        public int FrameId;

        // Upper bound from FrameInfo.NextSlot. VmFrame.Slots is allocated
        // to exactly this size on each invocation (M75 — the legacy
        // parallel `Locals[]` array is gone; `Slots[]` is the sole
        // per-frame value store, with tagged-union Tag/Bits/Ref payload).
        public int LocalCount;

        // Number of declared parameters. Slot indices [0..Arity) are the
        // parameter slots (slot 0 is `self` for methods, otherwise the first
        // positional parameter).
        public int Arity;

        // Reserved for future flags (variadic, has-default-args, is-async,
        // is-async-stream). M1 leaves this at zero.
        public byte ParamFlags;

        // Packed 32-bit instructions. See IR/Encoding.cs.
        public uint[] Code;

        // Constants referenced by OP_LOAD_CONST and friends. Strings, regex
        // literals, numeric literals that don't fit a 16-bit immediate, and
        // RaFunctions for closure construction all live here.
        public RuntimeValue?[] Consts;

        // Interned identifier names for OP_LOAD_GLOBAL / OP_GET_MEMBER, etc.
        public string[] Names;

        // Nested RaFunctions, materialised at OP_CLOSURE.
        public RaFunction[] Children;

        // Closure capture descriptors. The closure produced by OP_CLOSURE
        // allocates a RuntimeValue?[Upvalues.Length] and fills it from the
        // parent frame per each entry.
        public UpvalueSpec[] Upvalues;

        public ExceptionHandler[] EhTable;

        // AstNode references for OP_VISIT_AST. During M1 this is the entire
        // mechanism; later milestones shrink it as more nodes get native
        // lowerings.
        public AstNode[] AstRefs;

        // Cast-site references (M5+). One entry per OP_CAST emitted by the
        // IR compiler; the dispatch loop looks the CastNode up by u8 index
        // to find both `TargetType` (passed to `RuntimeValue.CastTo`) and
        // source positions (`SetPos`). Separate pool so its u8 indexing
        // doesn't crowd the u16 AstRefs pool. Cap: 256 cast sites per
        // function — anything beyond falls back to OP_VISIT_AST.
        public Parser.Nodes.Operations.CastNode[] CastRefs;

        // Member-access-site references (M7+). Same u8-indexed pool model
        // as CastRefs: one entry per OP_GET_MEMBER / OP_SET_MEMBER emission
        // so the dispatch loop has access to MemberTok.Value (member name),
        // PositionStart / PositionEnd (for error positions), and the
        // original AST node (for IsInsideSameType / private-field checks).
        public Parser.Nodes.Structs.MemberAccessNode[] MemberAccessRefs;
        public Parser.Nodes.Structs.MemberAssignmentNode[] MemberAssignRefs;
        public Parser.Nodes.Variables.ListAssignmentNode[] ListAssignRefs;
        public Parser.Nodes.Enums.EnumAccessNode[] EnumAccessRefs;

        // M9 per-site reference pools — u8 indexed, max 256 sites per
        // function. The opcodes load position / token data from these.
        public Parser.Nodes.Special.TypeofNode[] TypeofRefs;
        public Parser.Nodes.Special.NameofNode[] NameofRefs;
        public Parser.Nodes.Operations.DereferenceNode[] DerefRefs;
        public Parser.Nodes.Classes.SuperNode[] SuperRefs;
        public Parser.Nodes.Functions.FunctionDefinitionNode[] FuncDefRefs;
        public AstNode[] DefineRefs;

        // Source span per PC for traceback reconstruction. Compact form:
        // parallel arrays Pc[] / Span[]; binary search at error-time.
        public int[]? PcSpansPc;
        public SourceSpan[]? PcSpansSpan;

        // Slot-based local table (M14). The Resolver pass on the AST already
        // assigns each declaration a BindingId(FrameId, Offset). For this
        // function's frame, SlotCount = max Offset + 1 across every declaration
        // and reference, so VmFrame.SlotLocals can be allocated exactly. The
        // SlotNames parallel array is consulted only by error-reporting paths
        // (`'x' is not defined`, moved-value diagnostics) — the hot path
        // resolves via slot index alone. NameToSlot is the inverse map used by
        // SetLocalDirect / AssignBinding to refresh the slot when the iter
        // variable's SymbolEntry is replaced across outer-loop iterations.
        public int SlotCount;
        public string?[] SlotNames;
        public System.Collections.Generic.Dictionary<string, int>? NameToSlot;

        // M23.1: per-PC inline cache for OP_LOAD_GLOBAL. Indexed by the
        // PC of the opcode (Code position). Each slot caches
        // (SymbolTable ref, LocalGeneration snapshot, resolved SymbolEntry).
        // Hit condition: cached.Table == current ctx.SymbolTable && gen
        // unchanged. Mutation to parent tables doesn't invalidate because
        // SymbolEntry is mutated in place via TryAssign — the cached
        // pointer keeps seeing fresh values. Shadowing on the leaf bumps
        // LocalGeneration → cache miss → refresh.
        public LoadGlobalIcSlot[]? LoadGlobalIc;

        // M39: tier-up profiling counters. The dispatch loop increments
        // InvocationCount on every Execute entry, and the back-edge of
        // ForTest / JmpIfNot (the dominant loop-tail opcodes) bumps
        // LoopBackEdgeCount. Together they identify hot functions / hot
        // loops without requiring a sampling profiler. Once a function
        // crosses HotThreshold the future tier-up compiler (currently a
        // stub) is free to specialise it; until then the counters are
        // pure metadata the IR exposes for inspection via --dump-ir or
        // the `runtime.profile()` builtin.
        //
        // Reset to zero on every IrCompiler.CompileScript so menu-driven
        // re-runs of the same script start counting fresh. Lives on the
        // RaFunction itself (not per-frame) so recursive invocations
        // accumulate into the same counter.
        public int InvocationCount;
        public int LoopBackEdgeCount;
        public const int HotThreshold = 10_000;
        public bool IsHot => InvocationCount >= HotThreshold || LoopBackEdgeCount >= HotThreshold;

        // M40: per-slot type lattice. Forward-flow inference run once at
        // IR finalize over the linear opcode stream. Each entry holds the
        // RuntimeValueType the IR compiler proved a slot must hold at the
        // given PC, or RuntimeValueType.Null when ambiguous / unknown.
        // The lattice is single-cell-per-slot for now (joins on
        // re-assignment collapse to "unknown"); future SSA-based work
        // will refine to per-PC.
        //
        // No runtime cost — populated once, consumed by the future
        // tier-up compiler / type-specialised opcode emission. Exposed
        // via --dump-ir for offline inspection.
        public Values.RuntimeValueType[]? SlotTypeHints;

        // M66 tagged-union: set by IrRewriter when at least one
        // unboxed-int opcode (OP_*_II family) gets emitted into
        // `Code`. VmFrame allocates `LongLocals` / `LongValid` only
        // when this flag is true, so functions that never touched
        // the unboxed path pay zero per-frame overhead.
        public bool UsesUnboxedSlots;

        // M64: analysis bundle attached at IR finalize. Contains CFG /
        // Dominators / SSA / SCCP / GVN / LICM / DCE results consumed
        // by the in-place `IrRewriter` and by `--dump-cfg` diagnostics.
        // Optional / null when the script frame was empty.
        public Analysis.IrAnalysisBundle? Analysis;

        // M27.3: per-PC inline caches for OP_ENUM_ACCESS and OP_CAST.
        // Both allocated parallel to Code; slots stay zero-initialised until
        // the first execution at that PC primes them. See struct headers above
        // for hit semantics.
        public EnumAccessIcSlot[]? EnumAccessIc;
        public CastIcSlot[]? CastIc;

        // M28: per-PC inline caches for OP_GET_MEMBER and OP_CALL. See struct
        // headers above for the cache discipline. Allocated parallel to Code
        // so a (pc - 1) index is always in range without a bounds check.
        public MemberAccessIcSlot[]? MemberAccessIc;
        public CallMethodIcSlot[]? CallMethodIc;

        // Names assigned anywhere in this function body (direct
        // `VariableAssignment` writes OR `VariableDeclaration` shadow
        // declarations). Populated once at IR-compile time via AST walk;
        // consumed by LICM to decide whether a `LoadLocalS` of a given
        // name is safe to hoist out of a loop preheader. A name absent
        // from this set has a stable `SymbolEntry.Value` for the
        // duration of the function call.
        //
        // Set covers ALL names assigned anywhere — closures, nested
        // scopes, etc. Conservative for analysis purposes: any write
        // anywhere disqualifies the name from hoist consideration.
        public System.Collections.Generic.HashSet<string>? MutatedNames;

        // M88: the AST contains at least one `import` / `using` /
        // `namespace import` node. With cross-module references in
        // play, `MutatedNames` (which only walks the importing
        // function's own AST + nested function defs) can NOT see
        // mutations performed by callees that live in a different
        // module's frame. The LICM `LoadLocalS` hoist therefore has
        // to assume any in-loop `Call` / `CallMethod` / `TailCall`
        // could indirectly mutate any binding name when this flag is
        // true — closure capture across imported boundaries +
        // exported closures both fall into this hole.
        public bool HasImports;

        // M88: every named function reachable in this function's
        // compilation unit, mapped to its `MutatedNames` set if
        // known. Populated by `IrCompiler` from the AST's
        // `FunctionDefinition` walker. The LICM `LoadLocalS` hoist
        // uses this to resolve `Call` opcodes whose `fnSlot` was
        // loaded via `LoadGlobal` of a name in this map — if the
        // callee's `MutatedNames` does NOT contain the binding name,
        // the call is safe to hoist past. Unknown callees (cross-
        // module, dynamic dispatch, captured closures whose name
        // was not in scope) fall through to the conservative
        // `HasImports` gate.
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>? CalleeMutatedNames;

        // Parallel to AstRefs. For each AstRefs entry that originated from a
        // single-decl VariableDeclarationNode the IR lowered through
        // OP_DECLARE_LOCAL, DeclSlotByAstRef holds the frame slot to cache the
        // freshly-created SymbolEntry into. -1 means "no slot caching" (the
        // declaration ran but its name is not slot-eligible — multi-decl,
        // annotated, etc.).
        public int[] DeclSlotByAstRef;

        // M67 (refined by M77 to per-PC region-based eligibility):
        // bitmap `_memSsaEligible[pc]` records whether SymbolEntry
        // Memory-SSA tracking is safe at each PC. A `true` entry
        // means SE reads / writes at this PC participate in the
        // SSA def-use chain and SCCP lattice; `false` means SE
        // tracking is skipped (the IR fragment is treated as opaque
        // wrt the Memory-SSA layer).
        //
        // Eligibility is set to `false` at:
        //   * PCs inside any EH region (`[StartPc, EndPc)` of an
        //     ExceptionHandler, plus catch / finally bodies) — the
        //     CFG does not model exception edges, so SE phis at the
        //     catch handler entry are unreachable to SCCP and would
        //     produce stale lattice values.
        //   * PCs inside any loop body (PCs between a back-edge's
        //     target and source) — the SCCP lattice is per-SSA-
        //     version, not per-iter, so a pre-loop SE read would
        //     fold to its initial constant even when the body
        //     re-writes it.
        //   * PCs at or after the first aliasing opcode (`Call` /
        //     `CallKw` / `CallMethod` / `TailCall` / `Spawn` /
        //     `NewInstance` / `NativeDefine` / `Await` / `Throw`) —
        //     calls can mutate SE bindings via closure / global
        //     reach, invalidating earlier lattice values.
        //
        // M77 vs M67: M67 used a single `int _memSsaBarrierPc` cache —
        // function-global "off" when ANY EH or loop existed. Real
        // Ra programs always have try/catch and loops, so SE
        // tracking was effectively dead. M77's bitmap recovers
        // coverage in the straight-line prefix before any
        // try/loop/call, and in the (currently rare) post-loop
        // post-try regions that contain no further aliasing.
        //
        // Filled lazily by `SsaForm.BuildMemSsaEligibility(this)`.
        // `_memSsaEligible == null` after construction; the first
        // call materialises the bitmap (length = `Code.Length`).
        internal bool[]? _memSsaEligible;
        internal byte _memSsaBarrierCached; // 0 = unevaluated, 1 = filled.

        // M79: per-function VmFrame pool. Pre-sized frames are
        // reused across calls — `VmFrame.Rent` pops from this stack
        // when non-empty, sparing the GC of one VmFrame heap object
        // + Slots[] + SlotLocals[] allocations per call. `null` while
        // unused; lazily initialised by the first `VmFrame.Return`
        // call against this function (Interlocked-guarded so two
        // concurrent initialisers don't both win).
        //
        // Depth-capped at `VmFrame.PoolDepth` (currently 4) — beyond
        // that, returned frames drop on the floor and let the GC
        // reclaim. The cap matches RaTaskCore's pool design (M70)
        // and keeps memory pressure bounded under recursive hot
        // loops without becoming a leak vector.
        internal System.Collections.Concurrent.ConcurrentStack<Vm.VmFrame>? _framePool;

        public RaFunction(string name)
        {
            Name = name;
            FrameId = 0;
            LocalCount = 0;
            Arity = 0;
            ParamFlags = 0;
            Code = System.Array.Empty<uint>();
            Consts = System.Array.Empty<RuntimeValue?>();
            Names = System.Array.Empty<string>();
            Children = System.Array.Empty<RaFunction>();
            Upvalues = System.Array.Empty<UpvalueSpec>();
            EhTable = System.Array.Empty<ExceptionHandler>();
            AstRefs = System.Array.Empty<AstNode>();
            CastRefs = System.Array.Empty<Parser.Nodes.Operations.CastNode>();
            MemberAccessRefs = System.Array.Empty<Parser.Nodes.Structs.MemberAccessNode>();
            MemberAssignRefs = System.Array.Empty<Parser.Nodes.Structs.MemberAssignmentNode>();
            ListAssignRefs = System.Array.Empty<Parser.Nodes.Variables.ListAssignmentNode>();
            EnumAccessRefs = System.Array.Empty<Parser.Nodes.Enums.EnumAccessNode>();
            TypeofRefs = System.Array.Empty<Parser.Nodes.Special.TypeofNode>();
            NameofRefs = System.Array.Empty<Parser.Nodes.Special.NameofNode>();
            DerefRefs = System.Array.Empty<Parser.Nodes.Operations.DereferenceNode>();
            SuperRefs = System.Array.Empty<Parser.Nodes.Classes.SuperNode>();
            FuncDefRefs = System.Array.Empty<Parser.Nodes.Functions.FunctionDefinitionNode>();
            DefineRefs = System.Array.Empty<AstNode>();
            SlotCount = 0;
            SlotNames = System.Array.Empty<string?>();
            DeclSlotByAstRef = System.Array.Empty<int>();
        }
    }
}

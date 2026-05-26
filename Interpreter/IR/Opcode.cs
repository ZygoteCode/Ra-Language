namespace RaLanguage.Interpreter.IR
{
    // Bytecode opcode catalogue for the Ra VM. Locked alongside the
    // RA_VM_MIGRATION.md design doc (§3.4). New opcodes append at the end —
    // never re-number an existing entry, since RaFunction.Code is a packed
    // u32[] keyed by the low byte.
    //
    // Encoding (RA_VM_MIGRATION.md §3.3): each instruction is one u32.
    //   layout 1 (3-address):  [op:u8] [a:u8] [b:u8] [c:u8]
    //   layout 2 (16-bit imm): [op:u8] [a:u8] [imm16:u16]
    //   layout 3 (far jump):   [op:u8] [_:24] then extension u32 = pc_abs32
    public enum Opcode : byte
    {
        // --- constants / loads ---
        LoadConst       = 0x00,   // a, const16 (layout 2)
        LoadNull        = 0x01,   // a
        LoadTrue        = 0x02,   // a
        LoadFalse       = 0x03,   // a
        LoadIntS        = 0x04,   // a, imm16 (signed)  layout 2
        Move            = 0x05,   // a, b  (Aliased copy)

        // --- variables / bindings ---
        LoadGlobal      = 0x10,   // a, nameconst16
        StoreGlobal     = 0x11,   // a (src), nameconst16
        LoadBuiltin     = 0x12,   // a, builtinId16
        LoadUpval       = 0x13,   // a, upIdx16
        StoreUpval      = 0x14,   // a (src), upIdx16
        Declare         = 0x15,   // a (slot), kind:u8 (b), typeConst:u8 (c)
        Drop            = 0x16,   // a (slot)

        // --- memory model ---
        MoveLet         = 0x17,   // a, b  (sets IsMoved on src binding)
        Alias           = 0x18,   // a, b  (explicit Aliased())
        Borrow          = 0x19,   // a, b, mut:u8 (c)
        Deref           = 0x1A,   // a, b
        DerefStore      = 0x1B,   // a (ref), b (value)

        // `var/let/const/final x = src` for a single declaration. `a` is the
        // source slot holding the already-evaluated initializer; the imm16
        // points into AstRefs at the originating VariableDeclarationNode so
        // the helper can read DeclarationType / IsPublic / DeclaredType /
        // Annotations directly. The IR compiler only emits this when the
        // node has a single Declaration entry and HasAnnotations is false;
        // multi-declarations and annotated declarations are routed through
        // OP_NATIVE_DEFINE -> VariableDeclarationNodeVisitor.Apply.
        // See Interpreter/Runtime/DeclarationHelper.cs.
        DeclareLocal    = 0x1C,   // a (src), astRefIdx:u16

        // Scope management (M4). Mutate the dispatch loop's current Context
        // local. PushScope sets ctx = ctx.Copy() (allocates a fresh child
        // SymbolTable parented to the previous one). PopScope walks back via
        // Context.Parent. ClearScope wipes the current scope's local
        // bindings and decrements borrow counts (mirrors
        // SymbolTable.Clear() + ReleaseLocalBorrows used by the AST loop
        // visitors between iterations).
        PushScope       = 0x1D,
        PopScope        = 0x1E,
        ClearScope      = 0x1F,

        // ctx.SymbolTable.SetLocal(name, locals[src]). Bypasses parent
        // walk-up (vs Set, which would mutate any outer binding of the same
        // name). Used by For/ForEach to bind the iteration variable
        // directly in the loop scope. (0x2B is the free slot between BXor
        // and Neg; 0x20-0x2A are arithmetic.)
        SetLocalDirect  = 0x2B,   // a (src), nameIdx:u16

        // Updates an *existing* SymbolEntry's Value field by walking the
        // parent chain (SymbolTable.TryAssign). Used by For to mutate the
        // iteration variable each iteration without allocating a new
        // SymbolEntry. Errors if the binding has vanished (cannot happen in
        // single-threaded execution but defensive).
        AssignBinding   = 0x2F,   // a (src), nameIdx:u16

        // --- arithmetic ---
        Add             = 0x20,   // a, b, c
        Sub             = 0x21,
        Mul             = 0x22,
        Div             = 0x23,
        Mod             = 0x24,
        Pow             = 0x25,
        Shl             = 0x26,
        Shr             = 0x27,
        BAnd            = 0x28,
        BOr             = 0x29,
        BXor            = 0x2A,

        // --- unary ---
        Neg             = 0x2C,   // a, b
        Not             = 0x2D,
        BNot            = 0x2E,

        // M45 type-specialised numeric ops. Emitted by IR when
        // SlotTypeHints[] proves both source slots are RuntimeValueType.Number
        // at compile time. Skip the dynamic type-tag dispatch + null check
        // inside `Binary` and go straight to the int64 fast path. Fall
        // back to the boxed slow path on overflow / scale mismatch.
        AddNN           = 0xA8,   // a, b, c (Number + Number)
        SubNN           = 0xA9,
        MulNN           = 0xAA,

        // --- M73 Bool tagged-union opcodes ---
        //
        // Operate on the `ValueSlot.Bool` tag — payload `Bits & 1`
        // holds the boolean (0 = false, 1 = true). Counterparts to
        // II / FF arith for the eager-logic chain case.
        //
        //   AndBB [op][a:dst][b:lhs][c:rhs]   — bool AND (eager)
        //   OrBB  [op][a:dst][b:lhs][c:rhs]   — bool OR  (eager)
        //   NotB  [op][a:dst][b:src][0]       — bool NOT (unary)
        //
        // `AndJz` / `OrJnz` keep their short-circuit jump semantics
        // — those are the IR-emitter's `and` / `or` lowering and
        // skip evaluating the RHS when the LHS forces the result.
        // The BB family is for the rarer EAGER bool-arith pattern
        // and as a typed seam the future JIT can specialise.
        AndBB           = 0xAB,
        OrBB            = 0xAC,
        NotB            = 0xAD,

        // --- comparisons ---
        Eq              = 0x30,   // a, b, c
        Ne              = 0x31,
        SEq             = 0x32,
        SNe             = 0x33,
        Lt              = 0x34,
        Le              = 0x35,
        Gt              = 0x36,
        Ge              = 0x37,

        // --- short-circuit ---
        AndJz           = 0x38,   // a (cond), jmp_imm16
        OrJnz           = 0x39,   // a (cond), jmp_imm16

        // --- null / nullish ---
        NullCoal        = 0x3A,   // a, b, c
        NCJz            = 0x3B,   // a (val), jmp_imm16 (jumps if null)

        // --- strings ---
        StrConcat       = 0x40,   // a, b, c
        Interp          = 0x41,   // a, partsBase:u8 (b), partsCount:u8 (c)
        Fmt             = 0x42,   // a, b (expr), fmtConst:u8 (c)

        // --- containers (M6) ---
        // 3-address encoding for the new-collection opcodes: [op][dst][base][count].
        // `base..base+count-1` are the consecutively-laid-out element slots
        // produced by the IR compiler. Count is u8 (max 255 elements per
        // literal); larger literals fall back to OP_VISIT_AST.
        NewList         = 0x50,   // a (dst), b (base), c (count)
        // For maps the slot band is k0,v0,k1,v1,…; `c` is the *pair* count
        // (i.e. count*2 = total slot count).
        NewMap          = 0x51,   // a (dst), b (base), c (pairCount)
        NewSet          = 0x52,   // a (dst), b (base), c (count)
        NewTuple        = 0x53,   // a (dst), b (base), c (count)
        ListGet         = 0x54,   // a (dst), b (target), c (idx)  → target.ListAccess(idx)
        ListSet         = 0x55,   // a (target), b (idx), c (src)
        ListPush        = 0x56,   // a (list), b (src)
        MapGet          = 0x57,   // a (dst), b (map), c (key)
        MapSet          = 0x58,   // a (map), b (key), c (src)
        // 3-address: [op][dst][base][isInclusive]. base..base+2 = start, end, step.
        // Mirrors RangeNodeVisitor: materializes a ListValue eagerly.
        Range           = 0x59,   // a (dst), b (base), c (isInclusive flag)

        // --- member / index (M7) ---
        // [op][dst][srcSlot][refIdx:u8 → MemberAccessRefs].
        GetMember       = 0x60,
        // [op][ownerSlot][valSlot][refIdx:u8 → MemberAssignRefs].
        SetMember       = 0x61,
        GetIndex        = 0x62,   // (reserved)
        // [op][tgtSlot][idxSlot][refIdx:u8 → ListAssignRefs].
        // Caller emits value into slot (idxSlot+1) so the VM can locate it
        // without burning an extra encoding byte.
        SetIndex        = 0x63,
        // `EnumType.Variant`. [op][dst][srcSlot][refIdx:u8 → EnumAccessRefs].
        EnumAccess      = 0x64,

        // [op][dst][srcSlot][_]. Materialises an iterable ListValue from
        // any iterable collection (List/Tuple/Set/Map → list of items, where
        // Map yields TupleValue(key,value)). Used by ForEach to canonicalise
        // iteration regardless of source type.
        ForEachIterable = 0x65,
        // [op][dst][srcSlot][_]. Stores NumberValue(collection.Count) in dst.
        // Defined for List/Tuple/Set/Map; other types produce a runtime error.
        ListLen         = 0x66,

        // Typed-int-RHS self-additive slot. Layout: [op][selfSlot:u8][rhsLongSlot:u16].
        // RHS is read directly from `Slots[rhsLongSlot].Bits` as an int64 (typed
        // slot tag must be Int64; otherwise deopt to boxed AddIntoSlot path).
        // The slot's SymbolEntry holds the boxed accumulator; this op reads the
        // entry's value, adds the typed RHS via the int64 fast path, and stores
        // the new boxed value. Eliminates the per-iter `LoadLocalS` + boxed
        // mirror dispatch in `for i = 0 to N { sum = sum + i; }` shape, where
        // the body's `i` resolves to the lazy-long iter slot.
        AddIntoSlotI    = 0x67,
        SubIntoSlotI    = 0x68,

        // Slot-based local read. `a` = dst slot, imm16 = frame-slot index into
        // VmFrame.SlotLocals. The slot caches a SymbolEntry* populated at
        // declaration time, so the read bypasses ctx.SymbolTable.GetEntry
        // entirely. Falls back to OP_LOAD_GLOBAL when the Resolver could not
        // statically bind the access (BindingId.Unresolved).
        LoadLocalS      = 0x6A,
        // Slot-based local write. `a` = src slot (rhs value), imm16 = frame-slot
        // index. Only emitted for plain `=` assignments where Resolver has
        // tagged the target as Local/Global/Parameter inside the current frame;
        // compound forms (`+=`, `-=`, ...) still route through OP_STORE_GLOBAL
        // because AssignmentHelper.ApplyPrechecked needs the AST node.
        StoreLocalS     = 0x6B,

        // M66 tagged-union slot opcodes. Operate on the parallel
        // `VmFrame.LongLocals` int64 array. The slot indices
        // referenced here are the SAME slot ids the boxed opcodes
        // use — the tag bit `LongValid[slot]` selects which array
        // is canonical. Box / Unbox bridges convert between
        // representations on demand.
        //
        // Encoding layout:
        //   LoadIntS64   [op][a:slot][simm16]              — a = sign-extended imm
        //   UnboxI       [op][a:longSlot][b:boxedSlot][0]  — long = (NumberValue)boxed
        //   BoxI         [op][a:boxedSlot][b:longSlot][0]  — boxed = NumberValue(long)
        //   AddII / SubII / MulII / LtII / LeII / GtII / GeII / EqII / NeII
        //                [op][a:dst][b:lhs][c:rhs]         — long arith / cmp
        // Overflow on Add/Sub/Mul falls back to the boxed Binary path
        // via an inline branchless predicate (identical to M26.2).
        LoadIntS64       = 0xB8,
        UnboxI           = 0xB9,
        BoxI             = 0xBA,
        AddII            = 0xBB,
        SubII            = 0xBC,
        MulII            = 0xBD,
        LtII             = 0xBE,
        LeII             = 0xBF,
        // Continued range — placed at 0xB4-0xB7 (free slot just below
        // the existing II block) so the entire II family stays
        // contiguous from 0xB4 through 0xBF for a single range check.
        GtII             = 0xB4,
        GeII             = 0xB5,
        EqII             = 0xB6,
        NeII             = 0xB7,

        // M27.2 superinstructions: fused LoadLocalS + arithmetic + StoreLocalS
        // for `slot = slot ± <safe-rhs>`. `a` = rhs slot (already evaluated
        // value), imm16 = frame-slot index of the self-additive target. The
        // RHS sub-tree must be side-effect free (Number/Boolean/String literal,
        // VariableAccess, or unary neg over a literal) so the implicit read of
        // the target's prior value before mutating it preserves AST semantics.
        // Borrow / mutability / move semantics mirror StoreLocalS — including
        // IsMutable enforcement and the IsLet/!IsCopy → IsMoved transition.
        // Fast-paths NumberValue+NumberValue when both fit int64 (branchless
        // overflow check, same as Binary).
        AddIntoSlot     = 0x6C,
        SubIntoSlot     = 0x6D,

        // M27.5 inlined-immediate variants of AddIntoSlot/SubIntoSlot. Pattern:
        // `slot = slot ± <int16 literal>` where slot fits in a byte (frames
        // ≤ 256 slots, which is the common case). Layout: [op][slot:u8][simm16].
        // Skips both the LoadConst dispatch AND the const-pool entry that
        // AddIntoSlot needs for the RHS value. Borrow / mutability / move
        // semantics identical to AddIntoSlot.
        AddIntoSlotImm  = 0x6E,
        SubIntoSlotImm  = 0x6F,

        // --- control flow ---
        Jmp             = 0x70,   // _, jmp_imm16    (PC-relative; signed)
        JmpIf           = 0x71,   // a (cond), jmp_imm16
        JmpIfNot        = 0x72,   // a (cond), jmp_imm16
        JmpFar          = 0x73,   // _, _, _ ; extension word = u32 abs PC

        // --- functions ---
        Closure         = 0x80,   // a, funcConst16
        Call            = 0x81,   // a (dst), b (fn), c (argBase) — argCount in next u32 high byte; see Encoding
        CallKw          = 0x82,   // a, b (fn), payloadConst:u8 (c)
        // M28.3 fused Call + Ret. Layout [op][a:fnSlot][b:argBase][c:argCount].
        // The result of the inner Invoke becomes this frame's return value;
        // skips the separate OP_RET dispatch. Used by IR when an explicit
        // `return fn(args)` or an arrow-form auto-return body is a
        // FunctionCall node. True stack-trampolined TCO (no C# stack
        // growth across recursive tails) requires a thunk-return refactor
        // and is documented as deferred.
        TailCall        = 0x83,   // a (fn), b (argBase), c (argCount)
        Ret             = 0x84,   // a (src)
        RetNull         = 0x85,

        // --- methods / OOP ---
        CallMethod      = 0x86,   // a (dst), b (recv), nameconst:u8 (c)  + ext: argBase|argCount
        CallSuper       = 0x87,
        NewInstance     = 0x88,   // a (dst), classConst:u8 (b), c (argBase) + ext: argCount
        GetSelf         = 0x89,   // a
        // [op][dst][src][refIdx:u8 → TypeofRefs]. Produces a StringValue
        // with the canonical type name (matches TypeofNodeVisitor).
        Typeof          = 0x8A,
        // [op][dst][refIdx:u16 → NameofRefs]. Verifies the bound name then
        // returns it as a StringValue.
        Nameof          = 0x8B,
        // `a as TargetType`. `b` is the source slot, `c` is the index into
        // RaFunction.CastRefs (u8). Dispatch calls `src.CastTo(TargetType)`.
        // Result lands in locals[a].
        Cast            = 0x8C,   // a, b (src), castRefIdx:u8 (c)
        Is              = 0x8D,   // a, b, typeConst:u8 (c)

        // [op][dst][refIdx:u16 → SuperRefs]. Produces a SuperProxyValue
        // for the lexical class owner.
        GetSuper        = 0x8E,
        // [op][dst][refIdx:u16 → FuncDefRefs]. Constructs a FunctionValue
        // (or DLL-imported NativeFunctionValue) and registers it in the
        // current symbol table if the def has a name.
        DefineFunction  = 0x8F,

        // [op][dst][refIdx:u16 → DefineRefs]. Dispatches by node.NodeType
        // to the corresponding visitor's static Apply method. Covers
        // ExtensionDefinition / TraitDefinition / StructDefinition /
        // InterfaceDefinition / EnumDefinition / UsingNamespace.
        // Class/Annotation/Namespace/Import remain on OP_VISIT_AST pending
        // visitor refactors. Calling Apply directly skips
        // interpreter._visitors[] dispatch entirely — the AST visitor
        // array is never indexed.
        NativeDefine    = 0x90,

        // --- match / try-unwrap ---
        //
        // M86 — RESERVED BUT NOT EMITTED. Match expressions currently
        // route through `Opcode.NativeDefine` to
        // `Visitors.Patterns.MatchNodeVisitor.Apply`. That path works
        // correctly (the audit's own observation) but pays the
        // DefineRefs lookup + visitor dispatch per execution.
        //
        // Full IR lowering would replace the visitor path with an
        // opcode stream of the following shape:
        //
        //   MatchBegin scrutinee_slot
        //     ; scrutinee evaluated by preceding expression IR;
        //     ; this opcode just records the slot for the arm
        //     ; chain to consult.
        //   MatchArm armIdx
        //     ; pattern-test sub-stream emitted inline. Failure
        //     ; falls through to the NEXT MatchArm; success
        //     ; binds the pattern's bindings into the arm scope
        //     ; (fresh SymbolTable child pushed at MatchArm
        //     ; entry, popped on success-jump or fall-through).
        //   <pattern test opcodes>
        //     ; per-pattern primitives:
        //     ;   PatLitEq    cmp slot, literal_idx       (literal pattern)
        //     ;   PatVarBind  binding_name_idx            (variable bind)
        //     ;   PatVarTag   variant_tag_const_idx       (zero-arity enum variant)
        //     ;   PatVarPay   variant_tag, payload_dst    (payload-bearing variant)
        //     ;   PatTupleLen len_imm16                   (tuple shape check)
        //     ;   PatListLen  len_imm16, has_rest_flag    (list shape check)
        //     ;   PatStructShape shape_ref_idx            (struct type check)
        //     ;   PatFieldBind field_name_idx, dst_slot   (struct field extract)
        //     ;   PatWildcard (no-op success)
        //   <guard expression IR, if present>
        //     ; standard expression compilation; result in cmp_slot.
        //     ; JmpIfNot cmp_slot, next_arm
        //   <body IR>
        //     ; standard statement compilation. Falls through to
        //     ; MatchEnd via Jmp.
        //   MatchEnd
        //     ; pops the arm scope, joins all successful arm
        //     ; targets, finalises the match expression result
        //     ; in the destination slot. Also surfaces the
        //     ; "no arm matched" runtime error when reached
        //     ; via fall-through from the final arm.
        //
        // Why deferred (multi-week milestone):
        //
        //   1. 7 distinct pattern shapes (Wildcard / Variable /
        //      Literal / Variant / Tuple / List / Struct), each
        //      with its own dispatch + binding-extraction
        //      semantics. Variants and structs alone need
        //      shape-aware ICs to match the existing MemberAccess
        //      PIC infrastructure.
        //   2. Binding scope plumbing — bindings introduced by a
        //      pattern MUST NOT leak if the pattern eventually
        //      fails (e.g. nested tuple where the inner element
        //      doesn't match). The visitor today buffers
        //      bindings in a list and commits on full match;
        //      IR-level lowering needs a transactional scope
        //      pattern (push trial scope → pop on failure).
        //   3. Guards run AFTER bindings — so the guard's IR
        //      must compile against the trial scope, which the
        //      `MatchArm` lowering must establish before the
        //      guard's `JmpIfNot`.
        //   4. Body propagation — match bodies can `return` /
        //      `break` / `continue` / `yield`. The MatchEnd
        //      lowering must forward those out of the match
        //      expression's result slot, matching the visitor's
        //      `SuccessReturn` / `SuccessBreak` / `SuccessContinue`
        //      / `SuccessYield` semantics.
        //   5. Dispatch-loop await-point budget (proved by M85's
        //      revert): adding a new async dispatch case for
        //      Match would grow `Execute`'s async state machine
        //      and tip the depth-2000 recursion test over the
        //      C# stack budget. Any opcode-level Match lowering
        //      must either (a) compile pattern bodies to slot-
        //      level IR with no embedded await (already true
        //      for pure patterns + guards, but bodies can
        //      await), or (b) use a state-machine-free
        //      `IValueTaskSource` fast path for the sync case.
        //
        // Until those land, NativeDefine routing stays the
        // contract.
        MatchBegin      = 0x90,   // _, b (scrutinee)
        MatchArm        = 0x91,   // _, armIdx:u16  jumps on no-match
        MatchEnd        = 0x92,

        // --- exceptions ---
        Throw           = 0xA0,   // a (err src)
        EnterTry        = 0xA1,   // _, handlerIdx:u16
        LeaveTry        = 0xA2,   // _, handlerIdx:u16
        FinallyEnd      = 0xA3,

        // --- async ---
        Await           = 0xB0,   // a (dst), b (src)
        Spawn           = 0xB1,   // a (dst), b (fn), c (argBase)  + ext: argCount
        Emit            = 0xB2,   // a (src)
        ForAwait        = 0xB3,   // a (iter slot), b (stream), bodyJmp:u8 (c)  + ext: exit_pc

        // --- loops (specialized) ---
        ForInit         = 0xC0,   // a (iter), b (start), c (end)  + ext: step
        ForTest         = 0xC1,   // a (iter), jmp_imm16
        ForNext         = 0xC2,   // a (iter)
        ForEachInit     = 0xC3,   // a (iter), b (collection)
        ForEachNext     = 0xC4,   // a (iter), b (item), jmp_imm16

        // --- M72 Float64 tagged-union opcodes ---
        //
        // Counterparts to the M66 II family. Operate on the
        // `ValueSlot.Float64` tag — payload `Bits` stores the
        // double via `BitConverter.DoubleToInt64Bits`.
        //
        // Encoding mirrors the II family:
        //   UnboxF   [op][a:floatSlot][b:boxedSlot][0]  — float = (DoubleValue / FloatValue / NumberValue)boxed
        //   BoxF     [op][a:boxedSlot][b:floatSlot][0]  — boxed = DoubleValue(float)
        //   AddFF / SubFF / MulFF / DivFF /
        //   LtFF / LeFF / GtFF / GeFF
        //                [op][a:dst][b:lhs][c:rhs]      — double arith / cmp
        // Eq/Ne intentionally absent — the boxed `Binary` semantics
        // for `==` / `!=` cover every RuntimeValue subtype (string /
        // list / map / instance / null / ...) via the virtual
        // `GetComparisonEq` dispatch. The FF promotion would deopt
        // on every non-double operand pair.
        //
        // Div is included: float division by zero yields ±Infinity
        // / NaN (not an error), so DivFF has no fallback path.
        UnboxF           = 0xC5,
        BoxF             = 0xC6,
        AddFF            = 0xC7,
        SubFF            = 0xC8,
        MulFF            = 0xC9,
        DivFF            = 0xCA,
        LtFF             = 0xCB,
        LeFF             = 0xCC,
        GtFF             = 0xCD,
        GeFF             = 0xCE,

        // --- annotation / contract hooks ---
        RunPre          = 0xD0,   // _, handler:u16
        RunPost         = 0xD1,   // _, handler:u16, retSlot:u8

        // --- M68 pervasive II/FF: Div/Mod/Bitwise/Neg ---
        //
        // Extend the M66 II family to cover the remaining
        // numeric / bitwise binary ops + unary negate (int and
        // float). DivII / ModII deopt to the boxed `Binary` path
        // on division-by-zero / overflow so the original error
        // diagnostic survives unchanged. Bitwise ops (Shl / Shr /
        // BAnd / BOr / BXor) have no overflow / error edges and
        // always succeed when both operands fit int64.
        //
        // Encoding mirrors the M66 II family verbatim:
        //   DivII / ModII / ShlII / ShrII /
        //   BAndII / BOrII / BXorII
        //                [op][a:dst][b:lhs][c:rhs]
        //   NegI         [op][a:dst][b:src][0]
        //   NegF         [op][a:dst][b:src][0]
        DivII           = 0xD2,
        ModII           = 0xD3,
        ShlII           = 0xD4,
        ShrII           = 0xD5,
        BAndII          = 0xD6,
        BOrII           = 0xD7,
        BXorII          = 0xD8,
        NegI            = 0xD9,
        NegF            = 0xDA,

        // M80 — typed power. Operands carry the typed family (Int64 /
        // Float64) of `b` and `c`; result is written to slot `a` with
        // the matching tag.
        //
        //   PowII   [op][a:dst][b:base][c:exponent]
        //     Both operands are Int64-tagged. Computes b ^ c via
        //     repeated-squaring with branchless overflow check.
        //     Negative exponent / overflow → deopt to boxed Pow
        //     (BigNumber-precise result), preserving the existing
        //     error site at the call PC.
        //   PowFF   [op][a:dst][b:base][c:exponent]
        //     Both operands are Float64-tagged. Computes
        //     System.Math.Pow(b, c). IEEE-754 semantics — never throws.
        PowII           = 0xDB,
        PowFF           = 0xDC,

        // --- inline asm ---
        AsmInvoke       = 0xE0,   // a (retBase), b (argsBase), regionId:u8 (c)  + ext: argsCount|retCount

        // --- misc ---
        Pass            = 0xF0,
        Delete          = 0xF1,   // a (slot)

        // Stop dispatch and return RuntimeResult.Success(locals[a]). Used at
        // the very end of a script body; functions use Ret/RetNull instead.
        Halt            = 0xF9,   // a (result slot)

        // --- wide-operand prefix ---
        // When set, the *next* instruction's b/c are read as a single u16
        // instead of two u8s. Lets IR-gen address up to 65535 slots without
        // changing instruction width.
        Wide            = 0xFF,
    }
}

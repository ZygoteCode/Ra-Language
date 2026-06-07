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
        // L3: `&place` / `&mut place`. Lowered to [op][dst:a][nameIdx:imm16] —
        // the borrowed binding's name lives in Names[], resolved to its
        // SymbolEntry at dispatch via BorrowOps.TryBorrow (no AST-ref, no
        // sub-eval, so no new serialized side-table). Two opcodes keep the
        // whole 16-bit immediate a pure name index (shared vs mutable is the
        // opcode). Borrows carrying an explicit lifetime, or whose name index
        // would exceed 65535, fall back to OP_NATIVE_DEFINE.
        Borrow          = 0x19,   // a (dst), nameIdx:imm16  (shared `&`)
        BorrowMut       = 0x06,   // a (dst), nameIdx:imm16  (exclusive `&mut`)
        Deref           = 0x1A,   // a, b
        // L3: `*ref op= value`. [op][dst:a][refSlot:b][opTokenType:c]; the RHS
        // value lives in the contiguous slot b+1 (same trick as OP_SET_INDEX).
        // `c` is the assignment-operator TokenType (76 values, fits a byte);
        // the handler reads through the reference for compound ops, writes the
        // result back, and leaves it in `dst`. DerefStoreOps.Apply is shared
        // with the visitor fallback.
        DerefStore      = 0x1B,   // a (dst), b (refSlot; valSlot = b+1), opTok:c

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

        // L4: record copy-update `recv with { f: v, ... }`. Layout:
        //   [op][dst:a][base:b][defineRefIdx:c]
        // The receiver record sits at slot `base`; the N update values are laid
        // out contiguously at `base+1 .. base+N`. N (and the field names /
        // positions / declared types) come from the WithExpressionNode parked
        // in DefineRefs[c] (reusing the existing AST-ref pool — no new side
        // table, already serialized in .rac). The handler shallow-clones the
        // record and applies the validated overrides; the sub-expressions are
        // evaluated by ordinary opcodes (no AST re-walk of recv / values).
        With            = 0x43,   // a (dst), b (base: recv@base, values@base+1..), c (defineRefIdx)

        // L5: one-shot type definition from a FLAT descriptor (no AST). `a` =
        // scratch dst (the registered type value, mostly ignored), imm16 =
        // index into RaFunction.TypeDefs (polymorphic TypeDef pool). The
        // handler reconstructs + registers the runtime type from the descriptor
        // — so `.rac` stores definitions as plain data, not serialized AST.
        // Definitions whose data isn't fully flat-foldable fall back to
        // OP_NATIVE_DEFINE. Enum wired first; other kinds slot in behind it.
        DefineType      = 0x44,   // a (scratch dst), imm16 (TypeDefs index)

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
        ListPush        = 0x56,   // a (list), b (src) — append one value to list (spread-list incremental build)
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

        // 0x90 — RETIRED. Formerly OP_NATIVE_DEFINE (the runtime AST-exec
        // fallback: dispatch by node.NodeType to a visitor's static Apply for
        // any construct the IR could not lower). The whole corpus is now ND=0
        // (every frame is pure IR), so the opcode + its VM handler + the
        // compiler's fallback-routing have been deleted. 0x90 is a free opcode
        // value — DO NOT reuse it without auditing the --count-nd / --dump-ir /
        // RacBytecodeVerifier tripwires that still key off the literal 0x90.
        // The former 0x90 alias MatchBegin is retired with it.

        // --- match / try-unwrap ---
        //
        // RESERVED BUT NOT EMITTED. Match expressions lower to the L7
        // pattern-test opcode family (EnumTagEq / StructShape / TupleShape /
        // ListShape / … below), not to a MatchArm/MatchEnd stream. These two
        // values are kept reserved for a possible future structured-match
        // lowering; nothing emits or dispatches them today.
        MatchArm        = 0x91,   // _, armIdx:u16  jumps on no-match
        MatchEnd        = 0x92,

        // --- events ---
        //
        // Dedicated opcodes for event member-access and emit. v1 lands
        // them as semantic markers — IR-gen does NOT yet emit them
        // (Ra has no static event-type detection, so the `obj.E(args)`
        // shape is indistinguishable from any other call until the IC
        // primes). The VM dispatch routes through the same helpers
        // OP_GET_MEMBER / OP_CALL_METHOD use, plus an IC fast-path
        // tagged `BR_EVENT_INSTANCE` / `BR_EVENT_STATIC` in
        // MemberAccessHelper that skips the type-tag cascade once the
        // descriptor lookup is cached.
        //
        // Encoding parity with GetMember / CallMethod so a future PGO
        // pass can swap them in-place when the IC reports a stable
        // event hit.
        //
        //   GetEvent     [op][dst][recv][refIdx:u8 → MemberAccessRefs]
        //   EmitEvent    [op][dst][evSlot][argBase] + ext: argCount
        GetEvent        = 0x93,
        EmitEvent       = 0x94,

        // L7 (Match variant patterns). Enum-variant AND record-positional
        // introspection opcodes used to lower `case Ok(v)` / `case Some(x)` /
        // `case Point(x, y)` without the visitor. EnumTagEq/EnumPayload write
        // locals[A], read locals[B] (the scrutinee); C is an immediate (a Names
        // index / a payload index), not a slot. Both are POLYMORPHIC over the
        // scrutinee runtime type — EnumValue (by variant member name + payload
        // index) OR RecordInstanceValue (by nominal record type name + primary
        // field index) — mirroring the visitor's TryMatchVariant dispatch.
        //   EnumTagEq    [op][dst][scrut][nameIdx:c]  dst = scrut matches the
        //                  named variant/record (enum: MemberName==Names[c];
        //                  record: Definition is the RecordType bound to Names[c])
        //   EnumPayload  [op][dst][scrut][index:c]    dst = enum Payload[index]
        //                  OR record primary-field[index] value
        //   MatchArity   [op][--][scrut][subCount:c]  throws the visitor's exact
        //                  arity-mismatch error if the matched scrut's payload/
        //                  primary-field count != c; else nop. Writes nothing,
        //                  reads B; emitted right after a passing EnumTagEq.
        //   EnumNameEq   [op][dst][scrut][enumNameIdx:c]  dst = (scrut is
        //                  EnumValue && scrut.EnumName == Names[c]). Emitted for an
        //                  EXPLICIT `case Enum.Variant(..)` BEFORE EnumTagEq, to
        //                  disambiguate same-named variants across enums (records
        //                  carry no EnumName → never match an explicit pattern,
        //                  matching the visitor's EnumName!=null record exclusion).
        //   MatchFail    [op]   throws the visitor's exact "no match arm covered
        //                  the scrutinee value" error. Emitted as the final
        //                  instruction of a match with NO wildcard/variable
        //                  catch-all (exhaustive enum match): reached only when no
        //                  arm matched. No operands; a Throw-kind CFG terminator.
        //   TupleShape   [op][dst][scrut][len:c]  dst = (scrut is TupleValue &&
        //                  Elements.Count == c). A tuple pattern's element count IS
        //                  its shape — a mismatch is a no-match (not an error), so
        //                  no MatchArity follows. Elements extract via EnumPayload
        //                  (polymorphic over enum/record/tuple/list by index).
        EnumTagEq       = 0x95,
        EnumPayload     = 0x96,
        MatchArity      = 0x97,
        EnumNameEq      = 0x98,
        MatchFail       = 0x99,
        TupleShape      = 0x9A,
        //   StructShape  [op][dst][scrut][nameIdx:c]  dst = scrut is a struct OR
        //                  class OR record instance whose declared type name ==
        //                  Names[c] (nominal; a name mismatch is a no-match).
        //   StructFieldGet [op][dst][scrut][fieldIdx:c]  dst = scrut.GetField(
        //                  Names[c]); throws the visitor's exact "struct/class 'X'
        //                  has no field 'f'" error if the field is absent. Reached
        //                  only after a passing StructShape.
        StructShape     = 0x9B,
        StructFieldGet  = 0x9C,
        //   ListShape    [op][dst][scrut][c]  c = (modeBit<<7)|len7. dst = scrut is
        //                  ListValue && (mode 0: Count==len7 / mode 1: Count>=len7).
        //                  EXACT for a no-rest pattern, >= when a `..rest` is
        //                  present. Front elements extract via EnumPayload (index).
        //   ListElemBack [op][dst][scrut][kFromEnd:c]  dst = Elements[Count-k]
        //                  (k 1-based from the end) — the suffix patterns after a
        //                  `..rest`. Reached only after a passing ListShape.
        //   ListRestSlice[op][dst][scrut][c]  c = (prefix4<<4)|suffix4. dst = a new
        //                  ListValue of Elements[prefix .. Count-suffix] — the
        //                  middle captured by a named `..rest`.
        ListShape       = 0x9D,
        ListElemBack    = 0x9E,
        ListRestSlice   = 0x9F,
        //   MapShape     [op][dst][scrut][c]  c = (openBit<<7)|count7. dst = scrut
        //                  is MapValue && (open: Pairs.Count>=count / closed:
        //                  Pairs.Count==count). A `..` open-rest sets the open bit.
        //   MapHasKey    [op][dst][map][keySlot]  dst = the map contains keySlot
        //                  (structural GetComparisonEq over Pairs). 3-address: reads
        //                  B (map) + C (key SLOT), like Eq.
        //   MapGetKey    [op][dst][map][keySlot]  dst = the value at keySlot (a
        //                  preceding MapHasKey confirmed presence). 3-address.
        MapShape        = 0xA7,
        MapHasKey       = 0xAF,
        MapGetKey       = 0xCF,
        // L7 — `target?` postfix try-unwrap. dst (A) = the Ok payload; reads the
        // target at B. On Result.Err the handler signals an early function return
        // of the Err value (the case `return`s SuccessReturn); a non-Result / bad
        // variant throws the visitor's exact error. Writes A, reads B; impure
        // (the Err early-return is a side effect — never DCE'd/hoisted).
        TryUnwrap       = 0xED,
        // L7 — destructuring-declaration bind BY NAME. `a` = value slot, imm16 =
        // Names index. Does ctx.SymbolTable.SetLocal(Names[idx], locals[a]) — the
        // name-based binding the destructuring binders resolve to (the Resolver
        // does not slot-allocate them). Reads A, writes no slot; impure.
        DeclareLocalByName = 0xEE,
        // L7 — destructuring no-match terminator. Throws the visitor's exact
        // "destructuring pattern did not match the initializer value" error.
        // No operands; a Throw-kind CFG terminator (defence-in-depth — the parser
        // rejects refutable destructuring patterns, so this is rarely reached).
        DestructureFail = 0xEF,

        // --- exceptions ---
        Throw           = 0xA0,   // a (err src)
        EnterTry        = 0xA1,   // _, handlerIdx:u16
        LeaveTry        = 0xA2,   // _, handlerIdx:u16
        FinallyEnd      = 0xA3,

        // --- extended bitwise shifts (boxed) ---
        //
        // Boxed dispatch for the arrow-family shifts. Encoding matches the
        // core arithmetic / shift opcodes:
        //   [op][a:dst][b:lhs][c:rhs]
        //
        // Semantics (RA_SHIFTS_DESIGN.md §1):
        //   Ushr  — logical / unsigned right shift (`>>>`)
        //   Rol   — rotate-left  (`<<<<`)
        //   Ror   — rotate-right (`>>>>`)
        //
        // The logical LEFT shift (`<<<`) shares Opcode.Shl with `<<` because
        // for two's-complement integers the bit pattern is identical (zero-
        // fill on the low end). Keeping a separate token (vs opcode) lets the
        // parser preserve the unsigned intent in diagnostics while avoiding
        // a redundant VM dispatch case.
        Ushr            = 0xA4,
        Rol             = 0xA5,
        Ror             = 0xA6,

        // L7 (Match is-type pattern). `case is T [as v]`. dst = the scrutinee's
        // runtime type matches the TestedType of the IsTypeNode parked in
        // AstRefs[c] (WideC index, like Cast), via TypeSystem.IsRuntimeTypeMatch.
        // Reuses the existing IsTypeNode serialization (no new pool / version).
        // Writes A, reads B; the optional `as v` binder binds the scrutinee.
        IsType          = 0xAE,

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

        // --- typed Int64 extended bitwise shifts ---
        //
        // Counterparts to the M68 II family. Operands are read as int64 from
        // the typed long-slot side of the tagged union; deopt to the boxed
        // Ushr / Rol / Ror on tag-mismatch or out-of-range shift count.
        //
        // Encoding identical to ShlII / ShrII:
        //   UshrII / RolII / RorII   [op][a:dst][b:lhs][c:rhs]
        //
        // Width is fixed at 64 — matches the Int64 tagged-union representation.
        // Smaller fixed-width integers (Int32, Int16, …) box up to the slow
        // path so the per-type masking semantics in
        // BitwiseRotate{Left,Right}edBy on the boxed value class always wins.
        UshrII          = 0xDD,
        RolII           = 0xDE,
        RorII           = 0xDF,

        // --- inline asm ---
        AsmInvoke       = 0xE0,   // L9: a (dst), imm16 (DefineRefs idx of the parked pure-text AsmBlockNode)
        AsmInvokeI      = 0xF2,   // L10: a (dst), b (argsBase), c (DefineRefs idx) — interpolated asm; reads argsBase+0..N-1 (N = interp parts), With-shaped
        RetYield        = 0xF3,   // L10: a (value slot) — function-level `yield X`: return from the fn with FlowState.Yield (mirrors OP_RET but yield-flow)
        AnnotationApply = 0xF4,   // L10: a (dst), imm16 (DefineRefs idx) — standalone `@Name(args)` value; builds the AnnotationInstanceValue (NativeDefine-shaped)
        SetPendingFlow  = 0xF5,   // L10: a (value slot), b (kind: 1=return 2=yield) — stash a control-flow escape through an enclosing finally; OP_FINALLY_END applies it
        CallGeneric     = 0xF6,   // L10: a (dst), b (fnSlot: callee@fnSlot, args@fnSlot+1..), c (DefineRefs idx of the parked FunctionCallNode) — explicit-generic-type-arg call `foo<int>(...)`; argCount + GenericTypeArgs read from the parked node (With-shaped reads, Call-shaped def)
        // L10: `left in right` / `left not in right` membership test. Same
        // operand shape as Eq: a (dst), b (left), c (right) → left.InCollection(
        // right). `not in` lowers as OP_IN into a temp followed by OP_NOT.
        // CAN RAISE (InCollection errors on a non-collection RHS) → kept OUT of
        // every DCE/LICM/GVN pure-eraseable list so its error site stays stable,
        // exactly like Div / Mod / boxed Ushr. (0xF7 is free; 0xF6 / 0xF9 used.)
        In              = 0xF7,   // a (dst), b (left), c (right) → left.InCollection(right)

        // L10 — list-literal SPREAD (`[a, ...x, b]`). Appends every element of
        // the iterable at slot B to the ListValue at slot A (built incrementally
        // by a preceding NewList + ListPush stream). Mirrors the list-literal
        // visitor's spread semantics EXACTLY: the spread source must be a
        // ListValue (ranges materialize to lists eagerly, so they qualify); any
        // other value raises the visitor's "Spread target must be an iterable
        // (e.g. list)" RuntimeError. No per-element copy (the visitor does a bare
        // AddRange). MUTATES the list at A + CAN RAISE → kept OUT of every
        // DCE/LICM/GVN/CSE pure list and defines no `locals` slot, exactly like
        // ListPush. (0xF8 was free; 0xF7 is In, 0xF9 is Halt.)
        ListExtend      = 0xF8,   // a (list), b (iterable) — append all items of iterable to list (spread)

        // ---- streams (Streams runtime — see RA_STREAMS_DESIGN.md §10) ----
        // Forward-jump opcode that branches if `locals[a]` is a sync stream
        // (RuntimeValueType.Stream). Encoding mirrors `Opcode.JmpIfNot`:
        // `[op:u8][a:u8][imm16: signed forward offset]`. Used at the top of
        // `for x in expr { … }` to dispatch between the materializing IR
        // fast-path and the lazy stream-pull path emitted right after it.
        JmpIfStream     = 0xE1,

        // Per-iteration lazy pull from a sync `StreamValue`. Encoding:
        // `[op:u8][itemSlot:u8][streamSlot:u8][continueSlot:u8]`.
        //
        // Semantics:
        //   * call stream.PullNext(ctx) synchronously (ValueTask short-circuit
        //     on the steady-state hot path);
        //   * on error → throw RaUserError;
        //   * on done   → `locals[continueSlot] = BooleanValue.False`. itemSlot
        //                 is left unchanged (the body's JmpIfNot exits before
        //                 it would be read);
        //   * on value  → `locals[itemSlot] = value` and
        //                 `locals[continueSlot] = BooleanValue.True`.
        //
        // Pairs with `Opcode.JmpIfNot continueSlot, exitOffset` to form a
        // single-allocation pull-and-test loop that supports infinite
        // streams + body `break`.
        ForEachStreamPull = 0xE2,

        // --- M90 fused compare-and-branch superinstructions ---
        //
        // Fuse the dominant loop-test pattern `cmpII cmp,b,c; JmpIfNot
        // cmp,off` (37% of dispatched opcodes in the bench suite —
        // LtII+JmpIfNot alone) into a single dispatch. Layout:
        //   [op][a:lhsSlot][b:rhsSlot][c:signed-8 offset]
        // Semantics: read lhs/rhs as int64 (deopt to the boxed
        // comparison on tag miss), evaluate the comparison, and branch
        // by `(sbyte)c` when the comparison is FALSE — mirroring the
        // `JmpIfNot` that followed the original compare. The branch
        // offset is relative to this op's pc+1 (same convention as
        // Jmp/JmpIf), so the rewriter sets it to `origJmpOffset + 1`
        // (the fused op sits one slot earlier than the JmpIfNot it
        // absorbs). Emitted only by IrRewriter's fusion phase, which
        // replaces the cmpII at PC n and turns the JmpIfNot at PC n+1
        // into a Pass — preserving the 1:1 PC-index invariant.
        JmpNotLtII      = 0xE3,   // if !(a <  b) pc += (sbyte)c
        JmpNotLeII      = 0xE4,   // if !(a <= b) pc += (sbyte)c
        JmpNotGtII      = 0xE5,   // if !(a >  b) pc += (sbyte)c
        JmpNotGeII      = 0xE6,   // if !(a >= b) pc += (sbyte)c
        JmpNotEqII      = 0xE7,   // if !(a == b) pc += (sbyte)c
        JmpNotNeII      = 0xE8,   // if !(a != b) pc += (sbyte)c

        // --- string accumulator (O(n) string building) ---
        // A loop string accumulator `var s = ""; for ... { s = s + x }` whose
        // ONLY in-loop access is the self-append is promoted to a per-frame
        // StringBuilder (VmFrame.StrAcc[imm16]), turning O(n^2) reallocating
        // concatenation into O(n) append. The boxed `s` SymbolEntry is left
        // untouched during the loop and refreshed once on exit via
        // StrAccMaterialize, so aliases / post-loop reads see a correct string.
        StrAccBegin     = 0xE9,   // StrAcc[imm16] = new StringBuilder(locals[a] as string)
        StrAccAppend    = 0xEA,   // StrAcc[imm16].Append(locals[a].ToString())
        StrAccMaterialize = 0xEB, // locals[a] = StringValue(StrAcc[imm16].ToString())
        // Typed-iter fast append: `s = s + i` where `i` is the loop's typed
        // Int64 iter. Reads f.Slots[a].Bits directly (no boxed mirror), appends
        // its decimal form — identical to NumberValue's integer string. Skips
        // the per-iter BoxI (a NumberValue allocation) + AssignBinding publish.
        StrAccAppendI   = 0xEC,   // StrAcc[imm16].Append(f.Slots[a] as int64)

        // --- misc ---
        Pass            = 0xF0,
        Delete          = 0xF1,   // a (slot)

        // L10 — `del name` statement (VariableDeleteNode). `a` unused,
        // nameIdx:imm16 (index into the Names pool). Removes the binding from
        // the symbol table (ctx.SymbolTable.Remove), raising the visitor's exact
        // "'<name>' variable does not exist" RuntimeError if the name is absent.
        // One opcode is emitted per name in a multi-name `del a, b`. MUTATES the
        // symbol table (a binding-removing side effect) + CAN RAISE → kept OUT of
        // every DCE/CSE/GVN pure list and defines no `locals` slot; the Resolver
        // forces every del'd name to be NON-slot-eligible so the Remove is
        // observable to a subsequent read (which then compiles to OP_LOAD_GLOBAL
        // and errors after the binding is gone). Also a Memory-SSA aliasing
        // barrier (a deletion can invalidate cached LoadGlobal lookups).
        DeleteLocal     = 0xFA,   // a unused, nameIdx:imm16 — remove binding from symbol table, raise if absent

        // L10 — `retry for N times delay M { … }` inter-attempt delay. The delay
        // (ms) is carried as the imm16 PAYLOAD (layout 2: [op][a:unused][imm16]),
        // NOT in a slot. Rationale: the only read of a slot-based delay would
        // live in the EhTable catch handler, which the CFG/SSA does NOT model
        // (off-CFG) — so a constant delay-load there is invisible to the DCE
        // solvers and gets erased (LoadConst / LoadIntS64 are both DCE-erasable
        // in one pass or another), leaving the op reading garbage. Encoding the
        // value as an immediate removes the separate load entirely, so there is
        // nothing for DCE to erase. The IR compiler gates M to a non-negative
        // integer literal in [0, 65535] (the imm16 range); any non-int /
        // negative / larger / runtime-expression delay falls back to the
        // RetryNodeVisitor, which carries the exact ExtractRetryInt coercion +
        // RA error messages AND validates M up-front. The handler calls
        // System.Threading.Thread.Sleep(ms) — the SAME blocking call the visitor
        // makes between failed attempts (that IS the parity behavior). It only
        // sleeps when ms > 0 (mirroring the visitor's `delayMs > 0`). BLOCKING +
        // SIDE-EFFECTING; defines NO slot, reads NO slot; kept OUT of every
        // DCE/CSE/GVN/LICM pure list so it is never erased or hoisted. Emitted
        // only on the retry path (after the attempt-counter increment + the
        // `attempt < N` check), never on the exhausted/else path — mirroring the
        // visitor's `if (attempt < retries - 1 && delayMs > 0) Thread.Sleep`.
        Sleep           = 0xFB,   // _, imm16 (ms) — Thread.Sleep(ms); blocking, side-effecting

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

using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Events;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of MemberAccessNodeVisitor. The visitor evaluates
    // `node.TargetNode` and then calls Apply(); the VM's OP_GET_MEMBER
    // opcode pre-evaluates the target into a slot and calls Apply()
    // directly with that value. Both paths produce a bit-identical
    // RuntimeResult.
    public static class MemberAccessHelper
    {
        // M28.1 BranchKind tags. Stable encoding — values are persisted in
        // per-PC inline cache slots and matched against the chosen-branch
        // dispatcher. Never re-number; append at the tail.
        private const byte BR_ENUM_VARIANT       = 1;
        private const byte BR_STRUCT_FIELD       = 2;
        private const byte BR_STRUCT_METHOD      = 3;
        private const byte BR_STRUCT_EXT         = 4;
        private const byte BR_CLASS_FIELD        = 5;
        private const byte BR_CLASS_METHOD_GROUP = 6;
        private const byte BR_CLASS_EXT          = 7;
        private const byte BR_SUPER              = 8;
        private const byte BR_CLASSTYPE_STATIC   = 9;
        private const byte BR_NAMESPACE          = 10;
        private const byte BR_MODULE             = 11;
        private const byte BR_PRIMITIVE_EXT      = 12;
        private const byte BR_RECORD_DECONSTRUCT = 13;
        private const byte BR_EVENT_INSTANCE     = 14;
        private const byte BR_EVENT_STATIC       = 15;
        private const byte BR_EVENT_REF          = 16;
        // v2.3 extension field IC branch. Caches:
        //   FieldIndex  -> global slot index from ExtensionFieldStorage
        //   CachedAux   -> ExtensionFieldDescriptor (for lazy default
        //                  evaluation when the slot is still empty)
        // The hit path indexes ExtFieldSlots directly when the init
        // bit is set; misses fall back to ApplyAndPrime which runs
        // the slow registry resolution + default eval and re-primes.
        private const byte BR_EXT_FIELD          = 17;

        // M28.1 IC-aware entry point used by OP_GET_MEMBER. Falls back to the
        // unconditional Apply for first-hit (BranchKind = 0) and every miss.
        // Caller is responsible for the (pc, slot) lookup; this method only
        // reads / writes the slot fields. Sets BranchKind on first success so
        // subsequent hits can short-circuit the long type-tag chain.
        public static RuntimeResult ApplyWithIc(
            MemberAccessNode node,
            Context context,
            RuntimeValue target,
            ref MemberAccessIcSlot icSlot)
        {
            var res = new RuntimeResult();
            string memberName = node.MemberTok.Value?.ToString() ?? "";
            var t = target.Type;

            // Fast path: cache hit. The slot was primed on a prior visit and
            // the (TargetType, Shape) pair still matches the current target.
            // Dispatch directly to the branch chosen on first visit; for
            // stable-resolution branches return the cached RuntimeValue.
            if (icSlot.BranchKind != 0 && icSlot.TargetType == t)
            {
                object? curShape = ExtractShape(target);
                // M42 PIC: primary miss → scan Pic; on hit, promote into
                // primary via LRU-1 swap so the hottest shape stays cheap.
                // Old primary moves into the freed Pic slot, preserving
                // the cached resolution for the next time that shape is
                // observed.
                if (!ReferenceEquals(icSlot.Shape, curShape))
                {
                    var pic = icSlot.Pic;
                    if (pic != null)
                    {
                        for (int i = 0; i < pic.Length; i++)
                        {
                            ref var picEntry = ref pic[i];
                            if (picEntry.BranchKind != 0
                                && picEntry.TargetType == t
                                && ReferenceEquals(picEntry.Shape, curShape))
                            {
                                var saved = new MemberAccessIcEntry
                                {
                                    TargetType = icSlot.TargetType,
                                    Shape = icSlot.Shape,
                                    BranchKind = icSlot.BranchKind,
                                    CachedAux = icSlot.CachedAux,
                                    CachedResult = icSlot.CachedResult,
                                    FieldIndex = icSlot.FieldIndex,
                                    ExtRegistry = icSlot.ExtRegistry,
                                };
                                icSlot.TargetType = picEntry.TargetType;
                                icSlot.Shape = picEntry.Shape;
                                icSlot.BranchKind = picEntry.BranchKind;
                                icSlot.CachedAux = picEntry.CachedAux;
                                icSlot.CachedResult = picEntry.CachedResult;
                                icSlot.FieldIndex = picEntry.FieldIndex;
                                icSlot.ExtRegistry = picEntry.ExtRegistry;
                                picEntry = saved;
                                curShape = icSlot.Shape; // refresh after promote
                                break;
                            }
                        }
                    }
                }
                // v2.5: for extension-routed branches the IC is also
                // keyed on the calling context's ExtensionRegistry —
                // two contexts that share a Definition but ship
                // different registries must not cross-pollinate
                // cached resolutions. Native branches set ExtRegistry
                // to null at prime time, so the check is a no-op.
                bool extRegistryMatch = !IsExtensionBranch(icSlot.BranchKind)
                    || ReferenceEquals(icSlot.ExtRegistry, context.Extensions);
                if (ReferenceEquals(icSlot.Shape, curShape) && extRegistryMatch)
                {
                    switch (icSlot.BranchKind)
                    {
                        case BR_ENUM_VARIANT:
                        {
                            // EnumType variants are constructed once and never
                            // re-bound — direct return of the cached
                            // RuntimeValue is safe.
                            return res.Success(icSlot.CachedResult!);
                        }
                        case BR_STRUCT_FIELD:
                        {
                            // M41: shape-indexed slot read. icSlot.FieldIndex
                            // points into StructInstance.FieldSlots; skip the
                            // Dictionary<string,...>.TryGetValue walk in
                            // GetField. Fall back to dict lookup when the
                            // slot is empty (defensive — should never happen
                            // for a primed IC).
                            var inst = (StructInstanceValue)target;
                            if (!inst.IsFieldPublic(memberName) && !IsInsideSameType(context, inst.Definition.StructName))
                                return res.Failure(PrivateFieldError(node, inst.Definition.StructName, memberName, context));
                            int fi = icSlot.FieldIndex;
                            var slots = inst.FieldSlots;
                            if ((uint)fi < (uint)slots.Length)
                            {
                                var v = slots[fi];
                                if (v != null)
                                {
                                    var ret = v.IsCopy ? v.Copy() : v;
                                    return res.Success(ret.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                                }
                            }
                            return res.Success(inst.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_STRUCT_METHOD:
                        {
                            // CachedAux pins the StructMethodDefinitionNode
                            // chosen on first visit so the dictionary-keyed
                            // Definition.GetMethod lookup is skipped here.
                            // The BoundStructMethodValue wrapper still
                            // allocates because it binds the receiver
                            // instance.
                            var inst = (StructInstanceValue)target;
                            var method = (Parser.Nodes.Structs.StructMethodDefinitionNode)icSlot.CachedAux!;
                            if (!method.IsPublic && !IsInsideSameType(context, inst.Definition.StructName))
                                return res.Failure(PrivateMethodError(node, inst.Definition.StructName, memberName, context));
                            return res.Success(new BoundStructMethodValue(inst.Definition, inst, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_STRUCT_EXT:
                        case BR_CLASS_EXT:
                        case BR_PRIMITIVE_EXT:
                        {
                            var ext = context.Extensions.ResolveMethodEntries(target, memberName);
                            if (ext.Count == 0) break; // refresh — extension table mutated
                            return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_EXT_FIELD:
                        {
                            // Fast path: read ExtFieldSlots[slot]
                            // directly when the init bit is set —
                            // the common steady-state for a hot field.
                            int slot = icSlot.FieldIndex;
                            RuntimeValue?[]? slots = null;
                            if (target is ClassInstanceValue ciFast) slots = ciFast.ExtFieldSlots;
                            else if (target is StructInstanceValue siFast) slots = siFast.ExtFieldSlots;
                            else if (target is ClassTypeValue ctFast) slots = ctFast.StaticExtFieldSlots;
                            if (slots != null && (uint)slot < (uint)slots.Length
                                && ExtensionFieldStorage.IsInitialized(target, slot))
                            {
                                var v = slots[slot];
                                if (v != null)
                                {
                                    var ret = v.IsCopy ? v.Copy() : v;
                                    return res.Success(ret.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                                }
                            }
                            // Uninitialised slot: re-run dispatch from
                            // the cached entry. This still skips the
                            // registry walk (CachedAux is the
                            // pre-resolved ExtensionFieldEntry), so the
                            // "first read with default expr" path stays
                            // single-traversal even after IC priming.
                            if (icSlot.CachedAux is ExtensionFieldEntry fieldEntry)
                                return ExtensionDispatch.DispatchFieldGet(target, fieldEntry, context, node.PositionStart, node.PositionEnd);
                            break;
                        }
                        case BR_RECORD_DECONSTRUCT:
                        {
                            var recInst = (RaLanguage.Interpreter.Values.Records.RecordInstanceValue)target;
                            return res.Success(new RaLanguage.Interpreter.Values.Records.BoundRecordDeconstructValue(recInst)
                                .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_EVENT_INSTANCE:
                        {
                            // Owner is the target instance (StructInstance/RecordInstance/ClassInstance).
                            // Descriptor is pinned in CachedAux. Skip the GetEvent dict walk.
                            var desc = (EventDescriptor)icSlot.CachedAux!;
                            // Re-check subscribe visibility on the cached path —
                            // the answer depends on the caller's context, which
                            // the IC cannot bake in.
                            string declType = desc.DeclaringTypeName;
                            bool isInside =
                                IsInsideSameType(context, declType) ||
                                (target is ClassInstanceValue ci && IsInsideClassHierarchy(context, ci.Definition));
                            if (!isInside && !desc.SubscribeIsPublic)
                            {
                                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                    $"event '{declType}.{desc.Name}' is not accessible here (subscribe is private)",
                                    context));
                            }
                            return res.Success(new EventSubscriptionValue(target, desc)
                                .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_EVENT_STATIC:
                        {
                            var desc = (EventDescriptor)icSlot.CachedAux!;
                            var classType = (ClassTypeValue)target;
                            bool isInside =
                                IsInsideSameType(context, classType.ClassName) ||
                                IsInsideClassHierarchy(context, classType);
                            if (!isInside && !desc.SubscribeIsPublic)
                            {
                                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                    $"event '{desc.DeclaringTypeName}.{desc.Name}' is not accessible here (subscribe is private)",
                                    context));
                            }
                            return res.Success(new EventSubscriptionValue(classType, desc)
                                .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_EVENT_REF:
                        {
                            // Class.Event for instance events — unbound ref.
                            var desc = (EventDescriptor)icSlot.CachedAux!;
                            return res.Success(new EventRefValue(target, desc)
                                .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_CLASS_FIELD:
                        {
                            // M38: shape-indexed slot path. icSlot.FieldIndex
                            // is the cached offset into instance.FieldSlots,
                            // computed at prime time from the class's static
                            // field shape. Skip the Dictionary<string,...>
                            // .TryGetValue + key-hash walk that GetField would
                            // otherwise pay per access.
                            var inst = (ClassInstanceValue)target;
                            int fi = icSlot.FieldIndex;
                            var slots = inst.FieldSlots;
                            if ((uint)fi < (uint)slots.Length)
                            {
                                var v = slots[fi];
                                if (v != null)
                                {
                                    var ret = v.IsCopy ? v.Copy() : v;
                                    return res.Success(ret.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                                }
                            }
                            return res.Success(inst.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_CLASS_METHOD_GROUP:
                        {
                            // CachedAux holds the resolved List<FunctionDefinitionNode>
                            // produced by ResolveInstanceMethods on first
                            // visit. Reusing it skips the inheritance walk
                            // + LINQ allocation per dispatch.
                            var inst = (ClassInstanceValue)target;
                            var methods = (System.Collections.Generic.List<FunctionDefinitionNode>)icSlot.CachedAux!;
                            return res.Success(new BoundClassMethodGroupValue(inst.Definition, inst, methods).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_CLASSTYPE_STATIC:
                        {
                            // Static field resolution returns the field's
                            // current value, which can be reassigned — read it
                            // out of the cached ClassTypeValue. Static methods
                            // resolved on first visit are pinned in
                            // CachedResult; that BoundClassMethodValue holds
                            // owner + method refs with no receiver, so
                            // identity is stable.
                            if (icSlot.CachedResult != null)
                                return res.Success(icSlot.CachedResult.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                            var classType = (ClassTypeValue)target;
                            if (classType.HasStaticField(memberName))
                                return res.Success(classType.StaticFields[memberName].SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                            break;
                        }
                        case BR_NAMESPACE:
                        {
                            var ns = (NamespaceValue)target;
                            var entry = ns.Members.GetLocalEntry(memberName);
                            if (entry == null || !entry.IsPublic) break;
                            return res.Success(entry.Value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                        }
                        case BR_MODULE:
                        {
                            var moduleWrapper = (ModuleWrapperValue)target;
                            var ext = moduleWrapper.Module.Extensions.ResolveMethodEntries(target, memberName);
                            if (ext.Count > 0)
                                return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                            return res.Success(moduleWrapper.Module.SymbolTable.Get(memberName));
                        }
                        case BR_SUPER:
                            break; // fall through to slow path; uncommon enough not to specialize
                    }
                }
            }

            // M42 PIC eviction: if the primary slot is populated with a
            // shape different from the current observation, save it into
            // a Pic entry before the slow path overwrites primary with
            // the freshly-resolved branch. Without this step, polymorphic
            // call sites would thrash the primary on every visit.
            if (icSlot.BranchKind != 0)
            {
                var pic = icSlot.Pic ?? (icSlot.Pic = new MemberAccessIcEntry[2]);
                int writeIdx = -1;
                for (int i = 0; i < pic.Length; i++)
                {
                    if (pic[i].BranchKind == 0) { writeIdx = i; break; }
                }
                if (writeIdx < 0) writeIdx = 0; // ring-replace oldest
                pic[writeIdx] = new MemberAccessIcEntry
                {
                    TargetType = icSlot.TargetType,
                    Shape = icSlot.Shape,
                    BranchKind = icSlot.BranchKind,
                    CachedAux = icSlot.CachedAux,
                    CachedResult = icSlot.CachedResult,
                    FieldIndex = icSlot.FieldIndex,
                    ExtRegistry = icSlot.ExtRegistry,
                };
            }
            // Slow path: full chain dispatch with IC prime on success.
            return ApplyAndPrime(node, context, target, memberName, ref icSlot);
        }

        private static bool IsExtensionBranch(byte kind)
            => kind == BR_STRUCT_EXT
            || kind == BR_CLASS_EXT
            || kind == BR_PRIMITIVE_EXT
            || kind == BR_EXT_FIELD;

        private static void SetExtRegistry(ref MemberAccessIcSlot icSlot, Context context)
        {
            icSlot.ExtRegistry = context.Extensions;
        }

        // Materialises the resolution shape used as the IC key. For
        // instance-bound values the *type definition* is the shape so two
        // different instances of the same class share an IC slot. For type
        // values themselves the value's own identity is the shape.
        private static object? ExtractShape(RuntimeValue target)
        {
            switch (target.Type)
            {
                case RuntimeValueType.EnumType:       return target; // EnumTypeValue
                case RuntimeValueType.StructInstance: return ((StructInstanceValue)target).Definition;
                case RuntimeValueType.ClassInstance:  return ((ClassInstanceValue)target).Definition;
                case RuntimeValueType.Super:          return ((SuperProxyValue)target).BaseClass;
                case RuntimeValueType.ClassType:      return target; // ClassTypeValue
                case RuntimeValueType.Namespace:      return target; // NamespaceValue
                case RuntimeValueType.ModuleWrapper:  return ((ModuleWrapperValue)target).Module;
                default: return target.GetType();
            }
        }

        private static Errors.Types.RuntimeError PrivateFieldError(MemberAccessNode node, string typeName, string memberName, Context context)
            => new Errors.Types.RuntimeError(node.PositionStart, node.PositionEnd,
                $"field '{memberName}' of struct '{typeName}' is private",
                context,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "accessed from outside the declaring struct",
                help: "mark the field with 'pub' to expose it, or access it only from within the struct's own methods");

        private static Errors.Types.RuntimeError PrivateMethodError(MemberAccessNode node, string typeName, string memberName, Context context)
            => new Errors.Types.RuntimeError(node.PositionStart, node.PositionEnd,
                $"method '{memberName}' of struct '{typeName}' is private",
                context,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "called from outside the declaring struct",
                help: "mark the method with 'pub' to expose it");

        // Runs the cold dispatch chain identical to Apply() and primes the
        // inline cache slot before returning. Split out so the hit path stays
        // a small switch.
        private static RuntimeResult ApplyAndPrime(
            MemberAccessNode node,
            Context context,
            RuntimeValue target,
            string memberName,
            ref MemberAccessIcSlot icSlot)
        {
            var res = new RuntimeResult();

            if (target.Type == RuntimeValueType.EnumType)
            {
                var enumType = (EnumTypeValue)target;
                if (!enumType.HasMember(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"enum '{enumType.EnumName}' has no member '{memberName}'",
                        context,
                        code: DiagnosticCode.RuntimeUndefinedSymbol,
                        primaryLabel: $"'{memberName}' is not a variant",
                        help: $"available variants: {string.Join(", ", enumType.VariantsByName.Keys)}"));
                var variant = enumType.GetMember(memberName);
                icSlot.TargetType = RuntimeValueType.EnumType;
                icSlot.Shape = enumType;
                icSlot.BranchKind = BR_ENUM_VARIANT;
                icSlot.CachedResult = variant;
                return res.Success(variant);
            }

            if (target.Type == RuntimeValueType.StructInstance || target.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)target;

                // Property probe — properties take precedence over
                // fields when both are declared. The parser prevents
                // same-name collisions, so the dispatch is unambiguous.
                // IC is not primed for property hits in v1; the
                // property pipeline runs from scratch each visit.
                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    icSlot.BranchKind = 0;
                    bool isInside = IsInsideSameType(context, instance.Definition.StructName);
                    return PropertyAccessOps.Get(instance, propDesc, context, node.PositionStart, node.PositionEnd, isInside);
                }

                // Event probe — only record class (StructInstance acting
                // as record) reaches here for events; value structs and
                // value records reject `event` declarations at parse time.
                // Subscribe visibility is checked here; raise visibility
                // is checked on Execute via Context comparison.
                var evDesc = instance.Definition.GetEvent(memberName);
                if (evDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.StructName);
                    if (!isInside && !evDesc.SubscribeIsPublic)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"event '{evDesc.DeclaringTypeName}.{evDesc.Name}' is not accessible here (subscribe is private)",
                            context));
                    }
                    icSlot.TargetType = target.Type;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_EVENT_INSTANCE;
                    icSlot.CachedAux = evDesc;
                    return res.Success(new EventSubscriptionValue(instance, evDesc)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (instance.HasField(memberName))
                {
                    if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(PrivateFieldError(node, instance.Definition.StructName, memberName, context));
                    icSlot.TargetType = RuntimeValueType.StructInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_STRUCT_FIELD;
                    // M41: pin the slot offset so subsequent hits index the
                    // FieldSlots array directly.
                    icSlot.FieldIndex = instance.Definition.GetFieldSlotIndex(memberName);
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var method = instance.Definition.GetMethod(memberName);
                if (method != null)
                {
                    if (!method.IsPublic && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(PrivateMethodError(node, instance.Definition.StructName, memberName, context));
                    icSlot.TargetType = RuntimeValueType.StructInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_STRUCT_METHOD;
                    icSlot.CachedAux = method;
                    return res.Success(new BoundStructMethodValue(instance.Definition, instance, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                // Extension field — side-mapped O(1) lookup. Comes
                // before events/properties/methods because fields
                // surface as direct values (no callable wrapping).
                // Slow-path here primes BR_EXT_FIELD so subsequent
                // visits skip the registry walk entirely; the IC
                // hit path reads ExtFieldSlots[slot] directly.
                {
                    var sFieldEntry = context.Extensions.ResolveFieldEntry(instance, memberName, out var sFieldAmb);
                    if (sFieldEntry != null)
                    {
                        if (sFieldAmb != null)
                        {
                            icSlot.BranchKind = 0;
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"ambiguous extension field '{sFieldEntry.Descriptor.Name}' on '{instance.Definition.StructName}' — declared in two imported modules:\n  - {sFieldEntry.FormatSource()}\n  - {sFieldAmb.FormatSource()}",
                                context,
                                code: DiagnosticCode.RuntimeGeneric));
                        }
                        icSlot.TargetType = target.Type;
                        icSlot.Shape = instance.Definition;
                        icSlot.BranchKind = BR_EXT_FIELD;
                        icSlot.ExtRegistry = context.Extensions;
                        icSlot.FieldIndex = sFieldEntry.Descriptor.SlotIndex;
                        icSlot.CachedAux = sFieldEntry;
                        return ExtensionDispatch.DispatchFieldGet(instance, sFieldEntry, context, node.PositionStart, node.PositionEnd);
                    }
                }

                // Extension event — surface as EventSubscriptionValue
                // so `obj.ExtEvent.on(...)` etc. work identically to
                // native events. Subscribers piggy-back on
                // `instance.EventSubs[name]` so the storage is unified.
                if (ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtEv))
                {
                    icSlot.BranchKind = 0;
                    return sExtEv;
                }

                // Extension property — bypasses IC: property results
                // are value-dependent and the existing IC machinery
                // already opts out for native properties for the same
                // reason. Keep parity here.
                if (ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtProp))
                {
                    icSlot.BranchKind = 0;
                    return sExtProp;
                }

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                {
                    icSlot.TargetType = RuntimeValueType.StructInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_STRUCT_EXT;
                    icSlot.ExtRegistry = context.Extensions;
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                // Synthetic record built-ins. `deconstruct` is the only one
                // for now; resolved last so any user-defined member of the
                // same name on the record body wins. Member access yields
                // a BoundRecordDeconstructValue that captures the receiver;
                // invoking it returns a TupleValue of the primary fields.
                if (instance is RaLanguage.Interpreter.Values.Records.RecordInstanceValue recInst
                    && string.Equals(memberName, "deconstruct", StringComparison.Ordinal))
                {
                    icSlot.TargetType = RuntimeValueType.RecordInstance;
                    icSlot.Shape = recInst.Definition;
                    icSlot.BranchKind = BR_RECORD_DECONSTRUCT;
                    return res.Success(new RaLanguage.Interpreter.Values.Records.BoundRecordDeconstructValue(recInst)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"struct '{instance.Definition.StructName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the struct definition / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)target;

                // Property probe (walks BaseClass via GetProperty).
                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    icSlot.BranchKind = 0;
                    bool isInside = IsInsideSameType(context, instance.Definition.ClassName)
                                 || IsInsideClassHierarchy(context, instance.Definition);
                    return PropertyAccessOps.Get(instance, propDesc, context, node.PositionStart, node.PositionEnd, isInside);
                }

                // Event probe (walks BaseClass via GetEvent).
                var evDesc = instance.Definition.GetEvent(memberName);
                if (evDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.ClassName)
                                 || IsInsideClassHierarchy(context, instance.Definition);
                    if (!isInside && !evDesc.SubscribeIsPublic)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"event '{evDesc.DeclaringTypeName}.{evDesc.Name}' is not accessible here (subscribe is private)",
                            context));
                    }
                    icSlot.TargetType = RuntimeValueType.ClassInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_EVENT_INSTANCE;
                    icSlot.CachedAux = evDesc;
                    return res.Success(new EventSubscriptionValue(instance, evDesc)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (instance.HasField(memberName))
                {
                    icSlot.TargetType = RuntimeValueType.ClassInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_CLASS_FIELD;
                    // M38: pin the static slot offset so subsequent hits
                    // index the slot array directly.
                    icSlot.FieldIndex = instance.Definition.GetFieldSlotIndex(memberName);
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var native = instance.Definition.ResolveInstanceMethods(memberName);
                if (native.Count > 0)
                {
                    icSlot.TargetType = RuntimeValueType.ClassInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_CLASS_METHOD_GROUP;
                    icSlot.CachedAux = native;
                    return res.Success(new BoundClassMethodGroupValue(instance.Definition, instance, native).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                {
                    var cFieldEntry = context.Extensions.ResolveFieldEntry(instance, memberName, out var cFieldAmb);
                    if (cFieldEntry != null)
                    {
                        if (cFieldAmb != null)
                        {
                            icSlot.BranchKind = 0;
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"ambiguous extension field '{cFieldEntry.Descriptor.Name}' on '{instance.Definition.ClassName}' — declared in two imported modules:\n  - {cFieldEntry.FormatSource()}\n  - {cFieldAmb.FormatSource()}",
                                context,
                                code: DiagnosticCode.RuntimeGeneric));
                        }
                        icSlot.TargetType = RuntimeValueType.ClassInstance;
                        icSlot.Shape = instance.Definition;
                        icSlot.BranchKind = BR_EXT_FIELD;
                        icSlot.ExtRegistry = context.Extensions;
                        icSlot.FieldIndex = cFieldEntry.Descriptor.SlotIndex;
                        icSlot.CachedAux = cFieldEntry;
                        return ExtensionDispatch.DispatchFieldGet(instance, cFieldEntry, context, node.PositionStart, node.PositionEnd);
                    }
                }

                if (ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtEv))
                {
                    icSlot.BranchKind = 0;
                    return cExtEv;
                }

                if (ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtProp))
                {
                    icSlot.BranchKind = 0;
                    return cExtProp;
                }

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                {
                    icSlot.TargetType = RuntimeValueType.ClassInstance;
                    icSlot.Shape = instance.Definition;
                    icSlot.BranchKind = BR_CLASS_EXT;
                    icSlot.ExtRegistry = context.Extensions;
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{instance.Definition.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the class / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.Super)
            {
                var sup = (SuperProxyValue)target;
                if (sup.BaseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        "'super' cannot resolve a base class",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "no base class is in scope here",
                        help: "'super' is only meaningful inside methods of a class that extends another via ':'"));

                if (sup.Instance.HasField(memberName))
                    return res.Success(sup.Instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var candidates = sup.BaseClass.ResolveCandidates(memberName);
                if (candidates.Count > 0)
                    return res.Success(new BoundMethodGroupValue(memberName, sup.Instance, sup.BaseClass, candidates).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"base class '{sup.BaseClass.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such inherited field or method",
                    help: "verify the name and visibility of the inherited member"));
            }

            if (target.Type == RuntimeValueType.ClassType)
            {
                var classType = (ClassTypeValue)target;
                if (classType.HasStaticField(memberName))
                {
                    // Static fields are mutable → cache the branch but NOT the
                    // value. Cached read still pays one dict lookup but skips
                    // the type-tag cascade.
                    icSlot.TargetType = RuntimeValueType.ClassType;
                    icSlot.Shape = classType;
                    icSlot.BranchKind = BR_CLASSTYPE_STATIC;
                    icSlot.CachedResult = null;
                    return res.Success(classType.StaticFields[memberName].SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                // Static ext-field probe — `extend T { static var X }`.
                if (ExtensionDispatch.TryGetField(classType, memberName, context, node.PositionStart, node.PositionEnd, out var staticExtField))
                {
                    icSlot.BranchKind = 0;
                    return staticExtField;
                }
                // Named constructor (`T.name(...)`). Never inline-cached: both
                // visibility and overload resolution depend on the live call,
                // and construction cost dominates, so re-resolve on each hit.
                if (classType.HasAnyConstructorNamed(memberName))
                {
                    icSlot.BranchKind = 0;
                    return res.Success(new BoundConstructorValue(classType, memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                if (classType.TryGetStaticMethodOwner(memberName, out var owner, out var method) && method != null)
                {
                    var bound = new BoundClassMethodValue(owner, null, method, isStatic: true).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    icSlot.TargetType = RuntimeValueType.ClassType;
                    icSlot.Shape = classType;
                    icSlot.BranchKind = BR_CLASSTYPE_STATIC;
                    icSlot.CachedResult = bound;
                    return res.Success(bound);
                }
                // Event probe on the class type itself:
                //   - static → EventSubscriptionValue (bound to the type)
                //   - instance → EventRefValue (first-class unbound ref;
                //     call with an instance arg to get a subscription).
                var evOnType = classType.GetEvent(memberName);
                if (evOnType != null)
                {
                    if (evOnType.IsStatic)
                    {
                        bool isInside = IsInsideSameType(context, classType.ClassName)
                                     || IsInsideClassHierarchy(context, classType);
                        if (!isInside && !evOnType.SubscribeIsPublic)
                        {
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"event '{evOnType.DeclaringTypeName}.{evOnType.Name}' is not accessible here (subscribe is private)",
                                context));
                        }
                        icSlot.TargetType = RuntimeValueType.ClassType;
                        icSlot.Shape = classType;
                        icSlot.BranchKind = BR_EVENT_STATIC;
                        icSlot.CachedAux = evOnType;
                        return res.Success(new EventSubscriptionValue(classType, evOnType)
                            .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                    }
                    icSlot.TargetType = RuntimeValueType.ClassType;
                    icSlot.Shape = classType;
                    icSlot.BranchKind = BR_EVENT_REF;
                    icSlot.CachedAux = evOnType;
                    return res.Success(new EventRefValue(classType, evOnType)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                var ctSuggest = classType.SuggestMember(memberName);
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{classType.ClassName}' has no static member or constructor named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such static member or named constructor",
                    help: ctSuggest != null
                        ? $"did you mean '{classType.ClassName}.{ctSuggest}'?"
                        : $"check the spelling, or declare '{memberName}' with 'static' inside class '{classType.ClassName}'"));
            }

            // EventSubscriptionValue: synthetic methods (on/off/clear/count).
            if (target.Type == RuntimeValueType.EventSubscription)
            {
                var evSrc = (EventSubscriptionValue)target;
                BoundEventMethodValue? bound = memberName switch
                {
                    "on"    => new BoundEventMethodValue(evSrc, EventMethodKind.On),
                    "off"   => new BoundEventMethodValue(evSrc, EventMethodKind.Off),
                    "clear" => new BoundEventMethodValue(evSrc, EventMethodKind.Clear),
                    "count" => new BoundEventMethodValue(evSrc, EventMethodKind.Count),
                    _ => null
                };
                if (bound != null)
                {
                    bound.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(bound);
                }
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"event '{evSrc.Descriptor.DeclaringTypeName}.{evSrc.Descriptor.Name}' has no method '{memberName}' (available: on, off, clear, count)",
                    context));
            }

            // SubscriptionValue: dispose / is_active.
            if (target.Type == RuntimeValueType.Subscription)
            {
                var sub = (SubscriptionValue)target;
                BoundSubscriptionMethodValue? bound = memberName switch
                {
                    "dispose"    => new BoundSubscriptionMethodValue(sub, SubscriptionMethodKind.Dispose),
                    "is_active"  => new BoundSubscriptionMethodValue(sub, SubscriptionMethodKind.IsActive),
                    _ => null
                };
                if (bound != null)
                {
                    bound.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(bound);
                }
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"subscription has no method '{memberName}' (available: dispose, is_active)",
                    context));
            }

            // RecordType (record class definition) — surface instance
            // events as EventRefValue so `RecClass.Event(instance)` works
            // symmetrically with classes.
            if (target.Type == RuntimeValueType.RecordType)
            {
                var recType = (RaLanguage.Interpreter.Values.Records.RecordTypeValue)target;
                var evRec = recType.GetEvent(memberName);
                if (evRec != null && !evRec.IsStatic)
                {
                    return res.Success(new EventRefValue(recType, evRec)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            if (target.Type == RuntimeValueType.Namespace)
            {
                var ns = (NamespaceValue)target;
                var entry = ns.Members.GetLocalEntry(memberName);
                if (entry == null || !entry.IsPublic)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Namespace '{(ns.IsRoot ? "<global>" : ns.QualifiedName)}' has no public member '{memberName}'", context));
                icSlot.TargetType = RuntimeValueType.Namespace;
                icSlot.Shape = ns;
                icSlot.BranchKind = BR_NAMESPACE;
                return res.Success(entry.Value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (target.Type == RuntimeValueType.ModuleWrapper)
            {
                var moduleWrapper = (ModuleWrapperValue)target;
                var ext = moduleWrapper.Module.Extensions.ResolveMethodEntries(target, memberName);
                if (ext.Count > 0)
                {
                    icSlot.TargetType = RuntimeValueType.ModuleWrapper;
                    icSlot.Shape = moduleWrapper.Module;
                    icSlot.BranchKind = BR_MODULE;
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                icSlot.TargetType = RuntimeValueType.ModuleWrapper;
                icSlot.Shape = moduleWrapper.Module;
                icSlot.BranchKind = BR_MODULE;
                return res.Success(moduleWrapper.Module.SymbolTable.Get(memberName));
            }

            // Extension methods on built-in / primitive types.
            if (target.Type == RuntimeValueType.Enum || target.Type == RuntimeValueType.EnumType ||
                target.Type == RuntimeValueType.String || target.Type == RuntimeValueType.Number ||
                target.Type == RuntimeValueType.Integer || target.Type == RuntimeValueType.Long ||
                target.Type == RuntimeValueType.Float || target.Type == RuntimeValueType.Double ||
                target.Type == RuntimeValueType.UnsignedInteger || target.Type == RuntimeValueType.UnsignedLong ||
                target.Type == RuntimeValueType.Short || target.Type == RuntimeValueType.UnsignedShort ||
                target.Type == RuntimeValueType.Int128 || target.Type == RuntimeValueType.UnsignedInt128 ||
                target.Type == RuntimeValueType.Decimal || target.Type == RuntimeValueType.Byte ||
                target.Type == RuntimeValueType.List || target.Type == RuntimeValueType.Set ||
                target.Type == RuntimeValueType.Map || target.Type == RuntimeValueType.Tuple ||
                target.Type == RuntimeValueType.Boolean || target.Type == RuntimeValueType.Null)
            {
                if (ExtensionDispatch.TryGetProperty(target, memberName, context, node.PositionStart, node.PositionEnd, out var pExtProp))
                {
                    icSlot.BranchKind = 0;
                    return pExtProp;
                }
                var ext = context.Extensions.ResolveMethodEntries(target, memberName);
                if (ext.Count > 0)
                {
                    icSlot.TargetType = target.Type;
                    icSlot.Shape = target.GetType();
                    icSlot.BranchKind = BR_PRIMITIVE_EXT;
                    icSlot.ExtRegistry = context.Extensions;
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                "Member access is only valid on structs or enum types", context));
        }

        public static RuntimeResult Apply(MemberAccessNode node, Context context, RuntimeValue target)
        {
            var res = new RuntimeResult();
            string memberName = node.MemberTok.Value?.ToString() ?? "";

            if (target.Type == RuntimeValueType.EnumType)
            {
                var enumType = (EnumTypeValue)target;
                if (!enumType.HasMember(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"enum '{enumType.EnumName}' has no member '{memberName}'",
                        context,
                        code: DiagnosticCode.RuntimeUndefinedSymbol,
                        primaryLabel: $"'{memberName}' is not a variant",
                        help: $"available variants: {string.Join(", ", enumType.VariantsByName.Keys)}"));
                return res.Success(enumType.GetMember(memberName));
            }

            if (target.Type == RuntimeValueType.StructInstance || target.Type == RuntimeValueType.RecordInstance)
            {
                var instance = (StructInstanceValue)target;

                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.StructName);
                    return PropertyAccessOps.Get(instance, propDesc, context, node.PositionStart, node.PositionEnd, isInside);
                }

                var evDesc = instance.Definition.GetEvent(memberName);
                if (evDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.StructName);
                    if (!isInside && !evDesc.SubscribeIsPublic)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"event '{evDesc.DeclaringTypeName}.{evDesc.Name}' is not accessible here (subscribe is private)",
                            context));
                    }
                    return res.Success(new EventSubscriptionValue(instance, evDesc)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (instance.HasField(memberName))
                {
                    if (!instance.IsFieldPublic(memberName) && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"field '{memberName}' of struct '{instance.Definition.StructName}' is private",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "accessed from outside the declaring struct",
                            help: "mark the field with 'pub' to expose it, or access it only from within the struct's own methods"));
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                var method = instance.Definition.GetMethod(memberName);
                if (method != null)
                {
                    if (!method.IsPublic && !IsInsideSameType(context, instance.Definition.StructName))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"method '{memberName}' of struct '{instance.Definition.StructName}' is private",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "called from outside the declaring struct",
                            help: "mark the method with 'pub' to expose it"));
                    return res.Success(new BoundStructMethodValue(instance.Definition, instance, method).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (ExtensionDispatch.TryGetField(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtField2))
                    return sExtField2;

                if (ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtEv2))
                    return sExtEv2;

                if (ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var sExtProp2))
                    return sExtProp2;

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"struct '{instance.Definition.StructName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the struct definition / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (ClassInstanceValue)target;

                var propDesc = instance.Definition.GetProperty(memberName);
                if (propDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.ClassName)
                                 || IsInsideClassHierarchy(context, instance.Definition);
                    return PropertyAccessOps.Get(instance, propDesc, context, node.PositionStart, node.PositionEnd, isInside);
                }

                var evDesc = instance.Definition.GetEvent(memberName);
                if (evDesc != null)
                {
                    bool isInside = IsInsideSameType(context, instance.Definition.ClassName)
                                 || IsInsideClassHierarchy(context, instance.Definition);
                    if (!isInside && !evDesc.SubscribeIsPublic)
                    {
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                            $"event '{evDesc.DeclaringTypeName}.{evDesc.Name}' is not accessible here (subscribe is private)",
                            context));
                    }
                    return res.Success(new EventSubscriptionValue(instance, evDesc)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (instance.HasField(memberName))
                    return res.Success(instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var native = instance.Definition.ResolveInstanceMethods(memberName);
                if (native.Count > 0)
                    return res.Success(new BoundClassMethodGroupValue(instance.Definition, instance, native).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                if (ExtensionDispatch.TryGetField(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtField2))
                    return cExtField2;

                if (ExtensionDispatch.TryGetEvent(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtEv2))
                    return cExtEv2;

                if (ExtensionDispatch.TryGetProperty(instance, memberName, context, node.PositionStart, node.PositionEnd, out var cExtProp2))
                    return cExtProp2;

                var ext = context.Extensions.ResolveMethodEntries(instance, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(instance, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{instance.Definition.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such field, method or extension",
                    help: "check the spelling, or add the member to the class / an 'extend' block"));
            }

            if (target.Type == RuntimeValueType.Super)
            {
                var sup = (SuperProxyValue)target;
                if (sup.BaseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        "'super' cannot resolve a base class",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "no base class is in scope here",
                        help: "'super' is only meaningful inside methods of a class that extends another via ':'"));

                if (sup.Instance.HasField(memberName))
                    return res.Success(sup.Instance.GetField(memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                var candidates = sup.BaseClass.ResolveCandidates(memberName);
                if (candidates.Count > 0)
                    return res.Success(new BoundMethodGroupValue(memberName, sup.Instance, sup.BaseClass, candidates).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"base class '{sup.BaseClass.ClassName}' has no member named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such inherited field or method",
                    help: "verify the name and visibility of the inherited member"));
            }

            if (target.Type == RuntimeValueType.ClassType)
            {
                var classType = (ClassTypeValue)target;
                if (classType.HasStaticField(memberName))
                    return res.Success(classType.StaticFields[memberName].SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                if (ExtensionDispatch.TryGetField(classType, memberName, context, node.PositionStart, node.PositionEnd, out var staticExtField2))
                    return staticExtField2;
                // Named constructor (generative or factory): `T.name(...)`.
                // Yields a callable thunk that constructs on invocation.
                if (classType.HasAnyConstructorNamed(memberName))
                    return res.Success(new BoundConstructorValue(classType, memberName).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                if (classType.TryGetStaticMethodOwner(memberName, out var owner, out var method) && method != null)
                    return res.Success(new BoundClassMethodValue(owner, null, method, isStatic: true).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                var evOnType2 = classType.GetEvent(memberName);
                if (evOnType2 != null)
                {
                    if (evOnType2.IsStatic)
                    {
                        bool isInside = IsInsideSameType(context, classType.ClassName)
                                     || IsInsideClassHierarchy(context, classType);
                        if (!isInside && !evOnType2.SubscribeIsPublic)
                        {
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                                $"event '{evOnType2.DeclaringTypeName}.{evOnType2.Name}' is not accessible here (subscribe is private)",
                                context));
                        }
                        return res.Success(new EventSubscriptionValue(classType, evOnType2)
                            .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                    }
                    return res.Success(new EventRefValue(classType, evOnType2)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
                var ctSuggest = classType.SuggestMember(memberName);
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"class '{classType.ClassName}' has no static member or constructor named '{memberName}'",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such static member or named constructor",
                    help: ctSuggest != null
                        ? $"did you mean '{classType.ClassName}.{ctSuggest}'?"
                        : $"check the spelling, or declare '{memberName}' with 'static' inside class '{classType.ClassName}'"));
            }

            // EventSubscriptionValue: synthetic methods (on/off/clear/count).
            if (target.Type == RuntimeValueType.EventSubscription)
            {
                var evSrc = (EventSubscriptionValue)target;
                BoundEventMethodValue? bound = memberName switch
                {
                    "on"    => new BoundEventMethodValue(evSrc, EventMethodKind.On),
                    "off"   => new BoundEventMethodValue(evSrc, EventMethodKind.Off),
                    "clear" => new BoundEventMethodValue(evSrc, EventMethodKind.Clear),
                    "count" => new BoundEventMethodValue(evSrc, EventMethodKind.Count),
                    _ => null
                };
                if (bound != null)
                {
                    bound.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(bound);
                }
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"event '{evSrc.Descriptor.DeclaringTypeName}.{evSrc.Descriptor.Name}' has no method '{memberName}' (available: on, off, clear, count)",
                    context));
            }

            // SubscriptionValue: dispose / is_active.
            if (target.Type == RuntimeValueType.Subscription)
            {
                var sub = (SubscriptionValue)target;
                BoundSubscriptionMethodValue? bound = memberName switch
                {
                    "dispose"   => new BoundSubscriptionMethodValue(sub, SubscriptionMethodKind.Dispose),
                    "is_active" => new BoundSubscriptionMethodValue(sub, SubscriptionMethodKind.IsActive),
                    _ => null
                };
                if (bound != null)
                {
                    bound.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(bound);
                }
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"subscription has no method '{memberName}' (available: dispose, is_active)",
                    context));
            }

            if (target.Type == RuntimeValueType.RecordType)
            {
                var recType = (RaLanguage.Interpreter.Values.Records.RecordTypeValue)target;
                var evRec = recType.GetEvent(memberName);
                if (evRec != null && !evRec.IsStatic)
                {
                    return res.Success(new EventRefValue(recType, evRec)
                        .SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            if (target.Type == RuntimeValueType.Namespace)
            {
                var ns = (NamespaceValue)target;
                var entry = ns.Members.GetLocalEntry(memberName);
                if (entry == null || !entry.IsPublic)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Namespace '{(ns.IsRoot ? "<global>" : ns.QualifiedName)}' has no public member '{memberName}'", context));
                return res.Success(entry.Value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (target.Type == RuntimeValueType.ModuleWrapper)
            {
                var moduleWrapper = (ModuleWrapperValue)target;
                var ext = moduleWrapper.Module.Extensions.ResolveMethodEntries(target, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                return res.Success(moduleWrapper.Module.SymbolTable.Get(memberName));
            }

            // Extension methods on built-in / primitive types.
            if (target.Type == RuntimeValueType.Enum || target.Type == RuntimeValueType.EnumType ||
                target.Type == RuntimeValueType.String || target.Type == RuntimeValueType.Number ||
                target.Type == RuntimeValueType.Integer || target.Type == RuntimeValueType.Long ||
                target.Type == RuntimeValueType.Float || target.Type == RuntimeValueType.Double ||
                target.Type == RuntimeValueType.UnsignedInteger || target.Type == RuntimeValueType.UnsignedLong ||
                target.Type == RuntimeValueType.Short || target.Type == RuntimeValueType.UnsignedShort ||
                target.Type == RuntimeValueType.Int128 || target.Type == RuntimeValueType.UnsignedInt128 ||
                target.Type == RuntimeValueType.Decimal || target.Type == RuntimeValueType.Byte ||
                target.Type == RuntimeValueType.List || target.Type == RuntimeValueType.Set ||
                target.Type == RuntimeValueType.Map || target.Type == RuntimeValueType.Tuple ||
                target.Type == RuntimeValueType.Boolean || target.Type == RuntimeValueType.Null)
            {
                if (ExtensionDispatch.TryGetProperty(target, memberName, context, node.PositionStart, node.PositionEnd, out var pExtProp2))
                    return pExtProp2;

                var ext = context.Extensions.ResolveMethodEntries(target, memberName);
                if (ext.Count > 0)
                    return res.Success(new BoundExtensionMethodGroupValue(target, ext).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                "Member access is only valid on structs or enum types", context));
        }

        private static bool IsInsideSameType(Context context, string typeName)
        {
            var selfEntry = context.SymbolTable!.GetEntry("self");
            if (selfEntry == null) return false;
            if (selfEntry.Value.Type == RuntimeValueType.StructInstance)
                return string.Equals(((StructInstanceValue)selfEntry.Value).Definition.StructName, typeName, System.StringComparison.Ordinal);
            if (selfEntry.Value.Type == RuntimeValueType.ClassInstance)
                return string.Equals(((ClassInstanceValue)selfEntry.Value).Definition.ClassName, typeName, System.StringComparison.Ordinal);
            return false;
        }

        // True when the calling context's `self` is an instance of
        // `decl` or any subclass of it. Properties declared on the base
        // are reachable from a method body that runs on a derived class,
        // matching the visibility model for fields/methods.
        public static bool IsInsideClassHierarchy(Context context, ClassTypeValue decl)
        {
            var selfEntry = context.SymbolTable!.GetEntry("self");
            if (selfEntry == null) return false;
            if (selfEntry.Value.Type != RuntimeValueType.ClassInstance) return false;
            var inst = (ClassInstanceValue)selfEntry.Value;
            return inst.Definition.InheritsFrom(decl.ClassName);
        }
    }
}

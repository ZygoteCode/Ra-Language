using System.Collections.Concurrent;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Global side-table that turns "extend T { var x: int = 0 }" into
    // O(1) array-indexed storage on the receiver. The receiver's
    // hidden-class shape stays immutable — no field is added to its
    // FieldSlots; ext-field state lives in a parallel
    // `RuntimeValue?[] ExtFieldSlots` lazy-allocated on first write.
    //
    // Slot allocation is process-wide and stable. The composite key
    // `(targetName, generic-spec, fieldName)` mints exactly one slot
    // index even when the same `extend` block is re-evaluated across
    // module loads or re-imported through different paths.
    //
    // Memory model:
    //   - An instance with zero ext-fields touched costs ZERO extra
    //     bytes (ExtFieldSlots stays null).
    //   - First write allocates a small array sized to the slot index
    //     in question. Subsequent writes grow geometrically.
    //   - Slot indices are monotonic-increasing across registrations.
    //     `Compact()` is intentionally absent — slots are never
    //     reclaimed; the array per instance only grows up to the
    //     highest slot index a registered ext-field has used on this
    //     instance.
    //
    // Thread safety: slot allocation uses a ConcurrentDictionary +
    // Interlocked counter. Per-instance read / write paths are NOT
    // thread-safe — Ra's runtime is single-threaded inside one
    // VM frame; cross-thread races on the same instance would corrupt
    // native fields too.
    public static class ExtensionFieldStorage
    {
        private static int s_nextSlot;
        private static readonly ConcurrentDictionary<string, int> s_slotMap
            = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, ExtensionFieldDescriptor> s_descriptorsBySlot
            = new();

        // Allocates (or returns) the global slot index for the given
        // ext-field key. Multiple modules registering the same
        // (target, spec, name) collapse onto the same slot.
        public static int AllocateSlot(string targetName, TypeDescriptor? targetType, string fieldName)
        {
            var key = ComposeKey(targetName, targetType, fieldName);
            return s_slotMap.GetOrAdd(key, _ => System.Threading.Interlocked.Increment(ref s_nextSlot) - 1);
        }

        public static void RegisterDescriptor(int slot, ExtensionFieldDescriptor desc)
        {
            // Last-write-wins is intentional: if two modules declare
            // the same (target, spec, name) the descriptor metadata
            // (type, default, mutability) is expected to be congruent
            // — they registered into the same slot anyway. Diverging
            // metadata is a soft bug; the receiver only sees the
            // most-recently-registered descriptor's default + type.
            s_descriptorsBySlot[slot] = desc;
        }

        public static ExtensionFieldDescriptor? GetDescriptor(int slot)
            => s_descriptorsBySlot.TryGetValue(slot, out var d) ? d : null;

        // Reset for menu-driven re-runs. Mirrors how Program clears
        // GlobalSymbolTable and MetadataRegistry between [1]/[2]/[3]
        // cycles in the interactive launcher.
        public static void Reset()
        {
            s_slotMap.Clear();
            s_descriptorsBySlot.Clear();
            s_nextSlot = 0;
        }

        private static string ComposeKey(string targetName, TypeDescriptor? targetType, string fieldName)
        {
            if (targetType == null || targetType.GenericArgs.Count == 0)
                return targetName + "||" + fieldName;
            var sb = new System.Text.StringBuilder(targetName);
            sb.Append('|');
            for (int i = 0; i < targetType.GenericArgs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(targetType.GenericArgs[i].Name);
            }
            sb.Append('|').Append(fieldName);
            return sb.ToString();
        }

        // -----------------------------------------------------------
        //  Per-instance slot access
        // -----------------------------------------------------------

        public static RuntimeValue?[] GetOrCreateSlots(RuntimeValue instance, int minCapacity)
        {
            if (instance is ClassInstanceValue ci)
            {
                if (ci.ExtFieldSlots == null || ci.ExtFieldSlots.Length <= minCapacity)
                    ci.ExtFieldSlots = Grow(ci.ExtFieldSlots, minCapacity + 1);
                return ci.ExtFieldSlots!;
            }
            if (instance is StructInstanceValue si)
            {
                if (si.ExtFieldSlots == null || si.ExtFieldSlots.Length <= minCapacity)
                    si.ExtFieldSlots = Grow(si.ExtFieldSlots, minCapacity + 1);
                return si.ExtFieldSlots!;
            }
            if (instance is Values.Primitives.ClassTypeValue ct)
            {
                if (ct.StaticExtFieldSlots == null || ct.StaticExtFieldSlots.Length <= minCapacity)
                    ct.StaticExtFieldSlots = Grow(ct.StaticExtFieldSlots, minCapacity + 1);
                return ct.StaticExtFieldSlots!;
            }
            throw new InvalidOperationException(
                "ExtensionFieldStorage requires a ClassInstanceValue, StructInstanceValue or ClassTypeValue receiver");
        }

        public static RuntimeValue?[]? GetSlotsOrNull(RuntimeValue instance)
        {
            if (instance is ClassInstanceValue ci) return ci.ExtFieldSlots;
            if (instance is StructInstanceValue si) return si.ExtFieldSlots;
            if (instance is Values.Primitives.ClassTypeValue ct) return ct.StaticExtFieldSlots;
            return null;
        }

        // Tracks "this slot has been written at least once" — used by
        // const/let/final mutability gates. Stored as a bitset to keep
        // memory bounded.
        public static bool MarkInitialized(RuntimeValue instance, int slot)
        {
            var bits = GetOrCreateInitBits(instance, slot);
            int wordIndex = slot >> 6;
            ulong mask = 1UL << (slot & 63);
            bool was = (bits[wordIndex] & mask) != 0UL;
            bits[wordIndex] |= mask;
            return !was; // returns true on first init
        }

        public static bool IsInitialized(RuntimeValue instance, int slot)
        {
            ulong[]? bits = null;
            if (instance is ClassInstanceValue ci) bits = ci.ExtFieldInitBits;
            else if (instance is StructInstanceValue si) bits = si.ExtFieldInitBits;
            else if (instance is Values.Primitives.ClassTypeValue ct) bits = ct.StaticExtFieldInitBits;
            if (bits == null) return false;
            int wordIndex = slot >> 6;
            if (wordIndex >= bits.Length) return false;
            return (bits[wordIndex] & (1UL << (slot & 63))) != 0UL;
        }

        // Re-entrancy guard for lazy ext-fields. Lives in a parallel
        // bitset so the regular initialised bit can light up only at
        // the end of the default eval (post-recursion check).
        public static bool IsLazyInitializing(RuntimeValue instance, int slot)
        {
            ulong[]? bits = null;
            if (instance is ClassInstanceValue ci) bits = ci.ExtFieldLazyBits;
            else if (instance is StructInstanceValue si) bits = si.ExtFieldLazyBits;
            else if (instance is Values.Primitives.ClassTypeValue ct) bits = ct.StaticExtFieldLazyBits;
            if (bits == null) return false;
            int wordIndex = slot >> 6;
            if (wordIndex >= bits.Length) return false;
            return (bits[wordIndex] & (1UL << (slot & 63))) != 0UL;
        }

        public static void MarkLazyInitializing(RuntimeValue instance, int slot, bool flag)
        {
            int words = (slot >> 6) + 1;
            ulong[]? bits = null;
            if (instance is ClassInstanceValue ci)
            {
                if (ci.ExtFieldLazyBits == null || ci.ExtFieldLazyBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (ci.ExtFieldLazyBits != null)
                        System.Array.Copy(ci.ExtFieldLazyBits, grown, ci.ExtFieldLazyBits.Length);
                    ci.ExtFieldLazyBits = grown;
                }
                bits = ci.ExtFieldLazyBits!;
            }
            else if (instance is StructInstanceValue si)
            {
                if (si.ExtFieldLazyBits == null || si.ExtFieldLazyBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (si.ExtFieldLazyBits != null)
                        System.Array.Copy(si.ExtFieldLazyBits, grown, si.ExtFieldLazyBits.Length);
                    si.ExtFieldLazyBits = grown;
                }
                bits = si.ExtFieldLazyBits!;
            }
            else if (instance is Values.Primitives.ClassTypeValue ct)
            {
                if (ct.StaticExtFieldLazyBits == null || ct.StaticExtFieldLazyBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (ct.StaticExtFieldLazyBits != null)
                        System.Array.Copy(ct.StaticExtFieldLazyBits, grown, ct.StaticExtFieldLazyBits.Length);
                    ct.StaticExtFieldLazyBits = grown;
                }
                bits = ct.StaticExtFieldLazyBits!;
            }
            if (bits == null) return;
            int wordIndex = slot >> 6;
            ulong mask = 1UL << (slot & 63);
            if (flag) bits[wordIndex] |= mask;
            else bits[wordIndex] &= ~mask;
        }

        private static ulong[] GetOrCreateInitBits(RuntimeValue instance, int slot)
        {
            int words = (slot >> 6) + 1;
            if (instance is ClassInstanceValue ci)
            {
                if (ci.ExtFieldInitBits == null || ci.ExtFieldInitBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (ci.ExtFieldInitBits != null)
                        System.Array.Copy(ci.ExtFieldInitBits, grown, ci.ExtFieldInitBits.Length);
                    ci.ExtFieldInitBits = grown;
                }
                return ci.ExtFieldInitBits!;
            }
            if (instance is StructInstanceValue si)
            {
                if (si.ExtFieldInitBits == null || si.ExtFieldInitBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (si.ExtFieldInitBits != null)
                        System.Array.Copy(si.ExtFieldInitBits, grown, si.ExtFieldInitBits.Length);
                    si.ExtFieldInitBits = grown;
                }
                return si.ExtFieldInitBits!;
            }
            if (instance is Values.Primitives.ClassTypeValue ct)
            {
                if (ct.StaticExtFieldInitBits == null || ct.StaticExtFieldInitBits.Length < words)
                {
                    var grown = new ulong[NextPow2(words)];
                    if (ct.StaticExtFieldInitBits != null)
                        System.Array.Copy(ct.StaticExtFieldInitBits, grown, ct.StaticExtFieldInitBits.Length);
                    ct.StaticExtFieldInitBits = grown;
                }
                return ct.StaticExtFieldInitBits!;
            }
            throw new InvalidOperationException(
                "ExtensionFieldStorage requires a ClassInstanceValue, StructInstanceValue or ClassTypeValue receiver");
        }

        private static RuntimeValue?[] Grow(RuntimeValue?[]? existing, int neededCapacity)
        {
            int newSize = NextPow2(neededCapacity);
            var grown = new RuntimeValue?[newSize];
            if (existing != null) System.Array.Copy(existing, grown, existing.Length);
            return grown;
        }

        private static int NextPow2(int v)
        {
            if (v <= 4) return 4;
            int p = 4;
            while (p < v) p <<= 1;
            return p;
        }
    }

    // Runtime-side image of an extension field. Mirrors
    // PropertyDescriptor in spirit. Carries everything dispatch needs
    // without re-walking the AST.
    public sealed class ExtensionFieldDescriptor
    {
        public string Name { get; }
        public TypeDescriptor? FieldType { get; }
        public AstNode? DefaultValueNode { get; }
        public bool IsPublic { get; }
        public bool IsStaticField { get; }
        public bool IsLazy { get; }
        public VariableDeclarationType DeclarationType { get; }
        public string DeclaringTypeName { get; }
        public int SlotIndex { get; }
        public StructFieldDefinitionNode SourceNode { get; }

        public bool IsConst => DeclarationType == VariableDeclarationType.CONST;
        public bool IsFinal => DeclarationType == VariableDeclarationType.FINAL;
        public bool IsLet => DeclarationType == VariableDeclarationType.LET;
        public bool IsMutable => DeclarationType == VariableDeclarationType.VARIABLE;

        public ExtensionFieldDescriptor(
            StructFieldDefinitionNode source,
            string declaringTypeName,
            int slotIndex,
            bool isStaticField = false,
            bool isLazy = false)
        {
            SourceNode = source;
            Name = source.NameTok.Value?.ToString() ?? "";
            FieldType = source.FieldType;
            DefaultValueNode = source.DefaultValueNode;
            IsPublic = source.IsPublic;
            IsStaticField = isStaticField;
            IsLazy = isLazy;
            DeclarationType = source.DeclarationType;
            DeclaringTypeName = declaringTypeName;
            SlotIndex = slotIndex;
        }
    }
}

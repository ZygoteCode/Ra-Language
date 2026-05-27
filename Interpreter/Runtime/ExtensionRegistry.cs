using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Registry holding extension members for a single logical scope
    // (the global program scope or one per loaded module). Each entry
    // tracks declaring module + IsLocal so resolution prefers the
    // importer's own extensions over those pulled in from imports.
    //
    // Lookup priority (highest first):
    //   1. Local entries on most-derived candidate target with matching
    //      generic specialization.
    //   2. Local entries on each progressively base target.
    //   3. Imported entries on most-derived target.
    //   4. Imported entries on each progressively base target.
    //
    // Within each tier, methods/operators/indexers are returned in
    // registration order (deterministic). Properties/events return the
    // first hit.
    public class ExtensionRegistry
    {
        private readonly Dictionary<string, List<ExtensionMethodEntry>> _methods
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExtensionPropertyEntry>> _properties
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExtensionOperatorEntry>> _operators
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExtensionIndexerEntry>> _indexers
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExtensionEventEntry>> _events
            = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExtensionFieldEntry>> _fields
            = new(StringComparer.Ordinal);

        // Targets marked @sealed. Any subsequent ext declaration on a
        // sealed target is rejected at registration time. Survives
        // import merge — once sealed in any merged source, the target
        // stays sealed in the target.
        private readonly HashSet<string> _sealedTargets
            = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, List<ExtensionMethodEntry>> AllMethodEntries => _methods;
        public IReadOnlyDictionary<string, List<ExtensionPropertyEntry>> AllPropertyEntries => _properties;
        public IReadOnlyDictionary<string, List<ExtensionOperatorEntry>> AllOperatorEntries => _operators;
        public IReadOnlyDictionary<string, List<ExtensionIndexerEntry>> AllIndexerEntries => _indexers;
        public IReadOnlyDictionary<string, List<ExtensionEventEntry>> AllEventEntries => _events;
        public IReadOnlyDictionary<string, List<ExtensionFieldEntry>> AllFieldEntries => _fields;
        public IReadOnlyCollection<string> SealedTargets => _sealedTargets;

        // Compat shim — older call-sites walk the raw method list.
        public IReadOnlyDictionary<string, List<FunctionDefinitionNode>> AllMethods
        {
            get
            {
                var view = new Dictionary<string, List<FunctionDefinitionNode>>(StringComparer.Ordinal);
                foreach (var kv in _methods)
                {
                    var l = new List<FunctionDefinitionNode>(kv.Value.Count);
                    foreach (var entry in kv.Value) l.Add(entry.Method);
                    view[kv.Key] = l;
                }
                return view;
            }
        }

        public bool IsSealed(string targetTypeName)
            => _sealedTargets.Contains(targetTypeName);

        public bool MarkSealed(string targetTypeName)
        {
            if (string.IsNullOrEmpty(targetTypeName)) return false;
            return _sealedTargets.Add(targetTypeName);
        }

        public void Register(string targetTypeName, FunctionDefinitionNode method)
            => RegisterMethod(targetTypeName, method, isBlockPublic: true, isLocal: true, declaringModule: null, targetType: null);

        public void RegisterMethod(
            string targetTypeName,
            FunctionDefinitionNode method,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            if (!_methods.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionMethodEntry>();
                _methods[targetTypeName] = list;
            }
            list.Add(new ExtensionMethodEntry(method, isBlockPublic, isLocal, declaringModule, targetType));
        }

        public bool RegisterProperty(
            string targetTypeName,
            PropertyDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            out string? error,
            TypeDescriptor? targetType = null)
        {
            error = null;
            if (!_properties.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionPropertyEntry>();
                _properties[targetTypeName] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Descriptor.Name, descriptor.Name, StringComparison.Ordinal)
                    && SameTargetSpec(list[i].TargetType, targetType))
                {
                    if (list[i].IsLocal && isLocal)
                    {
                        error = $"extension property '{descriptor.Name}' on '{targetTypeName}' is declared more than once";
                        return false;
                    }
                    return true;
                }
            }

            list.Add(new ExtensionPropertyEntry(descriptor, isBlockPublic, isLocal, declaringModule, targetType));
            return true;
        }

        public void RegisterOperator(
            string targetTypeName,
            OperatorDefinitionNode op,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            if (!_operators.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionOperatorEntry>();
                _operators[targetTypeName] = list;
            }
            list.Add(new ExtensionOperatorEntry(op, isBlockPublic, isLocal, declaringModule, targetType));
        }

        public void RegisterIndexer(
            string targetTypeName,
            ExtensionIndexerEntry entry)
        {
            if (!_indexers.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionIndexerEntry>();
                _indexers[targetTypeName] = list;
            }
            list.Add(entry);
        }

        public bool RegisterField(
            string targetTypeName,
            ExtensionFieldDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            out string? error,
            TypeDescriptor? targetType = null)
        {
            error = null;
            if (!_fields.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionFieldEntry>();
                _fields[targetTypeName] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Descriptor.Name, descriptor.Name, StringComparison.Ordinal)
                    && SameTargetSpec(list[i].TargetType, targetType))
                {
                    if (list[i].IsLocal && isLocal)
                    {
                        error = $"extension field '{descriptor.Name}' on '{targetTypeName}' is declared more than once";
                        return false;
                    }
                    return true;
                }
            }

            list.Add(new ExtensionFieldEntry(descriptor, isBlockPublic, isLocal, declaringModule, targetType));
            return true;
        }

        public ExtensionFieldEntry? ResolveFieldEntry(RuntimeValue receiver, string memberName)
        {
            // Static fields surface only on a ClassType receiver
            // (the type itself), instance fields only on an actual
            // instance. The descriptor's IsStaticField flag is the
            // sole source of truth for this partition — the registry
            // never mixes them on the same dispatch.
            bool wantStatic = receiver.Type == RuntimeValueType.ClassType;

            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_fields.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (entry.Descriptor.IsStaticField != wantStatic) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;
                        if (string.Equals(entry.Descriptor.Name, memberName, StringComparison.Ordinal))
                            return entry;
                    }
                }
            }
            return null;
        }

        public bool RegisterEvent(
            string targetTypeName,
            EventDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            out string? error,
            TypeDescriptor? targetType = null)
        {
            error = null;
            if (!_events.TryGetValue(targetTypeName, out var list))
            {
                list = new List<ExtensionEventEntry>();
                _events[targetTypeName] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Descriptor.Name, descriptor.Name, StringComparison.Ordinal)
                    && SameTargetSpec(list[i].TargetType, targetType))
                {
                    if (list[i].IsLocal && isLocal)
                    {
                        error = $"extension event '{descriptor.Name}' on '{targetTypeName}' is declared more than once";
                        return false;
                    }
                    return true;
                }
            }

            list.Add(new ExtensionEventEntry(descriptor, isBlockPublic, isLocal, declaringModule, targetType));
            return true;
        }

        // -----------------------------------------------------------
        //  Resolution
        // -----------------------------------------------------------

        // Method resolution. Ranked candidates honor local-first +
        // derived→base + generic specialization (entries with declared
        // generic args only match receivers whose runtime
        // GenericBindings line up).
        public List<FunctionDefinitionNode> Resolve(RuntimeValue receiver, string memberName)
        {
            var result = new List<FunctionDefinitionNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_methods.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;

                        var mName = entry.Method.VarNameTok?.Value?.ToString() ?? "";
                        if (!string.Equals(mName, memberName, StringComparison.Ordinal))
                            continue;

                        var sig = Values.Traits.MethodSignature.KeyOf(entry.Method);
                        if (seen.Add(sig))
                            result.Add(entry.Method);
                    }
                }
            }

            return result;
        }

        // Same as Resolve but returns full entries — needed by the
        // ambiguity diagnostic to surface declaring modules.
        public List<ExtensionMethodEntry> ResolveMethodEntries(RuntimeValue receiver, string memberName)
        {
            var result = new List<ExtensionMethodEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_methods.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;

                        var mName = entry.Method.VarNameTok?.Value?.ToString() ?? "";
                        if (!string.Equals(mName, memberName, StringComparison.Ordinal))
                            continue;

                        var sig = Values.Traits.MethodSignature.KeyOf(entry.Method);
                        if (seen.Add(sig))
                            result.Add(entry);
                    }
                }
            }

            return result;
        }

        public PropertyDescriptor? ResolveProperty(RuntimeValue receiver, string memberName)
            => ResolvePropertyEntry(receiver, memberName)?.Descriptor;

        public ExtensionPropertyEntry? ResolvePropertyEntry(RuntimeValue receiver, string memberName)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_properties.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;
                        if (string.Equals(entry.Descriptor.Name, memberName, StringComparison.Ordinal))
                            return entry;
                    }
                }
            }
            return null;
        }

        // Operator resolution. Matches by operator token type and the
        // rhs parameter type name (mirroring ClassTypeValue.ResolveOperator).
        public ExtensionOperatorEntry? ResolveOperatorEntry(RuntimeValue receiver, TokenType opType, string rhsTypeName)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_operators.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;
                        if (entry.Operator.OperatorTok.Type != opType) continue;

                        var paramType = entry.Operator.ArgType?.Name ?? "";
                        if (string.Equals(paramType, rhsTypeName, StringComparison.Ordinal)
                            || string.Equals(paramType, "any", StringComparison.Ordinal))
                        {
                            return entry;
                        }
                    }
                }
            }
            return null;
        }

        public ExtensionIndexerEntry? ResolveIndexerEntry(RuntimeValue receiver, bool isAssignment)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_indexers.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;
                        if (entry.IsSetter != isAssignment) continue;
                        return entry;
                    }
                }
            }
            return null;
        }

        public ExtensionEventEntry? ResolveEventEntry(RuntimeValue receiver, string eventName)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantLocal = pass == 0;
                foreach (var typeKey in GetCandidateTypeKeys(receiver))
                {
                    if (!_events.TryGetValue(typeKey, out var list))
                        continue;

                    foreach (var entry in list)
                    {
                        if (entry.IsLocal != wantLocal) continue;
                        if (!MatchesGenericTarget(entry.TargetType, receiver)) continue;
                        if (string.Equals(entry.Descriptor.Name, eventName, StringComparison.Ordinal))
                            return entry;
                    }
                }
            }
            return null;
        }

        // -----------------------------------------------------------
        //  Generic matching
        // -----------------------------------------------------------

        // Returns true when the declared target type's generic args
        // line up with the receiver's runtime type bindings. An entry
        // declared as `extend Box` (no generic args) always matches;
        // `extend Box<int>` only matches when the receiver's Box
        // instance was constructed with `int` as its first type
        // parameter.
        //
        // For non-class receivers (primitives, structs without
        // recorded generic bindings) any entry without generic args
        // matches; entries with generic args fall through and miss.
        private static bool MatchesGenericTarget(TypeDescriptor? declaredTarget, RuntimeValue receiver)
        {
            if (declaredTarget == null || declaredTarget.GenericArgs.Count == 0)
                return true;

            if (receiver.Type == RuntimeValueType.ClassInstance)
            {
                var inst = (ClassInstanceValue)receiver;
                var classDef = inst.Definition;
                // Walk type-parameter names declared on the class
                // definition; for each, match the corresponding
                // GenericBindings entry against declaredTarget.GenericArgs.
                var paramNames = classDef.GenericTypeParams;
                if (paramNames == null || paramNames.Count == 0) return false;
                if (declaredTarget.GenericArgs.Count > paramNames.Count) return false;

                for (int i = 0; i < declaredTarget.GenericArgs.Count; i++)
                {
                    var paramName = paramNames[i];
                    if (!inst.GenericBindings.TryGetValue(paramName, out var bound)) return false;
                    if (!string.Equals(bound.Name, declaredTarget.GenericArgs[i].Name, StringComparison.Ordinal))
                        return false;
                }
                return true;
            }

            return false;
        }

        // Two extension TargetType specs match when both omit
        // generics, or both share the same generic arg names in
        // declaration order. Used to detect duplicate properties on
        // the same generic specialization without conflating
        // `extend Box<int>` with `extend Box<string>`.
        private static bool SameTargetSpec(TypeDescriptor? a, TypeDescriptor? b)
        {
            bool aHas = a != null && a.GenericArgs.Count > 0;
            bool bHas = b != null && b.GenericArgs.Count > 0;
            if (aHas != bHas) return false;
            if (!aHas) return true;
            if (a!.GenericArgs.Count != b!.GenericArgs.Count) return false;
            for (int i = 0; i < a.GenericArgs.Count; i++)
                if (!string.Equals(a.GenericArgs[i].Name, b.GenericArgs[i].Name, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private IEnumerable<string> GetCandidateTypeKeys(RuntimeValue receiver)
        {
            if (receiver.Type == RuntimeValueType.ClassInstance)
            {
                var current = ((ClassInstanceValue)receiver).Definition;
                while (current != null)
                {
                    yield return current.ClassName;
                    current = current.BaseClass;
                }

                yield break;
            }

            if (receiver.Type == RuntimeValueType.StructInstance)
            {
                yield return ((StructInstanceValue)receiver).Definition.StructName;
                yield break;
            }

            // For ClassType receivers (static ext-fields on the type
            // itself) walk the class definition chain, same as the
            // instance case. `ClassName` is the registry key the
            // declaring `extend T { static var X }` block used.
            if (receiver.Type == RuntimeValueType.ClassType)
            {
                var current = (ClassTypeValue)receiver;
                while (current != null)
                {
                    yield return current.ClassName;
                    current = current.BaseClass;
                }
                yield break;
            }

            if (receiver.Type == RuntimeValueType.Enum || receiver.Type == RuntimeValueType.EnumType)
            {
                yield return TypeSystem.GetExtensionTargetName(receiver);
                yield break;
            }

            yield return TypeSystem.GetExtensionTargetName(receiver);
        }
    }

    // A single registered extension method.
    public sealed class ExtensionMethodEntry
    {
        public FunctionDefinitionNode Method { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Method.IsPublic;

        public ExtensionMethodEntry(
            FunctionDefinitionNode method,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Method = method;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }

    public sealed class ExtensionPropertyEntry
    {
        public PropertyDescriptor Descriptor { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Descriptor.IsPublic;

        public ExtensionPropertyEntry(
            PropertyDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Descriptor = descriptor;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }

    public sealed class ExtensionOperatorEntry
    {
        public OperatorDefinitionNode Operator { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Operator.IsPublic;

        public ExtensionOperatorEntry(
            OperatorDefinitionNode op,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Operator = op;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }

    // Extension indexer — a get-shape or set-shape callable bound by
    // `operator[]` (read) or `operator[]=` (write). Both shapes are
    // distinct entries so resolution can pick the right one given the
    // call site (read vs assignment).
    public sealed class ExtensionIndexerEntry
    {
        public FunctionDefinitionNode Method { get; }
        public bool IsSetter { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Method.IsPublic;

        public ExtensionIndexerEntry(
            FunctionDefinitionNode method,
            bool isSetter,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Method = method;
            IsSetter = isSetter;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }

    public sealed class ExtensionEventEntry
    {
        public EventDescriptor Descriptor { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Descriptor.IsPublic;

        public ExtensionEventEntry(
            EventDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Descriptor = descriptor;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }

    public sealed class ExtensionFieldEntry
    {
        public ExtensionFieldDescriptor Descriptor { get; }
        public bool IsBlockPublic { get; }
        public bool IsLocal { get; set; }
        public string? DeclaringModule { get; }
        public TypeDescriptor? TargetType { get; }

        public bool IsEffectivelyPublic => IsBlockPublic || Descriptor.IsPublic;

        public ExtensionFieldEntry(
            ExtensionFieldDescriptor descriptor,
            bool isBlockPublic,
            bool isLocal,
            string? declaringModule,
            TypeDescriptor? targetType = null)
        {
            Descriptor = descriptor;
            IsBlockPublic = isBlockPublic;
            IsLocal = isLocal;
            DeclaringModule = declaringModule;
            TargetType = targetType;
        }
    }
}

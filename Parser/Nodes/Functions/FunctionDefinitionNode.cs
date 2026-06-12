using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public sealed class FunctionDefinitionNode : AstNode, ICallableMethodDefinition
    {
        // Resolver output. FrameId is the 16-bit identifier of this function's
        // frame, ParamBindings[i] is the slot allocated to parameter i, and
        // ResolvedCaptures lists every outer binding the body actually
        // references — the static capture set that closure machinery can use to
        // materialise upvalues without scanning the implicit lexical chain.
        public int FrameId = -1;
        public BindingId[]? ParamBindings;
        public List<ResolvedCapture>? ResolvedCaptures;

        // PERF (direct-slot method dispatch): set by the Resolver when this is a
        // method frame (slot 0 reserved for `self`). The IR compiler reserves
        // frame slot 0 so the method fast path can bind `self` there even for
        // zero-arg / zero-local methods (pure getters), whose SlotCount would
        // otherwise be 0.
        public bool ReservesSelfSlot;

        // M17: cached IR compile of the body. Populated lazily by any caller
        // that wants to dispatch the body through VmExecutor (top-level
        // functions via FunctionDefinitionHelper, class methods via
        // BoundClassMethodValue / MethodCallBinder, struct methods via
        // BoundStructMethodValue, etc.). Sentinel `IrCompileTried = true`
        // distinguishes "not yet attempted" from "attempted and failed".
        public Interpreter.IR.RaFunction? CompiledBody;
        public bool IrCompileTried;

        // M31 (A5): cached metadata-target key computed by
        // AnnotationInterceptors.ResolveCalleeMetadataKey on the first
        // invocation. `MetadataTarget.BuildKey(kind, className, methodName)`
        // builds a fresh string per call — eliminating it on the dispatch hot
        // path eliminates the allocation. Tuple of (className lookup result,
        // cached key): className is captured at the BoundClassMethodValue
        // construction site so storing only the resolved key here is safe.
        // Set lazily; null = not yet computed.
        public string? CachedMetadataKey;
        public Token? VarNameTok { get; }
        public List<Token> ArgNameToks { get; }

        // Cached projection of ArgNameToks → string. Was rebuilt on every call
        // via `ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList()` from
        // FunctionValue, BoundClassMethodValue, BoundClassMethodGroupValue,
        // CallableBinder. With this single immutable cache, the projection is
        // paid once at parse time and every dispatch path uses the same list.
        private List<string>? _argNamesCache;
        public List<string> ArgNames
        {
            get
            {
                var cache = _argNamesCache;
                if (cache != null) return cache;
                cache = new List<string>(ArgNameToks.Count);
                for (int i = 0; i < ArgNameToks.Count; i++)
                    cache.Add(ArgNameToks[i].Value?.ToString() ?? "");
                _argNamesCache = cache;
                return cache;
            }
        }

        public List<TypeDescriptor?> ArgTypes { get; }
        public List<bool> IsRefParams { get; }
        public List<AstNode?> ParamDefaults { get; }
        public List<List<AnnotationApplicationNode>?> ParamAnnotations { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public List<AnnotationApplicationNode>? VarArgAnnotations { get; set; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode? BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }
        public bool IsPublic { get; }
        public bool IsConstructor { get; }
        public bool IsOverride { get; }
        public bool IsAbstract { get; }
        public bool IsStatic { get; }
        public bool IsAsync { get; set; }
        public bool IsAsyncStream { get; set; }

        // Constructors v1 — generative / named / factory.
        // `IsConstructor` keeps its original meaning: a *generative*
        // constructor (self-bound, no `return`). A factory constructor is
        // `IsConstructor == false && IsFactory == true` (no self, must
        // return a value assignable to the enclosing type). Redirection is
        // expressed by a forwarding factory (`factory T.x() => T(...)`), so
        // no dedicated redirect node is needed.
        // `ConstructorName` is null for the unnamed form `T(...)` and the
        // bare name for the dotted form `T.name(...)`.
        public bool IsFactory { get; set; }
        public string? ConstructorName { get; set; }

        // Predicates v1. `IsPredicate` marks a `pred` declaration / `pred(...)`
        // literal — a first-class boolean function. The node is an ordinary
        // FunctionDefinitionNode (VarNameTok null = anonymous, same as
        // lambdas); the only difference is the marker and a `bool` return
        // contract. FunctionDefinitionHelper.Apply wraps the produced
        // FunctionValue in a PredicateValue so composition operators
        // (`&` / `|` / `!`) and the narrowing analyzer recognise it.
        public bool IsPredicate { get; set; }

        // Narrowing (user-defined type guard). When the predicate body is
        // exactly `param is T` / `param is not T`, the parser records the
        // refined parameter + tested type here so the NarrowingAnalyzer can
        // flow-type a call `p(v)` like an inline `v is T`. Null = not a
        // narrowing guard. Populated by DetectNarrowingGuard.
        public string? NarrowsParamName { get; set; }
        public TypeDescriptor? NarrowsToType { get; set; }
        public bool NarrowsNegated { get; set; }

        // True for any constructor flavour (generative, named or factory).
        public bool IsAnyConstructor => IsConstructor || IsFactory;

        // Explicit closure-capture specification. `null` means "no capture
        // clause present" — the function uses the legacy implicit lexical
        // closure (every parent binding is reachable via the parent scope
        // chain). When non-null, the closure binds ONLY the listed names
        // into its execution scope, each materialised according to its
        // CaptureMode (value / ref / move). The lexical chain is still
        // available for top-level / namespace lookups so calls to sibling
        // functions continue to work, but the listed names shadow it.
        public List<CaptureSpec>? CaptureList { get; }

        Token? ICallableMethodDefinition.NameTok => VarNameTok;
        List<bool> ICallableMethodDefinition.IsRefParams => IsRefParams;
        bool ICallableMethodDefinition.HasBody => BodyNode != null && !IsAbstract;
        bool ICallableMethodDefinition.IsAbstract => IsAbstract;
        bool ICallableMethodDefinition.IsOverride => IsOverride;
        bool ICallableMethodDefinition.IsConstructor => IsConstructor;
        bool ICallableMethodDefinition.ShouldAutoReturn => ShouldAutoReturn;
        AstNode? ICallableMethodDefinition.BodyNode => BodyNode;

        public FunctionDefinitionNode(
            Token? varNameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<bool> isRefParams,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            AstNode? bodyNode,
            bool shouldAutoReturn,
            List<string>? genericTypeParams = null,
            bool isPublic = false,
            bool isConstructor = false,
            bool isOverride = false,
            bool isAbstract = false,
            bool isStatic = false,
            List<WhereConstraintNode>? whereConstraints = null,
            List<List<AnnotationApplicationNode>?>? paramAnnotations = null,
            List<CaptureSpec>? captureList = null
        ) : base(AstNodeType.FunctionDefinition)
        {
            VarNameTok = varNameTok;
            ArgNameToks = argNameToks ?? new List<Token>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            IsRefParams = isRefParams ?? new List<bool>();
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            ParamAnnotations = paramAnnotations ?? new List<List<AnnotationApplicationNode>?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            IsPublic = isPublic;
            IsConstructor = isConstructor;
            IsOverride = isOverride;
            IsAbstract = isAbstract || bodyNode == null;
            IsStatic = isStatic;
            CaptureList = captureList;

            if (varNameTok != null) PositionStart = varNameTok.Value.PositionStart;
            else if (ArgNameToks.Count > 0) PositionStart = ArgNameToks[0].PositionStart;
            else if (bodyNode != null) PositionStart = bodyNode.PositionStart;

            PositionEnd = bodyNode?.PositionEnd ?? PositionStart;
        }
    }
}

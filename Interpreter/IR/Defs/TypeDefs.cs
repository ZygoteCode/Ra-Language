using System;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.IR.Defs
{
    // L5 — flat, serializable definition descriptors. A `TypeDef` captures
    // everything a one-shot definition's runtime registration needs, with NO
    // AST reference, so the `.rac` archive can store it as plain data (no
    // AstNodeSerializer round-trip) and the VM can register the type from
    // bytecode alone. Built once by IrCompiler at compile time; the matching
    // `OP_DEFINE_TYPE` handler reconstructs the runtime type from it.
    //
    // The pool (`RaFunction.TypeDefs`) is polymorphic; `Kind` tags each entry
    // for the serializer + the dispatch in the VM handler. One kind is wired so
    // far (Enum); the remaining one-shot kinds (class/struct/record/interface/
    // trait/extension/annotation/delegate) slot in behind the same machinery.
    public enum TypeDefKind : byte
    {
        Enum = 1,
        Delegate = 2,
        Using = 3,
        Struct = 4,
        Record = 5,
        Class = 6,
        Trait = 7,
        Extension = 8,
        Interface = 9,
        Annotation = 10,
        Import = 11,
        Namespace = 12,
    }

    // Which import form an ImportDef carries (mirrors the ImportNode subclasses).
    public enum ImportDefKind : byte
    {
        All = 1,        // `import x` / `import x.*`
        Selective = 2,  // `import x.{a, b}`
        Alias = 3,      // `import x as y`
    }

    public abstract class TypeDef
    {
        public abstract TypeDefKind Kind { get; }
    }

    // A single enum variant, fully resolved: its name, declaration ordinal, the
    // (constant-folded) underlying integer value, and the payload tuple types
    // (empty for a plain variant). No AST — the value expression was folded at
    // compile time; a non-constant value makes IrCompiler fall back to the
    // visitor instead of producing an EnumDef.
    public sealed class EnumVariantDef
    {
        public readonly string Name;
        public readonly int Ordinal;
        public readonly Int128 Value;
        public readonly TypeDescriptor[] PayloadTypes; // never null; empty = no payload

        public EnumVariantDef(string name, int ordinal, Int128 value, TypeDescriptor[] payloadTypes)
        {
            Name = name;
            Ordinal = ordinal;
            Value = value;
            PayloadTypes = payloadTypes ?? Array.Empty<TypeDescriptor>();
        }
    }

    public sealed class EnumDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Enum;

        public readonly string Name;
        public readonly string[] Generics;       // never null; empty = non-generic
        public readonly EnumVariantDef[] Variants;

        public EnumDef(string name, string[] generics, EnumVariantDef[] variants)
        {
            Name = name;
            Generics = generics ?? Array.Empty<string>();
            Variants = variants ?? Array.Empty<EnumVariantDef>();
        }
    }

    // `delegate Name = fn(...) -> R` — a structural function-type alias. Pure
    // flat metadata: the structural signature is a TypeDescriptor, the rest are
    // names / a flag. No values, no bodies. (Where-constraints, if present,
    // make IrCompiler fall back to the visitor — they carry AST.)
    public sealed class DelegateDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Delegate;

        public readonly string Name;
        public readonly TypeDescriptor Signature;
        public readonly string[] Generics;
        public readonly bool IsPublic;

        public DelegateDef(string name, TypeDescriptor signature, string[] generics, bool isPublic)
        {
            Name = name;
            Signature = signature;
            Generics = generics ?? Array.Empty<string>();
            IsPublic = isPublic;
        }
    }

    // `using a.b.c` / `using a.b.c as alias` — a one-shot directive (not a type:
    // it resolves a namespace at runtime and injects its public members, or
    // binds the alias). Pure flat metadata: the dotted path segments + optional
    // alias. The handler runs the SAME NamespaceRegistry resolve + inject the
    // visitor does. (Reuses the OP_DEFINE_TYPE machinery; the `Using` kind tag
    // distinguishes it from real type definitions.)
    public sealed class UsingDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Using;

        public readonly string[] Segments;
        public readonly string? Alias; // null = no alias

        public UsingDef(string[] segments, string? alias)
        {
            Segments = segments ?? Array.Empty<string>();
            Alias = alias;
        }
    }

    // L5e — a struct field, fully flat (no AST). Const-foldable defaults are
    // captured as a const RuntimeValue (null = no default); a non-constant
    // default makes IrCompiler fall back to the visitor.
    public sealed class StructFieldDef
    {
        public readonly string Name;
        public readonly TypeDescriptor? FieldType;
        public readonly bool IsPublic;
        public readonly bool IsStatic;
        public readonly bool IsAbstract;
        public readonly bool IsOverride;
        public readonly int DeclKind;                  // (int)VariableDeclarationType
        public readonly Values.RuntimeValue? DefaultConst; // folded literal default, or null

        public StructFieldDef(string name, TypeDescriptor? fieldType, bool isPublic, bool isStatic,
            bool isAbstract, bool isOverride, int declKind, Values.RuntimeValue? defaultConst)
        {
            Name = name; FieldType = fieldType; IsPublic = isPublic; IsStatic = isStatic;
            IsAbstract = isAbstract; IsOverride = isOverride; DeclKind = declKind; DefaultConst = defaultConst;
        }
    }

    // L5e — a struct method, flat: signature metadata + the PRECOMPILED body
    // RaFunction (replaces the AST BodyNode). Param defaults are AST → a method
    // with any non-null param default makes IrCompiler fall back to the visitor.
    public sealed class StructMethodDef
    {
        public readonly string Name;
        public readonly bool IsPublic;
        public readonly bool IsConstructor;
        public readonly bool IsAsync;
        public readonly bool IsAsyncStream;
        public readonly string[] ArgNames;
        public readonly TypeDescriptor?[] ArgTypes;
        public readonly bool[] IsRefParams;
        public readonly bool HasVarArgs;
        public readonly string? VarArgName;
        public readonly TypeDescriptor? VarArgType;
        public readonly TypeDescriptor? ReturnType;
        public readonly bool ShouldAutoReturn;
        public readonly int FrameId;
        public readonly RaFunction Body;               // precompiled (CompileMethodShape)
        public readonly string[] Generics;             // method-level type params (generic method)

        public StructMethodDef(string name, bool isPublic, bool isConstructor, bool isAsync, bool isAsyncStream,
            string[] argNames, TypeDescriptor?[] argTypes, bool[] isRefParams, bool hasVarArgs,
            string? varArgName, TypeDescriptor? varArgType, TypeDescriptor? returnType, bool shouldAutoReturn,
            int frameId, RaFunction body, string[]? generics = null)
        {
            Name = name; IsPublic = isPublic; IsConstructor = isConstructor; IsAsync = isAsync;
            IsAsyncStream = isAsyncStream; ArgNames = argNames; ArgTypes = argTypes; IsRefParams = isRefParams;
            HasVarArgs = hasVarArgs; VarArgName = varArgName; VarArgType = varArgType; ReturnType = returnType;
            ShouldAutoReturn = shouldAutoReturn; FrameId = frameId; Body = body;
            Generics = generics ?? Array.Empty<string>();
        }
    }

    // L5e — `struct Name { fields; methods }` lowered FLAT. The first sub-stage
    // covers the common subset: fields (no non-const defaults) + methods (no
    // param defaults) + simple generics. Operators / annotations / where-
    // constraints / param-defaults / non-const field-defaults → IrCompiler
    // falls back to the visitor. The handler reconstructs the (stub-bodied)
    // StructFieldDefinitionNode / StructMethodDefinitionNode the runtime
    // StructTypeValue API expects, wiring each method's precompiled RaFunction
    // into CompiledBody so execution is byte-identical to the visitor (which
    // compiles the same body lazily).
    // L10 one-shot-defn widening: an `operator <sym>(arg)` overload — a single-arg
    // method carrying the operator symbol. The body is precompiled (the SAME
    // GetOrCompileOperator the visitor uses lazily). Shared by struct/class/
    // record/extension lowering; reconstructed into an OperatorDefinitionNode.
    public sealed class OperatorDef
    {
        public readonly Lexer.Tokens.TokenType OpTokenType; // operator dispatch keys on Type, NOT text
        public readonly string Symbol;          // OperatorTok text, e.g. "+"
        public readonly bool IsPublic;
        public readonly bool IsOverride;
        public readonly bool IsStatic;
        public readonly string ArgName;
        public readonly TypeDescriptor? ArgType;
        public readonly TypeDescriptor? ReturnType;
        public readonly bool ShouldAutoReturn;
        public readonly string[] Generics;
        public readonly int FrameId;
        public readonly RaFunction Body;        // precompiled

        public OperatorDef(Lexer.Tokens.TokenType opTokenType, string symbol, bool isPublic, bool isOverride, bool isStatic,
            string argName, TypeDescriptor? argType, TypeDescriptor? returnType, bool shouldAutoReturn,
            string[] generics, int frameId, RaFunction body)
        {
            OpTokenType = opTokenType; Symbol = symbol; IsPublic = isPublic; IsOverride = isOverride; IsStatic = isStatic;
            ArgName = argName; ArgType = argType; ReturnType = returnType; ShouldAutoReturn = shouldAutoReturn;
            Generics = generics ?? Array.Empty<string>(); FrameId = frameId; Body = body;
        }
    }

    // L10 generic type-def widening: a generic `where T: Bound` constraint. No
    // body — the type-param name + the bound TypeDescriptor. Checked at runtime
    // by TypeSystem.ValidateWhereConstraints (method-bind time), so it must
    // survive the lowering + .rac round-trip. Reconstructed into a
    // WhereConstraintNode. Shared by struct/class/record lowering.
    public sealed class WhereConstraintDef
    {
        public readonly string ParameterName;
        public readonly TypeDescriptor ConstraintType;

        public WhereConstraintDef(string parameterName, TypeDescriptor constraintType)
        {
            ParameterName = parameterName; ConstraintType = constraintType;
        }
    }

    public sealed class StructDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Struct;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly string[] Generics;
        public readonly StructFieldDef[] Fields;
        public readonly StructMethodDef[] Methods;
        public readonly OperatorDef[] Operators;
        public readonly WhereConstraintDef[] Wheres;

        public StructDef(string name, bool isPublic, string[] generics, StructFieldDef[] fields, StructMethodDef[] methods,
            OperatorDef[]? operators = null, WhereConstraintDef[]? wheres = null)
        {
            Name = name; IsPublic = isPublic;
            Generics = generics ?? Array.Empty<string>();
            Fields = fields ?? Array.Empty<StructFieldDef>();
            Methods = methods ?? Array.Empty<StructMethodDef>();
            Operators = operators ?? Array.Empty<OperatorDef>();
            Wheres = wheres ?? Array.Empty<WhereConstraintDef>();
        }
    }

    // A record primary-constructor field (`record Point(x: int, ...)`). Flat:
    // const-foldable default → `DefaultConst` (null = none).
    public sealed class RecordPrimaryFieldDef
    {
        public readonly string Name;
        public readonly TypeDescriptor? FieldType;
        public readonly bool IsPublic;
        public readonly bool IsMutable;
        public readonly Values.RuntimeValue? DefaultConst;

        public RecordPrimaryFieldDef(string name, TypeDescriptor? fieldType, bool isPublic, bool isMutable, Values.RuntimeValue? defaultConst)
        {
            Name = name; FieldType = fieldType; IsPublic = isPublic; IsMutable = isMutable; DefaultConst = defaultConst;
        }
    }

    // `record Name(primary fields) { methods }` lowered FLAT. Reuses the shared
    // `StructMethodDef` (record methods ARE StructMethodDefinitionNodes). First
    // sub-stage covers value records with no inheritance / abstract / operators /
    // properties / events / annotations / where-constraints / param-defaults /
    // non-const field-defaults (all → fallback to the visitor).
    public sealed class RecordDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Record;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly bool IsRefRecord;
        public readonly bool AutoEquals;   // @derive(equals=false) flips this
        public readonly bool AutoToString; // @derive(to_string=false) flips this
        public readonly string[] Generics;
        public readonly RecordPrimaryFieldDef[] PrimaryFields;
        public readonly StructMethodDef[] Methods;
        public readonly OperatorDef[] Operators;
        public readonly WhereConstraintDef[] Wheres;

        public RecordDef(string name, bool isPublic, bool isRefRecord, bool autoEquals, bool autoToString,
            string[] generics, RecordPrimaryFieldDef[] primaryFields, StructMethodDef[] methods,
            OperatorDef[]? operators = null, WhereConstraintDef[]? wheres = null)
        {
            Name = name; IsPublic = isPublic; IsRefRecord = isRefRecord;
            AutoEquals = autoEquals; AutoToString = autoToString;
            Generics = generics ?? Array.Empty<string>();
            PrimaryFields = primaryFields ?? Array.Empty<RecordPrimaryFieldDef>();
            Methods = methods ?? Array.Empty<StructMethodDef>();
            Operators = operators ?? Array.Empty<OperatorDef>();
            Wheres = wheres ?? Array.Empty<WhereConstraintDef>();
        }
    }

    // A class method (or generative constructor) — class methods are
    // FunctionDefinitionNodes (richer than struct's StructMethodDefinitionNode),
    // so this carries the extra flags (override / static / factory). The body is
    // a precompiled RaFunction. Factory ctors / abstract / generic / param-
    // default / captured methods make IrCompiler fall back to the visitor.
    public sealed class ClassMethodDef
    {
        public readonly string Name;
        public readonly bool IsPublic;
        public readonly bool IsConstructor;
        public readonly bool IsOverride;
        public readonly bool IsStatic;
        public readonly bool IsAsync;
        public readonly bool IsAsyncStream;
        public readonly string[] ArgNames;
        public readonly TypeDescriptor?[] ArgTypes;
        public readonly bool[] IsRefParams;
        public readonly bool HasVarArgs;
        public readonly string? VarArgName;
        public readonly TypeDescriptor? VarArgType;
        public readonly TypeDescriptor? ReturnType;
        public readonly bool ShouldAutoReturn;
        public readonly int FrameId;
        public readonly RaFunction Body;
        public readonly string[] Generics;   // method-level type params (generic method)

        public ClassMethodDef(string name, bool isPublic, bool isConstructor, bool isOverride, bool isStatic,
            bool isAsync, bool isAsyncStream, string[] argNames, TypeDescriptor?[] argTypes, bool[] isRefParams,
            bool hasVarArgs, string? varArgName, TypeDescriptor? varArgType, TypeDescriptor? returnType,
            bool shouldAutoReturn, int frameId, RaFunction body, string[]? generics = null)
        {
            Name = name; IsPublic = isPublic; IsConstructor = isConstructor; IsOverride = isOverride;
            IsStatic = isStatic; IsAsync = isAsync; IsAsyncStream = isAsyncStream; ArgNames = argNames;
            ArgTypes = argTypes; IsRefParams = isRefParams; HasVarArgs = hasVarArgs; VarArgName = varArgName;
            VarArgType = varArgType; ReturnType = returnType; ShouldAutoReturn = shouldAutoReturn;
            FrameId = frameId; Body = body; Generics = generics ?? Array.Empty<string>();
        }
    }

    // `class Name { fields; methods }` lowered FLAT. First sub-stage: plain
    // value-ish classes — fields (StructFieldDefinitionNode, reuses the struct
    // field machinery incl. const defaults) + methods. Falls back on
    // inheritance (BaseType) / interfaces / traits / properties / events /
    // operators / static / abstract classes / annotations / where-constraints.
    public sealed class ClassDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Class;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly string[] Generics;
        public readonly StructFieldDef[] Fields;       // reuses the struct field descriptor
        public readonly ClassMethodDef[] Methods;
        public readonly OperatorDef[] Operators;
        public readonly WhereConstraintDef[] Wheres;

        public ClassDef(string name, bool isPublic, string[] generics, StructFieldDef[] fields, ClassMethodDef[] methods,
            OperatorDef[]? operators = null, WhereConstraintDef[]? wheres = null)
        {
            Name = name; IsPublic = isPublic;
            Generics = generics ?? Array.Empty<string>();
            Fields = fields ?? Array.Empty<StructFieldDef>();
            Methods = methods ?? Array.Empty<ClassMethodDef>();
            Operators = operators ?? Array.Empty<OperatorDef>();
            Wheres = wheres ?? Array.Empty<WhereConstraintDef>();
        }
    }

    // A trait method — provided (Body != null) or abstract/required
    // (IsAbstract, Body == null). No constructor/static/override/factory flags
    // (traits don't have them).
    public sealed class TraitMethodDef
    {
        public readonly string Name;
        public readonly bool IsAbstract;
        public readonly bool IsAsync;
        public readonly bool IsAsyncStream;
        public readonly string[] ArgNames;
        public readonly TypeDescriptor?[] ArgTypes;
        public readonly bool[] IsRefParams;
        public readonly bool HasVarArgs;
        public readonly string? VarArgName;
        public readonly TypeDescriptor? VarArgType;
        public readonly TypeDescriptor? ReturnType;
        public readonly bool ShouldAutoReturn;
        public readonly int FrameId;
        public readonly RaFunction? Body; // null = abstract/required (signature only)

        public TraitMethodDef(string name, bool isAbstract, bool isAsync, bool isAsyncStream,
            string[] argNames, TypeDescriptor?[] argTypes, bool[] isRefParams, bool hasVarArgs,
            string? varArgName, TypeDescriptor? varArgType, TypeDescriptor? returnType, bool shouldAutoReturn,
            int frameId, RaFunction? body)
        {
            Name = name; IsAbstract = isAbstract; IsAsync = isAsync; IsAsyncStream = isAsyncStream;
            ArgNames = argNames; ArgTypes = argTypes; IsRefParams = isRefParams; HasVarArgs = hasVarArgs;
            VarArgName = varArgName; VarArgType = varArgType; ReturnType = returnType;
            ShouldAutoReturn = shouldAutoReturn; FrameId = frameId; Body = body;
        }
    }

    // `trait Name { fn provided() {..}  fn required(); }` lowered FLAT. First
    // sub-stage: methods (provided + abstract) + fields; fallback on
    // properties / events / where-constraints / annotations / param-defaults.
    public sealed class TraitDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Trait;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly string[] Generics;
        public readonly StructFieldDef[] Fields;
        public readonly TraitMethodDef[] Methods;

        public TraitDef(string name, bool isPublic, string[] generics, StructFieldDef[] fields, TraitMethodDef[] methods)
        {
            Name = name; IsPublic = isPublic;
            Generics = generics ?? Array.Empty<string>();
            Fields = fields ?? Array.Empty<StructFieldDef>();
            Methods = methods ?? Array.Empty<TraitMethodDef>();
        }
    }

    // `extend T { fn ... }` lowered FLAT. Extension methods are
    // FunctionDefinitionNodes → reuse ClassMethodDef. First sub-stage: methods
    // only; fallback on properties / operators / events / fields / indexers.
    public sealed class ExtensionDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Extension;

        public readonly TypeDescriptor TargetType;
        public readonly bool IsPublic;
        public readonly bool IsSealed;
        public readonly ClassMethodDef[] Methods;

        public ExtensionDef(TypeDescriptor targetType, bool isPublic, bool isSealed, ClassMethodDef[] methods)
        {
            TargetType = targetType; IsPublic = isPublic; IsSealed = isSealed;
            Methods = methods ?? Array.Empty<ClassMethodDef>();
        }
    }

    // An interface method — a pure SIGNATURE (name + param names + param types +
    // return type). Interfaces carry no method bodies (conformance is checked by
    // signature), so unlike struct/class/trait there is NO precompiled RaFunction
    // here — InterfaceDef is the simplest code-bearing kind, pure flat metadata.
    public sealed class InterfaceMethodDef
    {
        public readonly string Name;
        public readonly string[] ArgNames;
        public readonly TypeDescriptor?[] ArgTypes;
        public readonly TypeDescriptor? ReturnType;

        public InterfaceMethodDef(string name, string[] argNames, TypeDescriptor?[] argTypes, TypeDescriptor? returnType)
        {
            Name = name;
            ArgNames = argNames ?? Array.Empty<string>();
            ArgTypes = argTypes ?? Array.Empty<TypeDescriptor?>();
            ReturnType = returnType;
        }
    }

    // `interface Name { fn sig(); var field: T }` lowered FLAT. Interface methods
    // are signatures only (no bodies → no RaFunction), fields reuse the struct
    // field descriptor (interface fields can't have defaults, so DefaultConst is
    // always null). First sub-stage: methods + fields + simple generics. Falls
    // back on properties / events / annotations / where-constraints. The handler
    // reconstructs the InterfaceDefinitionNode (signature + field nodes) and runs
    // the SAME InterfaceDefinitionNodeVisitor.Apply → byte-identical registration
    // + conformance metadata.
    public sealed class InterfaceDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Interface;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly string[] Generics;
        public readonly StructFieldDef[] Fields;       // reuses the struct field descriptor
        public readonly InterfaceMethodDef[] Methods;

        public InterfaceDef(string name, bool isPublic, string[] generics, StructFieldDef[] fields, InterfaceMethodDef[] methods)
        {
            Name = name; IsPublic = isPublic;
            Generics = generics ?? Array.Empty<string>();
            Fields = fields ?? Array.Empty<StructFieldDef>();
            Methods = methods ?? Array.Empty<InterfaceMethodDef>();
        }
    }

    // An annotation parameter — name + optional declared type + an optional
    // const-folded default (null = no default; a non-constant default makes
    // IrCompiler fall back to the visitor). Mirrors AnnotationParameterNode.
    public sealed class AnnotationParamDef
    {
        public readonly string Name;
        public readonly TypeDescriptor? DeclaredType;
        public readonly Values.RuntimeValue? DefaultConst;
        public readonly bool IsVarArgs;

        public AnnotationParamDef(string name, TypeDescriptor? declaredType, Values.RuntimeValue? defaultConst, bool isVarArgs)
        {
            Name = name; DeclaredType = declaredType; DefaultConst = defaultConst; IsVarArgs = isVarArgs;
        }
    }

    // `annotation Name(params)` lowered FLAT. First sub-stage: annotations with
    // NO meta-annotations (the `@target`/`@priority`/`@repeatable`/… on the
    // annotation definition itself carry argument expressions + register
    // metadata, so any meta-annotation → IrCompiler falls back to the visitor)
    // and only const-foldable / absent parameter defaults. The handler
    // reconstructs the AnnotationDefinitionNode (params with const-default stubs)
    // and runs the SAME visitor Apply → byte-identical AnnotationTypeValue
    // registration.
    public sealed class AnnotationDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Annotation;

        public readonly string Name;
        public readonly bool IsPublic;
        public readonly AnnotationParamDef[] Parameters;

        public AnnotationDef(string name, bool isPublic, AnnotationParamDef[] parameters)
        {
            Name = name; IsPublic = isPublic;
            Parameters = parameters ?? Array.Empty<AnnotationParamDef>();
        }
    }

    // L6 — `import …` lowered FLAT. The ModuleSpecifier is already flat data
    // (a string-literal raw path OR dotted segments + a wildcard flag), so an
    // ImportDef captures it verbatim plus the form-specific extra (selected
    // symbol names / alias). NO fallback condition — every import shape lowers.
    // The handler rebuilds the ModuleSpecifier + the matching ImportNode and
    // runs the SAME ImportNodeVisitor.Apply → ModuleManager.Load resolution is
    // byte-identical. (Reuses OP_DEFINE_TYPE like UsingDef — imports are
    // directives, not types, but slot into the same machinery.)
    public sealed class ImportDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Import;

        public readonly ImportDefKind ImportKind;
        public readonly bool SpecIsDotted;     // false = StringLiteral specifier
        public readonly string? RawPath;       // StringLiteral payload
        public readonly string[] Segments;     // Dotted payload
        public readonly bool IsWildcard;       // trailing `.*` on a dotted path
        public readonly string[] SymbolNames;  // Selective: imported names (else empty)
        public readonly string? Alias;         // Alias: bound name (else null)

        public ImportDef(ImportDefKind importKind, bool specIsDotted, string? rawPath, string[] segments,
            bool isWildcard, string[] symbolNames, string? alias)
        {
            ImportKind = importKind; SpecIsDotted = specIsDotted; RawPath = rawPath;
            Segments = segments ?? Array.Empty<string>();
            IsWildcard = isWildcard;
            SymbolNames = symbolNames ?? Array.Empty<string>();
            Alias = alias;
        }
    }

    // L6 — `namespace A.B { …body… }` lowered FLAT. Unlike the other kinds this
    // is SCOPE work: the body is a sequence of statements that execute in a
    // runtime-built NamespaceScopeView (registering definitions INTO the
    // namespace), not a static descriptor. So `Bodies` holds each body
    // statement PRECOMPILED to a RaFunction (via
    // IrExpressionEvaluator.CompileBodyStatement — bytecode-identical to the AST
    // visitor's on-demand compile). The handler reconstructs the
    // NamespaceDeclarationNode (segments only; body is a stub) and runs the SAME
    // NamespaceDeclarationNodeVisitor.Apply passing `Bodies` — so namespace
    // opening / scope-chain / closure-freezing are byte-identical; only the body
    // statements are pre-compiled instead of compiled on first execution.
    public sealed class NamespaceDef : TypeDef
    {
        public override TypeDefKind Kind => TypeDefKind.Namespace;

        public readonly string[] Segments;
        public readonly bool IsFileScoped;
        public readonly RaFunction[] Bodies;

        public NamespaceDef(string[] segments, bool isFileScoped, RaFunction[] bodies)
        {
            Segments = segments ?? Array.Empty<string>();
            IsFileScoped = isFileScoped;
            Bodies = bodies ?? Array.Empty<RaFunction>();
        }
    }
}

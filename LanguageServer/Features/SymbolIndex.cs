using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>A declared symbol with its full node span and name-token selection span.</summary>
    public sealed class RaSymbol
    {
        public string Name { get; set; } = string.Empty;
        public SymbolKind Kind { get; set; }
        public string? Detail { get; set; }
        public int RangeStart { get; set; }
        public int RangeEnd { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionEnd { get; set; }
        public bool IsPublic { get; set; } = true;
        /// <summary>Parameter names for callable symbols (functions / methods / constructors).</summary>
        public List<string>? Parameters { get; set; }
        /// <summary>Declared parameter types (parallel to <see cref="Parameters"/>) for arg-type checking.</summary>
        public IReadOnlyList<RaLanguage.Types.TypeDescriptor?>? ParameterTypes { get; set; }
        /// <summary>Call arity for callables: minimum required and maximum (int.MaxValue if varargs). -1 = not callable.</summary>
        public int MinArgs { get; set; } = -1;
        public int MaxArgs { get; set; } = -1;
        /// <summary>For fields/properties: their type. For methods/functions: the return type. Drives inference chaining.</summary>
        public RaLanguage.Types.TypeDescriptor? DeclaredType { get; set; }
        /// <summary>Type has a base class / implemented interfaces / mixed-in traits → members may be inherited.</summary>
        public bool HasBase { get; set; }
        /// <summary>Names of base class + implemented interfaces + mixed-in traits, for base-chain member resolution.</summary>
        public List<string>? BaseTypes { get; set; }
        /// <summary>Generic type-parameter names of this type (e.g. ["T"] for Box&lt;T&gt;), for member substitution.</summary>
        public List<string>? GenericParams { get; set; }
        public List<RaSymbol> Children { get; } = new();

        public bool IsCallable => MinArgs >= 0;
    }

    /// <summary>
    /// Structural symbol table built by walking the declaration nodes of the AST.
    /// Produces both a hierarchical tree (document outline) and a flat list keyed by
    /// name (definition / hover / signature lookups). Function bodies are not
    /// descended into — locals are resolved by the token-based fallbacks instead —
    /// which keeps the outline focused on the public structure of a file.
    /// </summary>
    public sealed class SymbolIndex
    {
        public List<RaSymbol> TopLevel { get; } = new();
        public List<RaSymbol> Flat { get; } = new();

        public static SymbolIndex Build(AstNode? root)
        {
            var index = new SymbolIndex();
            if (root != null) index.VisitInto(root, index.TopLevel);
            return index;
        }

        public IEnumerable<RaSymbol> FindByName(string name)
        {
            for (int i = 0; i < Flat.Count; i++)
                if (Flat[i].Name == name) yield return Flat[i];
        }

        private RaSymbol Add(List<RaSymbol> sink, string name, SymbolKind kind, AstNode node, in Token nameTok, string? detail = null, bool isPublic = true)
        {
            var symbol = new RaSymbol
            {
                Name = name,
                Kind = kind,
                Detail = detail,
                RangeStart = node.PositionStart.Idx,
                RangeEnd = node.PositionEnd.Idx,
                SelectionStart = nameTok.PositionStart.Idx,
                SelectionEnd = nameTok.PositionEnd.Idx,
                IsPublic = isPublic,
            };
            sink.Add(symbol);
            Flat.Add(symbol);
            return symbol;
        }

        private static string NameOf(in Token tok) => tok.Value?.ToString() ?? string.Empty;

        private void VisitInto(AstNode node, List<RaSymbol> sink)
        {
            switch (node)
            {
                case ScopeNode scope:
                    foreach (var child in scope.Nodes) VisitInto(child, sink);
                    break;

                case NamespaceDeclarationNode ns:
                {
                    var sym = AddNamespace(ns, sink);
                    if (ns.Body != null) VisitInto(ns.Body, sym.Children);
                    break;
                }

                case ClassDefinitionNode cls:
                {
                    var sym = Add(sink, NameOf(cls.NameTok), SymbolKind.Class, cls, cls.NameTok, isPublic: cls.IsPublic);
                    {
                        var bases = new List<string>();
                        if (cls.BaseType != null) bases.Add(cls.BaseType.Name);
                        foreach (var bi in cls.ImplementedInterfaces) bases.Add(bi.Name);
                        foreach (var bt in cls.WithTraits) bases.Add(bt.Name);
                        sym.BaseTypes = bases;
                        sym.HasBase = bases.Count > 0;
                    }
                    sym.GenericParams = cls.GenericTypeParams;
                    AddFields(cls.Fields, sym);
                    AddMethods(cls.Methods, sym);
                    AddProperties(cls.Properties, sym);
                    AddEvents(cls.Events, sym);
                    break;
                }

                case StructDefinitionNode str:
                {
                    var sym = Add(sink, NameOf(str.NameTok), SymbolKind.Struct, str, str.NameTok, isPublic: str.IsPublic);
                    sym.GenericParams = str.GenericTypeParams;
                    AddFields(str.Fields, sym);
                    AddStructMethods(str.Methods, sym);
                    AddProperties(str.Properties, sym);
                    AddEvents(str.Events, sym);
                    break;
                }

                case RecordDefinitionNode rec:
                {
                    var sym = Add(sink, NameOf(rec.NameTok), SymbolKind.Struct, rec, rec.NameTok, isPublic: rec.IsPublic);
                    sym.HasBase = rec.BaseType != null;
                    if (rec.BaseType != null) sym.BaseTypes = new List<string> { rec.BaseType.Name };
                    sym.GenericParams = rec.GenericTypeParams;
                    foreach (var f in rec.PrimaryFields)
                    {
                        var rf = Add(sym.Children, NameOf(f.NameTok), SymbolKind.Field, f, f.NameTok, isPublic: f.IsPublic);
                        rf.DeclaredType = f.FieldType;
                    }
                    AddStructMethods(rec.Methods, sym);
                    AddProperties(rec.Properties, sym);
                    AddEvents(rec.Events, sym);
                    break;
                }

                case EnumDefinitionNode en:
                {
                    var sym = Add(sink, NameOf(en.NameTok), SymbolKind.Enum, en, en.NameTok);
                    sym.GenericParams = en.GenericTypeParams;
                    foreach (var v in en.Variants)
                        Add(sym.Children, NameOf(v.MemberTok), SymbolKind.EnumMember, en, v.MemberTok);
                    break;
                }

                case InterfaceDefinitionNode iface:
                {
                    var sym = Add(sink, NameOf(iface.NameTok), SymbolKind.Interface, iface, iface.NameTok, isPublic: iface.IsPublic);
                    sym.GenericParams = iface.GenericTypeParams;
                    foreach (var m in iface.Methods)
                        Add(sym.Children, NameOf(m.NameTok), SymbolKind.Method, m, m.NameTok);
                    AddFields(iface.Fields, sym);
                    AddProperties(iface.Properties, sym);
                    break;
                }

                case TraitDefinitionNode trait:
                {
                    var sym = Add(sink, NameOf(trait.NameTok), SymbolKind.Interface, trait, trait.NameTok, isPublic: trait.IsPublic);
                    sym.GenericParams = trait.GenericTypeParams;
                    foreach (var m in trait.Methods)
                        if (m.NameTok.HasValue) Add(sym.Children, NameOf(m.NameTok.Value), SymbolKind.Method, m, m.NameTok.Value);
                    AddFields(trait.Fields, sym);
                    AddProperties(trait.Properties, sym);
                    break;
                }

                case FunctionDefinitionNode fn:
                {
                    if (fn.VarNameTok.HasValue)
                    {
                        var kind = (fn.IsConstructor || fn.IsFactory) ? SymbolKind.Constructor : SymbolKind.Function;
                        var sym = Add(sink, NameOf(fn.VarNameTok.Value), kind, fn, fn.VarNameTok.Value, FunctionDetail(fn.ArgNameToks), isPublic: fn.IsPublic);
                        sym.Parameters = ParamNames(fn.ArgNameToks);
                        SetArity(sym, fn.ArgNameToks.Count, fn.ParamDefaults, fn.HasVarArgs);
                        sym.DeclaredType = fn.ReturnType;
                        sym.ParameterTypes = fn.ArgTypes;
                        sym.GenericParams = fn.GenericTypeParams;
                    }
                    break;
                }

                case DelegateDefinitionNode del:
                    Add(sink, NameOf(del.NameTok), SymbolKind.Interface, del, del.NameTok, isPublic: del.IsPublic);
                    break;

                case AnnotationDefinitionNode ann:
                    Add(sink, NameOf(ann.NameTok), SymbolKind.Class, ann, ann.NameTok, isPublic: ann.IsPublic);
                    break;

                case ExtensionDefinitionNode ext:
                {
                    // Name the extension after its target type so its members merge with
                    // the type for member completion; detail marks it as an extension.
                    string target = ext.TargetType?.Name ?? "extension";
                    var sym = Add(sink, target, SymbolKind.Class, ext, default, detail: "(extension)", isPublic: ext.IsPublic);
                    AddMethods(ext.Methods, sym);
                    AddProperties(ext.Properties, sym);
                    break;
                }

                case VariableDeclarationNode varDecl:
                {
                    var kind = SymbolKind.Variable;
                    foreach (var decl in varDecl.Declarations)
                    {
                        var nameTok = decl.Item1;
                        var vs = Add(sink, NameOf(nameTok), kind, varDecl, nameTok, isPublic: varDecl.IsPublic);
                        vs.DeclaredType = decl.Item3;
                    }
                    break;
                }
            }
        }

        private RaSymbol AddNamespace(NamespaceDeclarationNode ns, List<RaSymbol> sink)
        {
            // Build a dotted name from the segment tokens; anchor selection on the
            // first segment.
            var parts = new List<string>();
            Token anchor = default;
            bool haveAnchor = false;
            foreach (var seg in ns.Segments)
            {
                parts.Add(NameOf(seg));
                if (!haveAnchor) { anchor = seg; haveAnchor = true; }
            }
            string name = parts.Count > 0 ? string.Join(".", parts) : "namespace";
            return Add(sink, name, SymbolKind.Namespace, ns, haveAnchor ? anchor : default);
        }

        private void AddFields(IReadOnlyList<StructFieldDefinitionNode> fields, RaSymbol parent)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                var fs = Add(parent.Children, NameOf(fields[i].NameTok), SymbolKind.Field, fields[i], fields[i].NameTok, isPublic: fields[i].IsPublic);
                fs.DeclaredType = fields[i].FieldType;
            }
        }

        private void AddMethods(IReadOnlyList<FunctionDefinitionNode> methods, RaSymbol parent)
        {
            for (int i = 0; i < methods.Count; i++)
            {
                var m = methods[i];
                if (!m.VarNameTok.HasValue) continue;
                var kind = (m.IsConstructor || m.IsFactory) ? SymbolKind.Constructor : SymbolKind.Method;
                var sym = Add(parent.Children, NameOf(m.VarNameTok.Value), kind, m, m.VarNameTok.Value, FunctionDetail(m.ArgNameToks), isPublic: m.IsPublic);
                sym.Parameters = ParamNames(m.ArgNameToks);
                SetArity(sym, m.ArgNameToks.Count, m.ParamDefaults, m.HasVarArgs);
                sym.DeclaredType = m.ReturnType;
                sym.ParameterTypes = m.ArgTypes;
            }
        }

        private void AddStructMethods(IReadOnlyList<StructMethodDefinitionNode> methods, RaSymbol parent)
        {
            for (int i = 0; i < methods.Count; i++)
            {
                var m = methods[i];
                var kind = m.IsConstructor ? SymbolKind.Constructor : SymbolKind.Method;
                var sym = Add(parent.Children, NameOf(m.NameTok), kind, m, m.NameTok, FunctionDetail(m.ArgNameToks), isPublic: m.IsPublic);
                sym.Parameters = ParamNames(m.ArgNameToks);
                SetArity(sym, m.ArgNameToks.Count, m.ParamDefaults, m.HasVarArgs);
                sym.DeclaredType = m.ReturnType;
                sym.ParameterTypes = m.ArgTypes;
            }
        }

        private void AddProperties(IReadOnlyList<PropertyDefinitionNode> properties, RaSymbol parent)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                var ps = Add(parent.Children, NameOf(properties[i].NameTok), SymbolKind.Property, properties[i], properties[i].NameTok, isPublic: properties[i].IsPublic);
                ps.DeclaredType = properties[i].PropertyType;
            }
        }

        private void AddEvents(IReadOnlyList<EventDefinitionNode> events, RaSymbol parent)
        {
            for (int i = 0; i < events.Count; i++)
                Add(parent.Children, NameOf(events[i].NameTok), SymbolKind.Event, events[i], events[i].NameTok, isPublic: events[i].IsPublic);
        }

        private static void SetArity(RaSymbol sym, int total, IReadOnlyList<AstNode?>? defaults, bool hasVarArgs)
        {
            int defCount = 0;
            if (defaults != null)
                for (int i = 0; i < defaults.Count; i++) if (defaults[i] != null) defCount++;
            int min = total - defCount;
            sym.MinArgs = min < 0 ? 0 : min;
            sym.MaxArgs = hasVarArgs ? int.MaxValue : total;
        }

        private static List<string> ParamNames(IReadOnlyList<Token> argNameToks)
        {
            var names = new List<string>(argNameToks?.Count ?? 0);
            if (argNameToks != null)
            {
                for (int i = 0; i < argNameToks.Count; i++)
                    names.Add(argNameToks[i].Value?.ToString() ?? "_");
            }
            return names;
        }

        private static string FunctionDetail(IReadOnlyList<Token> argNameToks)
        {
            if (argNameToks == null || argNameToks.Count == 0) return "()";
            var sb = new System.Text.StringBuilder("(");
            for (int i = 0; i < argNameToks.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(argNameToks[i].Value?.ToString() ?? "_");
            }
            sb.Append(')');
            return sb.ToString();
        }
    }
}

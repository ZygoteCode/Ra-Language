using System.Collections.Generic;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>Richer-than-LSP classification for a bound declaration (drives hover labels).</summary>
    public enum BoundKind
    {
        Variable,
        Parameter,
        Function,
        Class,
        Struct,
        Record,
        Enum,
        Interface,
        Trait,
        Annotation,
        Delegate,
        Namespace,
        LoopVariable,
        CatchVariable,
        PatternBinding,
    }

    /// <summary>A declared name with its name-token span and every resolved reference to it.</summary>
    public sealed class BoundSymbol
    {
        public string Name { get; }
        public BoundKind Kind { get; }
        public int NameStart { get; }
        public int NameEnd { get; }
        public List<BoundReference> References { get; } = new();

        public BoundSymbol(string name, BoundKind kind, int nameStart, int nameEnd)
        {
            Name = name;
            Kind = kind;
            NameStart = nameStart;
            NameEnd = nameEnd;
        }

        public SymbolKind LspKind => Kind switch
        {
            BoundKind.Function => SymbolKind.Function,
            BoundKind.Class => SymbolKind.Class,
            BoundKind.Struct or BoundKind.Record => SymbolKind.Struct,
            BoundKind.Enum => SymbolKind.Enum,
            BoundKind.Interface or BoundKind.Trait => SymbolKind.Interface,
            BoundKind.Annotation => SymbolKind.Class,
            BoundKind.Delegate => SymbolKind.Interface,
            BoundKind.Namespace => SymbolKind.Namespace,
            _ => SymbolKind.Variable,
        };

        public string Word => Kind switch
        {
            BoundKind.Parameter => "parameter",
            BoundKind.Function => "fn",
            BoundKind.Class => "class",
            BoundKind.Struct => "struct",
            BoundKind.Record => "record",
            BoundKind.Enum => "enum",
            BoundKind.Interface => "interface",
            BoundKind.Trait => "trait",
            BoundKind.Annotation => "annotation",
            BoundKind.Delegate => "delegate",
            BoundKind.Namespace => "namespace",
            BoundKind.LoopVariable => "loop variable",
            BoundKind.CatchVariable => "catch binding",
            BoundKind.PatternBinding => "binding",
            _ => "variable",
        };
    }

    /// <summary>An identifier read the binder could not resolve in lexical scope (a
    /// candidate for "undefined symbol" once builtins/imports/types are excluded).</summary>
    public readonly struct UnresolvedRef
    {
        public readonly string Name;
        public readonly int Start;
        public readonly int End;
        public UnresolvedRef(string name, int start, int end) { Name = name; Start = start; End = end; }
    }

    /// <summary>A bare-name call site (callee is a plain identifier), for arity checking.</summary>
    public readonly struct CallSite
    {
        public readonly string Callee;
        public readonly int ArgCount;
        public readonly int Start;
        public readonly int End;
        public readonly AstNode[] Args;
        public CallSite(string callee, int argCount, int start, int end, AstNode[] args)
        { Callee = callee; ArgCount = argCount; Start = start; End = end; Args = args; }
    }

    /// <summary>An <c>identifier.member</c> access, for module-qualified member checking.</summary>
    public readonly struct MemberAccessSite
    {
        public readonly string TargetName;
        public readonly string Member;
        public readonly int Start;
        public readonly int End;
        public MemberAccessSite(string targetName, string member, int start, int end) { TargetName = targetName; Member = member; Start = start; End = end; }
    }

    public sealed class BoundReference
    {
        public BoundSymbol Target { get; }
        public int Start { get; }
        public int End { get; }
        public bool IsWrite { get; }

        public BoundReference(BoundSymbol target, int start, int end, bool isWrite)
        {
            Target = target;
            Start = start;
            End = end;
            IsWrite = isWrite;
        }
    }

    /// <summary>
    /// Scope-aware resolution result for one document. Maps a cursor offset to the
    /// declaration or reference it sits on, with the full reference set per symbol —
    /// the precise, shadow-correct replacement for name-based definition/references/
    /// rename. Members, builtins and cross-module names stay unresolved here and fall
    /// back to the structural <see cref="SymbolIndex"/> / workspace index.
    /// </summary>
    public sealed class SemanticModel
    {
        public IReadOnlyList<BoundSymbol> Symbols { get; }
        public IReadOnlyList<BoundReference> References { get; }
        public IReadOnlyList<UnresolvedRef> Unresolved { get; }
        public IReadOnlyList<CallSite> Calls { get; }
        public IReadOnlyList<MemberAccessSite> MemberAccesses { get; }

        // Flattened, start-sorted spans for O(log n) "what is under the cursor".
        private readonly int[] _starts;
        private readonly Entry[] _entries;

        private readonly struct Entry
        {
            public readonly int End;
            public readonly BoundSymbol Symbol;
            public readonly bool IsDeclaration;
            public readonly bool IsWrite;
            public Entry(int end, BoundSymbol symbol, bool isDeclaration, bool isWrite)
            {
                End = end; Symbol = symbol; IsDeclaration = isDeclaration; IsWrite = isWrite;
            }
        }

        public SemanticModel(List<BoundSymbol> symbols, List<BoundReference> references, List<UnresolvedRef> unresolved,
            List<CallSite> calls, List<MemberAccessSite> memberAccesses)
        {
            Symbols = symbols;
            References = references;
            Unresolved = unresolved;
            Calls = calls;
            MemberAccesses = memberAccesses;

            var list = new List<(int Start, Entry Entry)>(symbols.Count + references.Count);
            foreach (var s in symbols)
                list.Add((s.NameStart, new Entry(s.NameEnd, s, isDeclaration: true, isWrite: true)));
            foreach (var r in references)
                list.Add((r.Start, new Entry(r.End, r.Target, isDeclaration: false, r.IsWrite)));
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            _starts = new int[list.Count];
            _entries = new Entry[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                _starts[i] = list[i].Start;
                _entries[i] = list[i].Entry;
            }
        }

        /// <summary>The symbol whose declaration or a reference covers <paramref name="offset"/>, or null.</summary>
        public BoundSymbol? SymbolAt(int offset)
        {
            int i = FloorIndex(offset);
            // Scan a small window left for an entry spanning the offset (spans are tiny and rarely nested).
            for (int k = i; k >= 0 && k >= i - 4; k--)
            {
                if (k < 0) break;
                if (offset >= _starts[k] && offset <= _entries[k].End) return _entries[k].Symbol;
            }
            return null;
        }

        private int FloorIndex(int offset)
        {
            int lo = 0, hi = _starts.Length - 1, result = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_starts[mid] <= offset) { result = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return result;
        }
    }
}

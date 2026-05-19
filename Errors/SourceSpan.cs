using System.Runtime.CompilerServices;
using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    public readonly struct SourceSpan : IEquatable<SourceSpan>
    {
        public Position Start { get; }
        public Position End { get; }

        public SourceSpan(Position start, Position end)
        {
            Start = start;
            End = end;
        }

        public bool IsValid => !string.IsNullOrEmpty(Start.Fn) || !string.IsNullOrEmpty(Start.Ftxt);

        public bool IsMultiLine => End.Ln > Start.Ln;

        public string? FileName => Start.Fn;

        public int Length => Math.Max(0, End.Idx - Start.Idx);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SourceSpan Of(Position start, Position end) => new(start, end);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SourceSpan Point(Position p) => new(p, p);

        public SourceSpan Union(SourceSpan other)
        {
            if (!IsValid) return other;
            if (!other.IsValid) return this;
            var s = Start.Idx <= other.Start.Idx ? Start : other.Start;
            var e = End.Idx >= other.End.Idx ? End : other.End;
            return new SourceSpan(s, e);
        }

        public bool Equals(SourceSpan other) =>
            Start.Idx == other.Start.Idx &&
            End.Idx == other.End.Idx &&
            string.Equals(Start.Fn, other.Start.Fn, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SourceSpan s && Equals(s);

        public override int GetHashCode() => HashCode.Combine(Start.Idx, End.Idx, Start.Fn);

        public override string ToString()
        {
            if (!IsValid) return "<unknown>";
            string fn = string.IsNullOrEmpty(Start.Fn) ? "<input>" : Start.Fn;
            return $"{fn}:{Start.Ln + 1}:{Start.Col + 1}";
        }
    }
}

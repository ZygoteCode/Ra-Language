using System;
using System.Collections.Generic;

namespace RaLanguage.LanguageServer.Text
{
    /// <summary>Negotiated LSP position encoding (units of the <c>character</c> field).</summary>
    public enum PositionEncodingKind
    {
        /// <summary>UTF-16 code units — the LSP default and what C# strings index in.</summary>
        Utf16,
        /// <summary>UTF-8 bytes.</summary>
        Utf8,
        /// <summary>UTF-32 / Unicode code points.</summary>
        Utf32,
    }

    /// <summary>
    /// Maps between absolute UTF-16 character offsets (what the lexer/AST carry in
    /// <c>Idx</c>) and LSP (line, character) positions, honoring the negotiated
    /// position encoding for the <c>character</c> axis.
    /// <para>
    /// Line starts are always tracked in UTF-16 units (C# string indices). Only the
    /// in-line column is translated to/from the negotiated encoding, so UTF-16 (the
    /// VS Code default) stays a zero-cost identity path and the common ASCII case is
    /// trivial in every encoding.
    /// </para>
    /// </summary>
    public sealed class LineIndex
    {
        private readonly string _text;
        private readonly int[] _lineStarts;
        private readonly PositionEncodingKind _encoding;

        public LineIndex(string text, PositionEncodingKind encoding = PositionEncodingKind.Utf16)
        {
            _text = text ?? string.Empty;
            _encoding = encoding;
            _lineStarts = BuildLineStarts(_text);
        }

        public string Text => _text;
        public int LineCount => _lineStarts.Length;
        public int Length => _text.Length;
        public PositionEncodingKind Encoding => _encoding;

        private static int[] BuildLineStarts(string text)
        {
            var starts = new List<int>(Math.Max(4, text.Length / 32)) { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        /// <summary>Absolute UTF-16 offset → zero-based (line, character-in-encoding).</summary>
        public (int Line, int Character) OffsetToPosition(int offset)
        {
            if (offset <= 0) return (0, 0);
            if (offset > _text.Length) offset = _text.Length;

            int line = FindLine(offset);
            int lineStart = _lineStarts[line];
            int character = _encoding == PositionEncodingKind.Utf16
                ? offset - lineStart
                : EncodedColumn(lineStart, offset);
            return (line, character);
        }

        /// <summary>(line, character-in-encoding) → absolute UTF-16 offset (clamped).</summary>
        public int PositionToOffset(int line, int character)
        {
            if (line < 0) return 0;
            if (line >= _lineStarts.Length) return _text.Length;
            if (character < 0) character = 0;

            int lineStart = _lineStarts[line];
            int lineEndExclusive = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;

            int offset = _encoding == PositionEncodingKind.Utf16
                ? lineStart + character
                : Utf16OffsetFromColumn(lineStart, lineEndExclusive, character);

            return offset > lineEndExclusive ? lineEndExclusive : offset;
        }

        public int LineStart(int line)
        {
            if (line < 0) return 0;
            if (line >= _lineStarts.Length) return _text.Length;
            return _lineStarts[line];
        }

        public int LineEndExclusive(int line)
        {
            if (line < 0) return 0;
            if (line + 1 < _lineStarts.Length) return _lineStarts[line + 1];
            return _text.Length;
        }

        private int FindLine(int offset)
        {
            int lo = 0, hi = _lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_lineStarts[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        // ---- encoding column math (utf-8 / utf-32) ----

        // UTF-16 offset within a line → column in the negotiated encoding.
        private int EncodedColumn(int lineStart, int offset)
        {
            int col = 0;
            int i = lineStart;
            while (i < offset)
            {
                int cp = char.ConvertToUtf32(_text, i);
                int utf16Units = cp >= 0x10000 ? 2 : 1;
                col += _encoding == PositionEncodingKind.Utf8 ? Utf8Length(cp) : 1; // Utf32 → 1 per code point
                i += utf16Units;
            }
            return col;
        }

        // Column in the negotiated encoding → UTF-16 offset within the line.
        private int Utf16OffsetFromColumn(int lineStart, int lineEndExclusive, int column)
        {
            int col = 0;
            int i = lineStart;
            while (i < lineEndExclusive && col < column)
            {
                int cp = char.ConvertToUtf32(_text, i);
                int utf16Units = cp >= 0x10000 ? 2 : 1;
                col += _encoding == PositionEncodingKind.Utf8 ? Utf8Length(cp) : 1;
                i += utf16Units;
            }
            return i;
        }

        private static int Utf8Length(int codePoint)
        {
            if (codePoint < 0x80) return 1;
            if (codePoint < 0x800) return 2;
            if (codePoint < 0x10000) return 3;
            return 4;
        }
    }
}

namespace RaLanguage.Types.Formatting
{
    // Numeric / textual kind a format spec targets. Mirrors the type-char part
    // of the grammar (`f`, `x`, `b`, `%`, ...). The "default" kind covers
    // `${value}` with no spec and `${value:.3}` (precision only).
    public enum FormatKind : byte
    {
        Default = 0,
        Float,
        Hex,
        Binary,
        Octal,
        Decimal,
        Exponential,
        General,
        Percent
    }

    // Parsed, runtime-ready representation of a `:spec` format directive.
    //
    // FormatSpec values are produced once by the parser (lexer already validated
    // the text via Lexer.IsValidFormatSpec) and then read by FormatEngine at
    // runtime. Kept as a readonly struct so it can be embedded by-value inside
    // FormattedInterpolationNode without paying an extra allocation per node.
    public readonly struct FormatSpec
    {
        public FormatKind Kind { get; }
        public int Precision { get; }
        public bool HasPrecision { get; }
        public bool AlternateForm { get; }
        public bool UpperCase { get; }

        public FormatSpec(FormatKind kind, bool alternateForm, bool hasPrecision, int precision, bool upperCase)
        {
            Kind = kind;
            AlternateForm = alternateForm;
            HasPrecision = hasPrecision;
            Precision = precision;
            UpperCase = upperCase;
        }

        public static readonly FormatSpec Default = new FormatSpec(FormatKind.Default, false, false, 0, false);

        // Parses a syntactically validated spec string (the lexer rejects
        // malformed specs before they ever reach us). Returns Default when the
        // input is null or empty so callers do not need a separate null check.
        public static FormatSpec Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return Default;

            int i = 0;
            int n = raw.Length;

            bool alternate = false;
            if (i < n && raw[i] == '#') { alternate = true; i++; }

            bool hasPrecision = false;
            int precision = 0;
            if (i < n && raw[i] == '.')
            {
                i++;
                int p = 0;
                while (i < n && raw[i] >= '0' && raw[i] <= '9')
                {
                    p = p * 10 + (raw[i] - '0');
                    i++;
                }
                hasPrecision = true;
                precision = p;
            }

            FormatKind kind = FormatKind.Default;
            bool upper = false;
            if (i < n)
            {
                char t = raw[i++];
                switch (t)
                {
                    case 'f': kind = FormatKind.Float; break;
                    case 'F': kind = FormatKind.Float; upper = true; break;
                    case 'x': kind = FormatKind.Hex; break;
                    case 'X': kind = FormatKind.Hex; upper = true; break;
                    case 'b': kind = FormatKind.Binary; break;
                    case 'B': kind = FormatKind.Binary; upper = true; break;
                    case 'd': case 'D': kind = FormatKind.Decimal; break;
                    case 'o': kind = FormatKind.Octal; break;
                    case 'O': kind = FormatKind.Octal; upper = true; break;
                    case 'e': kind = FormatKind.Exponential; break;
                    case 'E': kind = FormatKind.Exponential; upper = true; break;
                    case 'g': kind = FormatKind.General; break;
                    case 'G': kind = FormatKind.General; upper = true; break;
                    case '%': kind = FormatKind.Percent; break;
                }
            }

            return new FormatSpec(kind, alternate, hasPrecision, precision, upper);
        }

        public bool IsDefault => Kind == FormatKind.Default && !HasPrecision && !AlternateForm;

        // Pack the whole spec into a single non-negative int so the IR can
        // intern it as a plain integer constant (OP_FMT's `c` operand indexes
        // the const pool). Layout:
        //   bits 0-3  : Kind   (0..8)
        //   bit  4    : AlternateForm
        //   bit  5    : HasPrecision
        //   bit  6    : UpperCase
        //   bits 8-30 : Precision (23 bits, far beyond any real format width)
        // Round-trips losslessly via Unpack — no re-parse of the textual spec
        // at runtime (the whole point of pre-parsing on the node).
        public int Pack()
        {
            int v = ((int)Kind) & 0xF;
            if (AlternateForm) v |= 1 << 4;
            if (HasPrecision)  v |= 1 << 5;
            if (UpperCase)     v |= 1 << 6;
            v |= (Precision & 0x7FFFFF) << 8;
            return v;
        }

        public static FormatSpec Unpack(int v)
        {
            var kind = (FormatKind)(v & 0xF);
            bool alt = (v & (1 << 4)) != 0;
            bool hasPrec = (v & (1 << 5)) != 0;
            bool upper = (v & (1 << 6)) != 0;
            int precision = (v >> 8) & 0x7FFFFF;
            return new FormatSpec(kind, alt, hasPrec, precision, upper);
        }

        // Pretty-print used by diagnostics. Reconstructs the canonical form
        // from the parsed pieces so the user sees the spec exactly as they
        // would re-write it.
        public override string ToString()
        {
            if (IsDefault) return string.Empty;
            var sb = new System.Text.StringBuilder(8);
            if (AlternateForm) sb.Append('#');
            if (HasPrecision) { sb.Append('.'); sb.Append(Precision); }
            switch (Kind)
            {
                case FormatKind.Float:       sb.Append(UpperCase ? 'F' : 'f'); break;
                case FormatKind.Hex:         sb.Append(UpperCase ? 'X' : 'x'); break;
                case FormatKind.Binary:      sb.Append(UpperCase ? 'B' : 'b'); break;
                case FormatKind.Octal:       sb.Append(UpperCase ? 'O' : 'o'); break;
                case FormatKind.Decimal:     sb.Append('d'); break;
                case FormatKind.Exponential: sb.Append(UpperCase ? 'E' : 'e'); break;
                case FormatKind.General:     sb.Append(UpperCase ? 'G' : 'g'); break;
                case FormatKind.Percent:     sb.Append('%'); break;
            }
            return sb.ToString();
        }
    }
}

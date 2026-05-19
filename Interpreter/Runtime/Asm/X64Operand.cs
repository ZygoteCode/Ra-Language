using System;
using System.Globalization;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    internal enum OperandKind { Register, Immediate, Memory, Label, StringLiteral }

    internal enum MemSizeHint { None, Byte, Word, Dword, Qword, OWord }

    internal sealed class X64Operand
    {
        public OperandKind Kind;

        public RegRef Reg;

        public long Imm;
        public bool ImmIsSigned;

        public RegRef MemBase;
        public bool HasMemBase;
        public RegRef MemIndex;
        public bool HasMemIndex;
        public byte MemScale = 1;
        public long MemDisp;
        public string? MemRipLabel;
        public MemSizeHint MemSize;
        public bool MemIsRipRelative;

        public string LabelName = "";

        public byte[] StringBytes = Array.Empty<byte>();

        public static X64Operand FromRegister(RegRef r) => new X64Operand { Kind = OperandKind.Register, Reg = r };
        public static X64Operand FromImmediate(long v) => new X64Operand { Kind = OperandKind.Immediate, Imm = v };
        public static X64Operand FromLabel(string name) => new X64Operand { Kind = OperandKind.Label, LabelName = name };
        public static X64Operand FromString(byte[] bytes) => new X64Operand { Kind = OperandKind.StringLiteral, StringBytes = bytes };
    }

    internal static class X64OperandParser
    {
        public static X64Operand Parse(string raw, int lineNumber, string lineText)
        {
            string text = raw.Trim();
            if (text.Length == 0) throw new AsmAssembleException(lineNumber, lineText, "empty operand");

            MemSizeHint memHint = MemSizeHint.None;
            string lower = text.ToLowerInvariant();
            string[] prefixes = { "byte ptr", "word ptr", "dword ptr", "qword ptr", "oword ptr", "xmmword ptr", "byte", "word", "dword", "qword", "oword", "xmmword" };
            foreach (var prefix in prefixes)
            {
                if (lower.StartsWith(prefix + " ") || lower.StartsWith(prefix + "\t") || (lower.StartsWith(prefix) && text.Length > prefix.Length && text[prefix.Length] == '['))
                {
                    memHint = prefix switch
                    {
                        var p when p.StartsWith("byte") => MemSizeHint.Byte,
                        var p when p.StartsWith("word") && !p.StartsWith("dword") && !p.StartsWith("qword") && !p.StartsWith("oword") && !p.StartsWith("xmmword") => MemSizeHint.Word,
                        var p when p.StartsWith("dword") => MemSizeHint.Dword,
                        var p when p.StartsWith("qword") => MemSizeHint.Qword,
                        _ => MemSizeHint.OWord,
                    };
                    text = text.Substring(prefix.Length).TrimStart();
                    if (text.StartsWith("ptr", StringComparison.OrdinalIgnoreCase))
                        text = text.Substring(3).TrimStart();
                    break;
                }
            }

            if (text.StartsWith("[") && text.EndsWith("]"))
            {
                var inner = text.Substring(1, text.Length - 2).Trim();
                return ParseMemory(inner, memHint, lineNumber, lineText);
            }

            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                var bytes = DecodeStringLiteral(text.Substring(1, text.Length - 2));
                return X64Operand.FromString(bytes);
            }

            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'' && text.Length > 3)
            {
                var bytes = DecodeStringLiteral(text.Substring(1, text.Length - 2));
                return X64Operand.FromString(bytes);
            }

            if (X64Registers.TryParse(text, out var reg))
                return X64Operand.FromRegister(reg);

            if (TryParseNumber(text, out long imm))
                return X64Operand.FromImmediate(imm);

            if (text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_' || text[0] == '.'))
                return X64Operand.FromLabel(text);

            throw new AsmAssembleException(lineNumber, lineText, $"unable to parse operand '{raw}'");
        }

        private static byte[] DecodeStringLiteral(string body)
        {
            var ms = new System.IO.MemoryStream();
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    char esc = body[i + 1];
                    switch (esc)
                    {
                        case 'n': ms.WriteByte((byte)'\n'); i++; break;
                        case 't': ms.WriteByte((byte)'\t'); i++; break;
                        case 'r': ms.WriteByte((byte)'\r'); i++; break;
                        case '0': ms.WriteByte(0); i++; break;
                        case '\\': ms.WriteByte((byte)'\\'); i++; break;
                        case '"': ms.WriteByte((byte)'"'); i++; break;
                        case '\'': ms.WriteByte((byte)'\''); i++; break;
                        case 'x':
                            if (i + 3 < body.Length)
                            {
                                var hex = body.Substring(i + 2, 2);
                                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var b))
                                { ms.WriteByte(b); i += 3; break; }
                            }
                            ms.WriteByte((byte)esc); i++;
                            break;
                        default:
                            ms.WriteByte((byte)esc); i++;
                            break;
                    }
                }
                else
                {
                    foreach (var b in System.Text.Encoding.UTF8.GetBytes(new[] { c })) ms.WriteByte(b);
                }
            }
            return ms.ToArray();
        }

        public static bool TryParseNumber(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;

            bool negative = false;
            int i = 0;
            if (text[0] == '+' || text[0] == '-')
            {
                negative = text[0] == '-';
                i = 1;
            }

            if (i >= text.Length) return false;

            string body = text.Substring(i);

            if (body.Length > 2 && body[0] == '0' && (body[1] == 'x' || body[1] == 'X'))
            {
                if (ulong.TryParse(body.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                {
                    value = unchecked((long)u);
                    if (negative) value = -value;
                    return true;
                }
                return false;
            }

            if (body.Length > 2 && body[0] == '0' && (body[1] == 'b' || body[1] == 'B'))
            {
                long acc = 0;
                for (int k = 2; k < body.Length; k++)
                {
                    char c = body[k];
                    if (c == '_') continue;
                    if (c != '0' && c != '1') return false;
                    acc = (acc << 1) | (c == '1' ? 1L : 0L);
                }
                value = negative ? -acc : acc;
                return true;
            }

            if (body.Length >= 3 && body[0] == '\'' && body[body.Length - 1] == '\'')
            {
                if (body.Length == 3)
                {
                    value = body[1];
                    if (negative) value = -value;
                    return true;
                }
                return false;
            }

            if (long.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd))
            {
                value = negative ? -sd : sd;
                return true;
            }

            return false;
        }

        private static X64Operand ParseMemory(string inner, MemSizeHint hint, int lineNumber, string lineText)
        {
            var op = new X64Operand { Kind = OperandKind.Memory, MemSize = hint };

            inner = inner.Replace("\t", " ").Trim();

            string normalized = inner.Replace(" - ", " + -").Replace("-", " + -");
            var parts = normalized.Split('+');

            foreach (var rawPart in parts)
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;

                if (part.Contains('*'))
                {
                    var idxParts = part.Split('*');
                    if (idxParts.Length != 2) throw new AsmAssembleException(lineNumber, lineText, $"invalid index expression '{part}'");
                    var a = idxParts[0].Trim();
                    var b = idxParts[1].Trim();

                    RegRef idxReg;
                    long scaleVal;

                    if (X64Registers.TryParse(a, out idxReg) && TryParseNumber(b, out scaleVal)) { }
                    else if (X64Registers.TryParse(b, out idxReg) && TryParseNumber(a, out scaleVal)) { }
                    else
                        throw new AsmAssembleException(lineNumber, lineText, $"invalid index expression '{part}'");

                    if (idxReg.Class != RegClass.Gpr || (idxReg.Size != RegSize.B64 && idxReg.Size != RegSize.B32))
                        throw new AsmAssembleException(lineNumber, lineText, "index register must be 32/64-bit GPR");

                    if (scaleVal != 1 && scaleVal != 2 && scaleVal != 4 && scaleVal != 8)
                        throw new AsmAssembleException(lineNumber, lineText, "scale must be 1, 2, 4, or 8");

                    if (op.HasMemIndex) throw new AsmAssembleException(lineNumber, lineText, "duplicate index register");
                    op.HasMemIndex = true;
                    op.MemIndex = idxReg;
                    op.MemScale = (byte)scaleVal;
                    continue;
                }

                if (X64Registers.TryParse(part, out var maybeReg))
                {
                    if (maybeReg.Class != RegClass.Gpr || (maybeReg.Size != RegSize.B64 && maybeReg.Size != RegSize.B32))
                        throw new AsmAssembleException(lineNumber, lineText, "memory base register must be 32/64-bit GPR");

                    if (string.Equals(part, "rip", StringComparison.OrdinalIgnoreCase))
                    {
                        op.MemIsRipRelative = true;
                        continue;
                    }

                    if (!op.HasMemBase)
                    {
                        op.HasMemBase = true;
                        op.MemBase = maybeReg;
                    }
                    else if (!op.HasMemIndex)
                    {
                        op.HasMemIndex = true;
                        op.MemIndex = maybeReg;
                        op.MemScale = 1;
                    }
                    else throw new AsmAssembleException(lineNumber, lineText, "too many registers in memory operand");
                    continue;
                }

                if (TryParseNumber(part, out long disp))
                {
                    op.MemDisp += disp;
                    continue;
                }

                op.MemRipLabel = part;
                op.MemIsRipRelative = true;
            }

            return op;
        }
    }
}

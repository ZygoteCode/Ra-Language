using System.Runtime.CompilerServices;

namespace RaLanguage.Lexer
{
    public readonly struct Position
    {
        public int Idx { get; }
        public int Ln { get; }
        public int Col { get; }
        public string Fn { get; }
        public string Ftxt { get; }

        public Position(int idx, int ln, int col, string fn, string ftxt)
        {
            Idx = idx;
            Ln = ln;
            Col = col;
            Fn = fn;
            Ftxt = ftxt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Position Advance(char? currentChar = null)
        {
            int newIdx = Idx + 1;
            int newCol = Col + 1;
            int newLn = Ln;

            if (currentChar == '\n')
            {
                newLn++;
                newCol = 0;
            }

            return new Position(newIdx, newLn, newCol, Fn, Ftxt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Position Copy() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly string ToString() => $"{Fn}:{Ln}:{Col}";
    }
}
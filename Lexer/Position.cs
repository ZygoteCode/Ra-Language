namespace RaLanguage.Lexer
{
    public class Position
    {
        public int Idx { get; set; }
        public int Ln { get; private set; }
        public int Col { get; private set; }
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

        public Position Advance(char? currentChar = null)
        {
            Idx++;
            Col++;

            if (currentChar == '\n')
            {
                Ln++;
                Col = 0;
            }
            return this;
        }

        public Position Copy()
        {
            return new Position(Idx, Ln, Col, Fn, Ftxt);
        }
    }
}
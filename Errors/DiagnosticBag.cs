using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Errors
{
    public class DiagnosticBag
    {
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.AsReadOnly();

        public bool HasErrors => _diagnostics.Exists(d => d.Severity == DiagnosticSeverity.Error);

        public bool HasWarnings => _diagnostics.Exists(d => d.Severity == DiagnosticSeverity.Warning);

        public int Count => _diagnostics.Count;

        public void Add(Diagnostic diagnostic)
        {
            _diagnostics.Add(diagnostic);
        }

        public void AddError(string message, Lexer.Position? positionStart = null, Lexer.Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Error, positionStart, positionEnd));
        }

        public void AddWarning(string message, Lexer.Position? positionStart = null, Lexer.Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Warning, positionStart, positionEnd));
        }

        public void AddInfo(string message, Lexer.Position? positionStart = null, Lexer.Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Info, positionStart, positionEnd));
        }

        public void AddRange(IEnumerable<Diagnostic> diagnostics)
        {
            _diagnostics.AddRange(diagnostics);
        }

        public void AddRange(DiagnosticBag other)
        {
            if (other != null)
            {
                _diagnostics.AddRange(other._diagnostics);
            }
        }

        public override string ToString()
        {
            if (_diagnostics.Count == 0)
                return "No diagnostics.";

            var sb = new StringBuilder();
            foreach (var diagnostic in _diagnostics)
            {
                sb.AppendLine(diagnostic.ToString());
            }
            return sb.ToString().TrimEnd();
        }

        public void Clear()
        {
            _diagnostics.Clear();
        }
    }
}

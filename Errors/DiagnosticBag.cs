using System.Collections.Generic;
using System.Text;
using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    public class DiagnosticBag
    {
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < _diagnostics.Count; i++)
                    if (_diagnostics[i].Severity == DiagnosticSeverity.Error) return true;
                return false;
            }
        }

        public bool HasWarnings
        {
            get
            {
                for (int i = 0; i < _diagnostics.Count; i++)
                    if (_diagnostics[i].Severity == DiagnosticSeverity.Warning) return true;
                return false;
            }
        }

        public int Count => _diagnostics.Count;

        public int ErrorCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < _diagnostics.Count; i++)
                    if (_diagnostics[i].Severity == DiagnosticSeverity.Error) c++;
                return c;
            }
        }

        public int WarningCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < _diagnostics.Count; i++)
                    if (_diagnostics[i].Severity == DiagnosticSeverity.Warning) c++;
                return c;
            }
        }

        public Diagnostic? FirstError
        {
            get
            {
                for (int i = 0; i < _diagnostics.Count; i++)
                    if (_diagnostics[i].Severity == DiagnosticSeverity.Error) return _diagnostics[i];
                return null;
            }
        }

        public void Add(Diagnostic diagnostic)
        {
            if (diagnostic == null) return;
            _diagnostics.Add(diagnostic);
        }

        public void AddError(string message, Position? positionStart = null, Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Error, positionStart, positionEnd));
        }

        public void AddError(string title, DiagnosticCode code, Position positionStart, Position positionEnd,
            DiagnosticPhase phase = DiagnosticPhase.Unknown, string? help = null, string? primaryLabel = null, string? category = null)
        {
            _diagnostics.Add(new Diagnostic(
                title: title,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(positionStart, positionEnd),
                code: code,
                phase: phase,
                category: category,
                help: help,
                primaryLabel: primaryLabel));
        }

        public void AddWarning(string message, Position? positionStart = null, Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Warning, positionStart, positionEnd));
        }

        public void AddInfo(string message, Position? positionStart = null, Position? positionEnd = null)
        {
            _diagnostics.Add(new Diagnostic(message, DiagnosticSeverity.Info, positionStart, positionEnd));
        }

        public void AddRange(IEnumerable<Diagnostic> diagnostics)
        {
            if (diagnostics == null) return;
            _diagnostics.AddRange(diagnostics);
        }

        public void AddRange(DiagnosticBag other)
        {
            if (other != null) _diagnostics.AddRange(other._diagnostics);
        }

        public string Render()
        {
            if (_diagnostics.Count == 0) return string.Empty;
            return DiagnosticRenderer.RenderMany(_diagnostics);
        }

        public override string ToString()
        {
            if (_diagnostics.Count == 0) return "No diagnostics.";
            return Render();
        }

        public void Clear() => _diagnostics.Clear();

        public string Summary()
        {
            int e = ErrorCount;
            int w = WarningCount;
            if (e == 0 && w == 0) return "no diagnostics";
            var sb = new StringBuilder();
            if (e > 0) sb.Append(e).Append(" error").Append(e == 1 ? "" : "s");
            if (w > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(w).Append(" warning").Append(w == 1 ? "" : "s");
            }
            return sb.ToString();
        }
    }
}

using System.Collections.Generic;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class RuntimeError : Error
    {
        public Context Context { get; }

        public RuntimeError(Position positionStart, Position positionEnd, string details, Context context)
            : base(BuildDiagnostic(positionStart, positionEnd, details, context, DiagnosticCode.RuntimeGeneric, null, null, null))
        {
            Context = context;
        }

        public RuntimeError(Position positionStart, Position positionEnd, string details, Context context,
            DiagnosticCode code, string? help = null, string? primaryLabel = null, string? category = null)
            : base(BuildDiagnostic(positionStart, positionEnd, details, context, code, help, primaryLabel, category))
        {
            Context = context;
        }

        private static Diagnostic BuildDiagnostic(
            Position s, Position e, string details, Context context,
            DiagnosticCode code, string? help, string? primaryLabel, string? category)
        {
            return new Diagnostic(
                title: string.IsNullOrEmpty(details) ? "runtime error" : details,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(s, e),
                code: code.IsEmpty ? DiagnosticCode.RuntimeGeneric : code,
                phase: DiagnosticPhase.Runtime,
                category: category ?? "Runtime Error",
                help: help,
                primaryLabel: primaryLabel,
                traceback: BuildTraceback(s, context));
        }

        private static IReadOnlyList<TracebackFrame> BuildTraceback(Position primaryPos, Context? ctx)
        {
            var frames = new List<TracebackFrame>(4);
            var pos = primaryPos;
            var current = ctx;
            while (current != null)
            {
                var span = new SourceSpan(pos, pos);
                var frame = new TracebackFrame(current.DisplayName, span);
                if (frames.Count == 0 || !FramesMatch(frames[frames.Count - 1], frame))
                {
                    frames.Add(frame);
                }
                pos = current.ParentEntryPos ?? pos;
                current = current.Parent;
            }
            return frames;
        }

        private static bool FramesMatch(TracebackFrame a, TracebackFrame b)
        {
            return string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
                && a.Span.Start.Idx == b.Span.Start.Idx
                && string.Equals(a.Span.Start.Fn, b.Span.Start.Fn, StringComparison.Ordinal);
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace RaLanguage.Interpreter.Archive
{
    // Pretty-printer for the `--inspect-archive` CLI flag. Reports
    // header + manifest + per-section directory entries. Intentionally
    // verbose so a user diagnosing a build artifact can see everything
    // the format encodes.
    public static class RacInspector
    {
        public static string Describe(string path)
        {
            using var archive = RacReader.Open(path);
            var sb = new StringBuilder();
            sb.AppendLine($"Ra Archive: {path}");
            sb.AppendLine(new string('=', 70));
            var h = archive.Header;
            sb.AppendLine($"Format            : {h.FormatMajor}.{h.FormatMinor}");
            sb.AppendLine($"Runtime required  : {RacHeader.FormatSemver(h.RaRuntimeRequired)}");
            sb.AppendLine($"Runtime built     : {RacHeader.FormatSemver(h.RaRuntimeBuiltWith)}");
            sb.AppendLine($"Flags             : {h.Flags}");
            sb.AppendLine($"Sections          : {h.SectionCount}");
            sb.AppendLine($"Section table off : {h.SectionTableOffset}");
            sb.AppendLine($"Manifest offset   : {h.ManifestOffset}");
            sb.AppendLine($"Directory SHA-256 : {RacIntegrity.FormatHex(h.DirectoryHash)}");
            sb.AppendLine();

            var m = archive.Manifest;
            sb.AppendLine("Manifest");
            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"  Built by       : {m.BuiltBy}");
            sb.AppendLine($"  Built on host  : {m.BuildHost}");
            sb.AppendLine($"  Built at (UTC) : {new DateTime(m.BuildTimeTicks, DateTimeKind.Utc):o}");
            sb.AppendLine($"  Modules        : {m.Modules.Count}");
            sb.AppendLine($"  Entry index    : {m.EntryModuleIndex}");
            sb.AppendLine($"  Std references : {m.StdReferences.Count}");
            sb.AppendLine();

            sb.AppendLine("Modules");
            sb.AppendLine(new string('-', 70));
            for (int i = 0; i < m.Modules.Count; i++)
            {
                var r = m.Modules[i];
                sb.AppendLine($"  [{i:D3}] kind={r.Kind} logical='{r.LogicalPath}'");
                sb.AppendLine($"        virtual='{r.AbsoluteVirtualPath}'");
                sb.AppendLine($"        source-section={r.SourceSectionIndex}  bytecode-section={r.BytecodeSectionIndex}");
                sb.AppendLine($"        source SHA-256 = {RacIntegrity.FormatHex(r.SourceHash)}");
                if (r.Imports.Count > 0)
                {
                    sb.Append("        imports = [");
                    for (int k = 0; k < r.Imports.Count; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        sb.Append(r.Imports[k]);
                    }
                    sb.AppendLine("]");
                }
            }
            sb.AppendLine();

            sb.AppendLine("Sections");
            sb.AppendLine(new string('-', 70));
            for (int i = 0; i < archive.Sections.Count; i++)
            {
                var s = archive.Sections[i];
                double ratio = s.UncompressedSize == 0 ? 1.0 : (double)s.StoredSize / s.UncompressedSize;
                sb.AppendLine(
                    $"  [{i:D3}] {s.Kind,-14} flags={s.Flags,-30} off={s.Offset,-10} stored={s.StoredSize,-10} raw={s.UncompressedSize,-10} ratio={ratio:F2}");
            }

            if (m.StdReferences.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Std references");
                sb.AppendLine(new string('-', 70));
                foreach (var s in m.StdReferences) sb.AppendLine($"  {s}");
            }

            return sb.ToString();
        }
    }
}

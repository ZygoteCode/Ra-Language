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
                string codecTag = s.IsCompressed ? s.Codec.ToString() : "-";
                sb.AppendLine(
                    $"  [{i:D3}] {s.Kind,-14} flags={(uint)s.Flags,-4:X} codec={codecTag,-7} mu={(s.MustUnderstand ? "y" : "n")} off={s.Offset,-10} stored={s.StoredSize,-10} raw={s.UncompressedSize,-10} ratio={ratio:F2}");
            }

            if (m.StdReferences.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Std references");
                sb.AppendLine(new string('-', 70));
                foreach (var s in m.StdReferences) sb.AppendLine($"  {s}");
            }

            // v1.1 (#7) shared constant pool stats.
            try
            {
                var pool = archive.SharedConstPool;
                if (pool != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Shared const pool (v1.1)");
                    sb.AppendLine(new string('-', 70));
                    sb.AppendLine($"  strings : {pool.Strings.Count}");
                    sb.AppendLine($"  numbers : {pool.Numbers.Count}");
                    sb.AppendLine($"  ints    : {pool.Integers.Count}");
                    sb.AppendLine($"  longs   : {pool.Longs.Count}");
                    sb.AppendLine($"  doubles : {pool.Doubles.Count}");
                    sb.AppendLine($"  floats  : {pool.Floats.Count}");
                    sb.AppendLine($"  total   : {pool.TotalEntries}");
                    int preview = System.Math.Min(pool.Strings.Count, 5);
                    if (preview > 0)
                    {
                        sb.Append("  sample  : ");
                        for (int k = 0; k < preview; k++)
                        {
                            if (k > 0) sb.Append(", ");
                            string raw = pool.Strings[k] ?? "";
                            string shown = raw.Length <= 24 ? raw : raw.Substring(0, 24) + "…";
                            sb.Append('"').Append(shown.Replace("\"", "\\\"")).Append('"');
                        }
                        sb.AppendLine();
                    }
                }
            }
            catch (System.Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine($"Shared const pool decode failed: {ex.Message}");
            }

            // v1.1 (#6) tree-shake report. Lives inside the StdLibIndex
            // section payload — decoded lazily so older archives without
            // the tagged form still print correctly.
            for (int i = 0; i < archive.Sections.Count; i++)
            {
                if (archive.Sections[i].Kind != RacSectionKind.StdLibIndex) continue;
                StdLibIndexSection.Decoded? decoded = null;
                try { decoded = StdLibIndexSection.Decode(archive.ReadSection(i)); }
                catch (System.Exception ex)
                {
                    sb.AppendLine();
                    sb.AppendLine($"StdLibIndex (#{i}) decode failed: {ex.Message}");
                }
                if (decoded != null && decoded.HasShakeReport && decoded.ShakenModules.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Tree-shake (v1.1)");
                    sb.AppendLine(new string('-', 70));
                    int totBefore = 0, totAfter = 0, totKept = 0, totDropped = 0;
                    foreach (var sm in decoded.ShakenModules)
                    {
                        totBefore += sm.BytesBefore;
                        totAfter += sm.BytesAfter;
                        totKept += sm.Kept.Count;
                        totDropped += sm.Dropped.Count;
                    }
                    sb.AppendLine(
                        $"  modules: {decoded.ShakenModules.Count}   "
                        + $"kept: {totKept}   dropped: {totDropped}   "
                        + $"size: {totBefore:N0} → {totAfter:N0} bytes  "
                        + $"(-{totBefore - totAfter:N0})");
                    foreach (var sm in decoded.ShakenModules)
                    {
                        if (sm.Dropped.Count == 0 && sm.Kept.Count == 0) continue;
                        sb.AppendLine($"  [{sm.Path}]   "
                            + $"kept={sm.Kept.Count}  dropped={sm.Dropped.Count}  "
                            + $"size: {sm.BytesBefore:N0} → {sm.BytesAfter:N0}");
                        if (sm.Kept.Count > 0)
                            sb.AppendLine($"    kept:    {string.Join(", ", sm.Kept)}");
                        if (sm.Dropped.Count > 0)
                            sb.AppendLine($"    dropped: {string.Join(", ", sm.Dropped)}");
                    }
                }
                break;
            }

            return sb.ToString();
        }
    }
}

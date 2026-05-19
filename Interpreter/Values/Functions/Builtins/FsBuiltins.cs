using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class FsBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("fs_exists", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_exists", args, 1, ctx, p1, p2, out var err)) return err;
                var p = AsString(args[0]);
                return Ok(MakeBool(File.Exists(p) || Directory.Exists(p)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_is_file", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_is_file", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(File.Exists(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_is_dir", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_is_dir", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(Directory.Exists(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_is_symlink", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_is_symlink", args, 1, ctx, p1, p2, out var err)) return err;
                var p = AsString(args[0]);
                try
                {
                    var fi = new FileInfo(p);
                    return Ok(MakeBool((fi.Attributes & FileAttributes.ReparsePoint) != 0), ctx, p1, p2);
                }
                catch { return Ok(MakeBool(false), ctx, p1, p2); }
            });
            BuiltInRegistry.Register("fs_read_text", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_read_text", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new StringValue(File.ReadAllText(AsString(args[0]), Encoding.UTF8)), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_read_text: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_write_text", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_write_text", args, 2, ctx, p1, p2, out var err)) return err;
                try { File.WriteAllText(AsString(args[0]), AsString(args[1]), Encoding.UTF8); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_write_text: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_append_text", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_append_text", args, 2, ctx, p1, p2, out var err)) return err;
                try { File.AppendAllText(AsString(args[0]), AsString(args[1]), Encoding.UTF8); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_append_text: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_read_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_read_bytes", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var bytes = File.ReadAllBytes(AsString(args[0]));
                    var list = new List<RuntimeValue>(bytes.Length);
                    foreach (var b in bytes) list.Add(new ByteValue(b));
                    return Ok(new ListValue(list), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_read_bytes: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_write_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_write_bytes", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[1] is not ListValue lv) return Fail(ctx, p1, p2, "fs_write_bytes: second arg must be a list of bytes");
                var bytes = new byte[lv.Elements.Count];
                for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsInt(lv.Elements[i]) & 0xff);
                try { File.WriteAllBytes(AsString(args[0]), bytes); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_write_bytes: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_read_lines", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_read_lines", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var lines = File.ReadAllLines(AsString(args[0]), Encoding.UTF8);
                    return Ok(new ListValue(Strings(lines)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_read_lines: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_size", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_size", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new LongValue(new FileInfo(AsString(args[0])).Length), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_size: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_mtime", (ctx, args, p1, p2) => StatTime(ctx, args, p1, p2, "fs_mtime", File.GetLastWriteTimeUtc, Directory.GetLastWriteTimeUtc));
            BuiltInRegistry.Register("fs_ctime", (ctx, args, p1, p2) => StatTime(ctx, args, p1, p2, "fs_ctime", File.GetCreationTimeUtc, Directory.GetCreationTimeUtc));
            BuiltInRegistry.Register("fs_atime", (ctx, args, p1, p2) => StatTime(ctx, args, p1, p2, "fs_atime", File.GetLastAccessTimeUtc, Directory.GetLastAccessTimeUtc));
            BuiltInRegistry.Register("fs_create_dir", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_create_dir", args, 1, ctx, p1, p2, out var err)) return err;
                try { Directory.CreateDirectory(AsString(args[0])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_create_dir: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_create_dirs", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_create_dirs", args, 1, ctx, p1, p2, out var err)) return err;
                try { Directory.CreateDirectory(AsString(args[0])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_create_dirs: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_remove", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_remove", args, 1, ctx, p1, p2, out var err)) return err;
                try { File.Delete(AsString(args[0])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_remove: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_remove_dir", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("fs_remove_dir", args, 1, 2, ctx, p1, p2, out var err)) return err;
                bool recursive = args.Count == 2 && AsBool(args[1]);
                try { Directory.Delete(AsString(args[0]), recursive); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_remove_dir: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_copy", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("fs_copy", args, 2, 3, ctx, p1, p2, out var err)) return err;
                bool overwrite = args.Count == 3 && AsBool(args[2]);
                try { File.Copy(AsString(args[0]), AsString(args[1]), overwrite); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_copy: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_move", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_move", args, 2, ctx, p1, p2, out var err)) return err;
                try { File.Move(AsString(args[0]), AsString(args[1])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_move: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_rename", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_rename", args, 2, ctx, p1, p2, out var err)) return err;
                try { File.Move(AsString(args[0]), AsString(args[1])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_rename: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_list_dir", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_list_dir", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var entries = Directory.GetFileSystemEntries(AsString(args[0]));
                    return Ok(new ListValue(Strings(entries.Select(Path.GetFileName)!)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_list_dir: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_list_files", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_list_files", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var files = Directory.GetFiles(AsString(args[0]));
                    return Ok(new ListValue(Strings(files.Select(Path.GetFileName)!)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_list_files: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_list_dirs", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_list_dirs", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var dirs = Directory.GetDirectories(AsString(args[0]));
                    return Ok(new ListValue(Strings(dirs.Select(Path.GetFileName)!)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_list_dirs: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_walk", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_walk", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var entries = Directory.GetFileSystemEntries(AsString(args[0]), "*", SearchOption.AllDirectories);
                    return Ok(new ListValue(Strings(entries)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_walk: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_glob", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("fs_glob", args, 1, 2, ctx, p1, p2, out var err)) return err;
                string dir = args.Count == 2 ? AsString(args[0]) : ".";
                string pattern = args.Count == 2 ? AsString(args[1]) : AsString(args[0]);
                try
                {
                    var matches = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
                    return Ok(new ListValue(Strings(matches)), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_glob: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_abs", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_abs", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetFullPath(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_canonicalize", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_canonicalize", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetFullPath(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_basename", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_basename", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetFileName(AsString(args[0])) ?? ""), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_dirname", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_dirname", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetDirectoryName(AsString(args[0])) ?? ""), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_extension", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_extension", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetExtension(AsString(args[0])) ?? ""), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_stem", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_stem", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.GetFileNameWithoutExtension(AsString(args[0])) ?? ""), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_join", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("fs_join", args, 2, ctx, p1, p2, out var err)) return err;
                var parts = args.Select(a => AsString(a)).ToArray();
                return Ok(new StringValue(Path.Combine(parts)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_split", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_split", args, 1, ctx, p1, p2, out var err)) return err;
                var p = AsString(args[0]);
                return Ok(new TupleValue(new List<RuntimeValue> {
                    new StringValue(Path.GetDirectoryName(p) ?? ""),
                    new StringValue(Path.GetFileName(p) ?? "")
                }), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_is_absolute", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_is_absolute", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(Path.IsPathRooted(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("fs_temp_file", (ctx, args, p1, p2) =>
            {
                try { return Ok(new StringValue(Path.GetTempFileName()), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_temp_file: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_temp_dir_new", (ctx, args, p1, p2) =>
            {
                try
                {
                    var d = Path.Combine(Path.GetTempPath(), "ra_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(d);
                    return Ok(new StringValue(d), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"fs_temp_dir_new: {ex.Message}"); }
            });
            BuiltInRegistry.Register("fs_change_ext", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("fs_change_ext", args, 2, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Path.ChangeExtension(AsString(args[0]), AsString(args[1])) ?? ""), ctx, p1, p2);
            });
        }

        private static RuntimeResult StatTime(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string name, Func<string, DateTime> fileF, Func<string, DateTime> dirF)
        {
            if (!ExpectArgs(name, args, 1, ctx, p1, p2, out var err)) return err;
            try
            {
                var p = AsString(args[0]);
                var dt = File.Exists(p) ? fileF(p) : Directory.Exists(p) ? dirF(p) : DateTime.MinValue;
                if (dt == DateTime.MinValue) return OkNull(ctx, p1, p2);
                return Ok(new LongValue(new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds()), ctx, p1, p2);
            }
            catch (Exception ex) { return Fail(ctx, p1, p2, $"{name}: {ex.Message}"); }
        }
    }
}

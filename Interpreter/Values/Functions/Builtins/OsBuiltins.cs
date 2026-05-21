using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class OsBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("os_name", (ctx, args, p1, p2) =>
            {
                if (args.Count != 0) return Fail(ctx, p1, p2, "os_name takes no args");
                string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? "freebsd"
                    : "other";
                return Ok(new StringValue(name), ctx, p1, p2);
            });
            BuiltInRegistry.Register("os_version", (ctx, args, p1, p2) =>
                Ok(new StringValue(RuntimeInformation.OSDescription), ctx, p1, p2));
            BuiltInRegistry.Register("os_arch", (ctx, args, p1, p2) =>
                Ok(new StringValue(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()), ctx, p1, p2));
            BuiltInRegistry.Register("os_process_arch", (ctx, args, p1, p2) =>
                Ok(new StringValue(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()), ctx, p1, p2));
            BuiltInRegistry.Register("os_runtime", (ctx, args, p1, p2) =>
                Ok(new StringValue(RuntimeInformation.FrameworkDescription), ctx, p1, p2));
            BuiltInRegistry.Register("os_hostname", (ctx, args, p1, p2) =>
            {
                try { return Ok(new StringValue(Environment.MachineName), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("os_username", (ctx, args, p1, p2) =>
            {
                try { return Ok(new StringValue(Environment.UserName), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("os_userdomain", (ctx, args, p1, p2) =>
            {
                try { return Ok(new StringValue(Environment.UserDomainName), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("os_processor_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(Environment.ProcessorCount), ctx, p1, p2));
            BuiltInRegistry.Register("os_uptime_ms", (ctx, args, p1, p2) =>
                Ok(new LongValue(Environment.TickCount64), ctx, p1, p2));
            BuiltInRegistry.Register("os_is_64bit", (ctx, args, p1, p2) =>
                Ok(MakeBool(Environment.Is64BitOperatingSystem), ctx, p1, p2));
            BuiltInRegistry.Register("os_process_is_64bit", (ctx, args, p1, p2) =>
                Ok(MakeBool(Environment.Is64BitProcess), ctx, p1, p2));
            BuiltInRegistry.Register("os_endian", (ctx, args, p1, p2) =>
                Ok(new StringValue(BitConverter.IsLittleEndian ? "little" : "big"), ctx, p1, p2));
            BuiltInRegistry.Register("os_newline", (ctx, args, p1, p2) =>
                Ok(new StringValue(Environment.NewLine), ctx, p1, p2));
            BuiltInRegistry.Register("os_path_sep", (ctx, args, p1, p2) =>
                Ok(new StringValue(System.IO.Path.DirectorySeparatorChar.ToString()), ctx, p1, p2));
            BuiltInRegistry.Register("os_path_list_sep", (ctx, args, p1, p2) =>
                Ok(new StringValue(System.IO.Path.PathSeparator.ToString()), ctx, p1, p2));

            BuiltInRegistry.Register("env_get", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("env_get", args, 1, ctx, p1, p2, out var err)) return err;
                var v = Environment.GetEnvironmentVariable(AsString(args[0]));
                return v == null ? OkNull(ctx, p1, p2) : Ok(new StringValue(v), ctx, p1, p2);
            });
            BuiltInRegistry.Register("env_set", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("env_set", args, 2, ctx, p1, p2, out var err)) return err;
                Environment.SetEnvironmentVariable(AsString(args[0]), AsString(args[1]));
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("env_unset", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("env_unset", args, 1, ctx, p1, p2, out var err)) return err;
                Environment.SetEnvironmentVariable(AsString(args[0]), null);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("env_has", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("env_has", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(MakeBool(Environment.GetEnvironmentVariable(AsString(args[0])) != null), ctx, p1, p2);
            });
            BuiltInRegistry.Register("env_all", (ctx, args, p1, p2) =>
            {
                var pairs = new List<(RuntimeValue, RuntimeValue)>();
                foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
                    pairs.Add((new StringValue(kv.Key?.ToString() ?? ""), new StringValue(kv.Value?.ToString() ?? "")));
                return Ok(new MapValue(pairs), ctx, p1, p2);
            });
            BuiltInRegistry.Register("args", (ctx, args, p1, p2) =>
            {
                var list = new List<RuntimeValue>();
                foreach (var a in Environment.GetCommandLineArgs()) list.Add(new StringValue(a));
                return Ok(new ListValue(list), ctx, p1, p2);
            });
            BuiltInRegistry.Register("cwd", (ctx, args, p1, p2) =>
                Ok(new StringValue(Environment.CurrentDirectory), ctx, p1, p2));
            BuiltInRegistry.Register("chdir", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("chdir", args, 1, ctx, p1, p2, out var err)) return err;
                try { Environment.CurrentDirectory = AsString(args[0]); return Ok(BooleanValue.Of(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"chdir: {ex.Message}"); }
            });
            BuiltInRegistry.Register("home_dir", (ctx, args, p1, p2) =>
                Ok(new StringValue(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), ctx, p1, p2));
            BuiltInRegistry.Register("temp_dir", (ctx, args, p1, p2) =>
                Ok(new StringValue(System.IO.Path.GetTempPath()), ctx, p1, p2));
            BuiltInRegistry.Register("exe_path", (ctx, args, p1, p2) =>
                Ok(new StringValue(Environment.ProcessPath ?? ""), ctx, p1, p2));
            BuiltInRegistry.Register("system_dir", (ctx, args, p1, p2) =>
                Ok(new StringValue(Environment.SystemDirectory ?? ""), ctx, p1, p2));
            BuiltInRegistry.Register("exit", (ctx, args, p1, p2) =>
            {
                int code = args.Count >= 1 ? AsInt(args[0]) : 0;
                Environment.Exit(code);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("os_special_folder", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("os_special_folder", args, 1, ctx, p1, p2, out var err)) return err;
                if (Enum.TryParse<Environment.SpecialFolder>(AsString(args[0]), true, out var sf))
                    return Ok(new StringValue(Environment.GetFolderPath(sf)), ctx, p1, p2);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("os_total_memory", (ctx, args, p1, p2) =>
            {
                try
                {
                    var info = GC.GetGCMemoryInfo();
                    return Ok(new LongValue(info.TotalAvailableMemoryBytes), ctx, p1, p2);
                }
                catch { return OkNull(ctx, p1, p2); }
            });
        }
    }
}

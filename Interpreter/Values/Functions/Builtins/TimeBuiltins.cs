using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class TimeBuiltins
    {
        private static readonly Stopwatch _mono = Stopwatch.StartNew();

        public static void Register()
        {
            BuiltInRegistry.Register("now_unix_s", (ctx, args, p1, p2) =>
                Ok(new LongValue(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ctx, p1, p2));
            BuiltInRegistry.Register("now_unix_ms", (ctx, args, p1, p2) =>
                Ok(new LongValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ctx, p1, p2));
            BuiltInRegistry.Register("now_unix_us", (ctx, args, p1, p2) =>
                Ok(new LongValue(DateTimeOffset.UtcNow.UtcTicks / 10), ctx, p1, p2));
            BuiltInRegistry.Register("now_unix_ns", (ctx, args, p1, p2) =>
                Ok(new LongValue(DateTimeOffset.UtcNow.UtcTicks * 100), ctx, p1, p2));
            BuiltInRegistry.Register("now_iso", (ctx, args, p1, p2) =>
                Ok(new StringValue(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)), ctx, p1, p2));
            BuiltInRegistry.Register("now_utc_iso", (ctx, args, p1, p2) =>
                Ok(new StringValue(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)), ctx, p1, p2));
            BuiltInRegistry.Register("now_local_iso", (ctx, args, p1, p2) =>
                Ok(new StringValue(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)), ctx, p1, p2));
            BuiltInRegistry.Register("monotonic_ms", (ctx, args, p1, p2) =>
                Ok(new LongValue(_mono.ElapsedMilliseconds), ctx, p1, p2));
            BuiltInRegistry.Register("monotonic_ns", (ctx, args, p1, p2) =>
                Ok(new LongValue(_mono.Elapsed.Ticks * 100), ctx, p1, p2));
            BuiltInRegistry.Register("monotonic_us", (ctx, args, p1, p2) =>
                Ok(new LongValue(_mono.Elapsed.Ticks / 10), ctx, p1, p2));
            BuiltInRegistry.Register("sleep_ms_blocking", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("sleep_ms_blocking", args, 1, ctx, p1, p2, out var err)) return err;
                System.Threading.Thread.Sleep(Math.Max(0, AsInt(args[0])));
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("time_year", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_year", d => d.Year));
            BuiltInRegistry.Register("time_month", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_month", d => d.Month));
            BuiltInRegistry.Register("time_day", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_day", d => d.Day));
            BuiltInRegistry.Register("time_hour", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_hour", d => d.Hour));
            BuiltInRegistry.Register("time_minute", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_minute", d => d.Minute));
            BuiltInRegistry.Register("time_second", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_second", d => d.Second));
            BuiltInRegistry.Register("time_millisecond", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_millisecond", d => d.Millisecond));
            BuiltInRegistry.Register("time_weekday", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_weekday", d => (int)d.DayOfWeek));
            BuiltInRegistry.Register("time_day_of_year", (ctx, args, p1, p2) => TimeField(ctx, args, p1, p2, "time_day_of_year", d => d.DayOfYear));
            BuiltInRegistry.Register("tz_offset_minutes", (ctx, args, p1, p2) =>
                Ok(new IntegerValue((int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes), ctx, p1, p2));
            BuiltInRegistry.Register("tz_name", (ctx, args, p1, p2) =>
                Ok(new StringValue(TimeZoneInfo.Local.Id), ctx, p1, p2));
            BuiltInRegistry.Register("time_format", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("time_format", args, 2, ctx, p1, p2, out var err)) return err;
                var d = DateTimeOffset.FromUnixTimeMilliseconds(AsLong(args[0])).UtcDateTime;
                return Ok(new StringValue(d.ToString(AsString(args[1]), CultureInfo.InvariantCulture)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("time_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("time_parse", args, 2, ctx, p1, p2, out var err)) return err;
                if (DateTime.TryParseExact(AsString(args[0]), AsString(args[1]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return Ok(new LongValue(new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds()), ctx, p1, p2);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("time_iso_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("time_iso_parse", args, 1, ctx, p1, p2, out var err)) return err;
                if (DateTimeOffset.TryParse(AsString(args[0]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    return Ok(new LongValue(dto.ToUnixTimeMilliseconds()), ctx, p1, p2);
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("time_diff_ms", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("time_diff_ms", args, 2, ctx, p1, p2, out var err)) return err;
                return Ok(new LongValue(AsLong(args[1]) - AsLong(args[0])), ctx, p1, p2);
            });
            BuiltInRegistry.Register("time_add_ms", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("time_add_ms", args, 2, ctx, p1, p2, out var err)) return err;
                return Ok(new LongValue(AsLong(args[0]) + AsLong(args[1])), ctx, p1, p2);
            });

            // Calendar helpers (all UTC / invariant-culture, AOT-safe).
            BuiltInRegistry.Register("is_leap_year", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("is_leap_year", args, 1, ctx, p1, p2, out var err)) return err;
                int y = AsInt(args[0]);
                if (y < 1 || y > 9999) return Fail(ctx, p1, p2, "is_leap_year: year out of range [1, 9999]");
                return Ok(MakeBool(DateTime.IsLeapYear(y)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("days_in_month", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("days_in_month", args, 2, ctx, p1, p2, out var err)) return err;
                int y = AsInt(args[0]), m = AsInt(args[1]);
                if (y < 1 || y > 9999 || m < 1 || m > 12) return Fail(ctx, p1, p2, "days_in_month: year/month out of range");
                return Ok(new IntegerValue(DateTime.DaysInMonth(y, m)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("month_name", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("month_name", args, 1, ctx, p1, p2, out var err)) return err;
                int m = AsInt(args[0]);
                if (m < 1 || m > 12) return Fail(ctx, p1, p2, "month_name: month must be in [1, 12]");
                return Ok(new StringValue(DateTimeFormatInfo.InvariantInfo.GetMonthName(m)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("weekday_name", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("weekday_name", args, 1, ctx, p1, p2, out var err)) return err;
                int d = AsInt(args[0]);
                if (d < 0 || d > 6) return Fail(ctx, p1, p2, "weekday_name: index must be in [0, 6] (0 = Sunday)");
                return Ok(new StringValue(DateTimeFormatInfo.InvariantInfo.GetDayName((DayOfWeek)d)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("unix_to_iso", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("unix_to_iso", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(DateTimeOffset.FromUnixTimeSeconds(AsLong(args[0])).ToString("o", CultureInfo.InvariantCulture)), ctx, p1, p2);
            });
            BuiltInRegistry.Register("iso_to_unix", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("iso_to_unix", args, 1, ctx, p1, p2, out var err)) return err;
                if (!DateTimeOffset.TryParse(AsString(args[0]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    return Fail(ctx, p1, p2, "iso_to_unix: could not parse timestamp");
                return Ok(new LongValue(dto.ToUnixTimeSeconds()), ctx, p1, p2);
            });
        }

        private static RuntimeResult TimeField(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string name, Func<DateTime, int> f)
        {
            if (!ExpectArgs(name, args, 1, ctx, p1, p2, out var err)) return err;
            var d = DateTimeOffset.FromUnixTimeMilliseconds(AsLong(args[0])).UtcDateTime;
            return Ok(new IntegerValue(f(d)), ctx, p1, p2);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.net (continued) — LIVE networking: DNS, TCP, HTTP.
    //
    // 100% NativeAOT-compatible. `dotnet publish -c Release -r win-x64` runs
    // the ILC trim/AOT analysis over this file and emits ZERO IL2026/IL3050:
    // System.Net.Http (SocketsHttpHandler), System.Net.Sockets and
    // System.Net.Dns are all reflection-free under .NET 10 AOT. No trim
    // RootDescriptor or feature switch is required.
    //
    // Two flavours, both AOT-clean:
    //   * BLOCKING builtins (Dns.GetHostAddresses, TcpClient, HttpClient.Send)
    //     — a sync builtin, no async bridge.
    //   * *_async builtins return a Ra Task (via AsyncScheduler) that composes
    //     with std.prelude.async await / gather. async/await lowers to a state
    //     machine — also reflection-free, also AOT-clean.
    // Offline-deterministic paths (localhost DNS, closed ports, refused /
    // invalid-host errors, await error propagation) are unit-tested; live
    // network success is exercised by smoke only.
    public static class NetLiveBuiltins
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public static void Register()
        {
            // dns_resolve(host) -> list of IP strings (IPv4 + IPv6).
            BuiltInRegistry.Register("dns_resolve", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dns_resolve", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var addrs = Dns.GetHostAddresses(AsString(args[0]));
                    var list = new List<RuntimeValue>(addrs.Length);
                    foreach (var a in addrs) list.Add(new StringValue(a.ToString()));
                    return Ok(new ListValue(list), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, "dns_resolve: " + ex.Message); }
            });

            // dns_resolve_one(host) -> first IP string, or null on failure.
            BuiltInRegistry.Register("dns_resolve_one", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dns_resolve_one", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var addrs = Dns.GetHostAddresses(AsString(args[0]));
                    return addrs.Length == 0 ? OkNull(ctx, p1, p2) : Ok(new StringValue(addrs[0].ToString()), ctx, p1, p2);
                }
                catch { return OkNull(ctx, p1, p2); }
            });

            // dns_reverse(ip) -> hostname (PTR lookup). Needs the network.
            BuiltInRegistry.Register("dns_reverse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dns_reverse", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new StringValue(Dns.GetHostEntry(AsString(args[0])).HostName), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, "dns_reverse: " + ex.Message); }
            });

            // tcp_port_open(host, port [, timeout_ms]) -> bool. Never throws on
            // a refused/timed-out connection — it just reports false.
            BuiltInRegistry.Register("tcp_port_open", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("tcp_port_open", args, 2, 3, ctx, p1, p2, out var err)) return err;
                int timeout = args.Count == 3 ? AsInt(args[2]) : 3000;
                try
                {
                    using var c = new TcpClient();
                    if (!c.ConnectAsync(AsString(args[0]), AsInt(args[1])).Wait(timeout)) return Ok(MakeBool(false), ctx, p1, p2);
                    return Ok(MakeBool(c.Connected), ctx, p1, p2);
                }
                catch { return Ok(MakeBool(false), ctx, p1, p2); }
            });

            // tcp_request(host, port, payload [, timeout_ms]) -> response text.
            // One-shot: connect, send the UTF-8 payload, read until the peer
            // closes or the receive timeout elapses. For line/request protocols.
            BuiltInRegistry.Register("tcp_request", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("tcp_request", args, 3, 4, ctx, p1, p2, out var err)) return err;
                int timeout = args.Count == 4 ? AsInt(args[3]) : 5000;
                try
                {
                    using var c = new TcpClient();
                    if (!c.ConnectAsync(AsString(args[0]), AsInt(args[1])).Wait(timeout))
                        return Fail(ctx, p1, p2, "tcp_request: connection timed out");
                    c.ReceiveTimeout = timeout;
                    using var ns = c.GetStream();
                    byte[] data = Encoding.UTF8.GetBytes(AsString(args[2]));
                    ns.Write(data, 0, data.Length);
                    var sb = new StringBuilder();
                    var buf = new byte[8192];
                    try { int n; while ((n = ns.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
                    catch (IOException) { /* receive timeout — return what we got */ }
                    return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, "tcp_request: " + ex.Message); }
            });

            // http_get(url [, headers_map]) -> { status, ok, body, headers }
            BuiltInRegistry.Register("http_get", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_get", args, 1, 2, ctx, p1, p2, out var err)) return err;
                return DoHttp(ctx, p1, p2, "GET", AsString(args[0]), null, null, args.Count == 2 ? args[1] : null);
            });

            // http_post(url, body [, content_type] [, headers_map])
            BuiltInRegistry.Register("http_post", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_post", args, 2, 4, ctx, p1, p2, out var err)) return err;
                string contentType = args.Count >= 3 ? AsString(args[2]) : "text/plain";
                RuntimeValue? headers = args.Count == 4 ? args[3] : null;
                return DoHttp(ctx, p1, p2, "POST", AsString(args[0]), AsString(args[1]), contentType, headers);
            });

            // http_request(method, url, body, headers_map) — the general form.
            // Pass body="" and an empty map for a bare request.
            BuiltInRegistry.Register("http_request", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_request", args, 2, 4, ctx, p1, p2, out var err)) return err;
                string body = args.Count >= 3 ? AsString(args[2]) : "";
                RuntimeValue? headers = args.Count == 4 ? args[3] : null;
                string method = AsString(args[0]).ToUpperInvariant();
                string bodyArg = (method == "GET" || method == "HEAD") ? null! : body;
                return DoHttp(ctx, p1, p2, method, AsString(args[1]), bodyArg, "text/plain", headers);
            });

            // http_status(url) -> integer status code (GET), or a clean error.
            BuiltInRegistry.Register("http_status", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("http_status", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, AsString(args[0]));
                    using var resp = _http.Send(req);
                    return Ok(new IntegerValue((int)resp.StatusCode), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, "http_status: " + ex.Message); }
            });

            // http_download(url, path) -> number of bytes written to `path`.
            BuiltInRegistry.Register("http_download", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("http_download", args, 2, ctx, p1, p2, out var err)) return err;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, AsString(args[0]));
                    using var resp = _http.Send(req);
                    resp.EnsureSuccessStatusCode();
                    using var src = resp.Content.ReadAsStream();
                    using var dst = File.Create(AsString(args[1]));
                    src.CopyTo(dst);
                    return Ok(new LongValue(dst.Length), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, "http_download: " + ex.Message); }
            });

            // ---- ASYNC variants: return a Task that composes with await /
            // gather / race in std.prelude.async. The request runs on a fiber
            // (non-blocking). AOT-clean — async lowering is reflection-free.
            BuiltInRegistry.Register("http_get_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_get_async", args, 1, 2, ctx, p1, p2, out var err)) return err;
                string url = AsString(args[0]);
                RuntimeValue? headers = args.Count == 2 ? args[1] : null;
                var task = AsyncScheduler.Schedule("http_get_async", ctx?.AsyncCtx,
                    async cc => await HttpResultAsync(ctx, p1, p2, "GET", url, null, null, headers, cc.Token));
                return Ok(new TaskValue(task), ctx, p1, p2);
            });
            BuiltInRegistry.Register("http_post_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_post_async", args, 2, 4, ctx, p1, p2, out var err)) return err;
                string url = AsString(args[0]), body = AsString(args[1]);
                string ct = args.Count >= 3 ? AsString(args[2]) : "text/plain";
                RuntimeValue? headers = args.Count == 4 ? args[3] : null;
                var task = AsyncScheduler.Schedule("http_post_async", ctx?.AsyncCtx,
                    async cc => await HttpResultAsync(ctx, p1, p2, "POST", url, body, ct, headers, cc.Token));
                return Ok(new TaskValue(task), ctx, p1, p2);
            });
            BuiltInRegistry.Register("http_request_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("http_request_async", args, 2, 4, ctx, p1, p2, out var err)) return err;
                string method = AsString(args[0]).ToUpperInvariant(), url = AsString(args[1]);
                string? body = (args.Count >= 3 && method != "GET" && method != "HEAD") ? AsString(args[2]) : null;
                RuntimeValue? headers = args.Count == 4 ? args[3] : null;
                var task = AsyncScheduler.Schedule("http_request_async", ctx?.AsyncCtx,
                    async cc => await HttpResultAsync(ctx, p1, p2, method, url, body, "text/plain", headers, cc.Token));
                return Ok(new TaskValue(task), ctx, p1, p2);
            });
            BuiltInRegistry.Register("dns_resolve_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("dns_resolve_async", args, 1, ctx, p1, p2, out var err)) return err;
                string host = AsString(args[0]);
                var task = AsyncScheduler.Schedule("dns_resolve_async", ctx?.AsyncCtx, async cc =>
                {
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(host, cc.Token).ConfigureAwait(false);
                        var list = new List<RuntimeValue>(addrs.Length);
                        foreach (var a in addrs) list.Add(new StringValue(a.ToString()));
                        return new ValueResult(new ListValue(list), null);
                    }
                    catch (Exception ex) { return new ValueResult(null, new RuntimeError(p1, p2, "dns_resolve_async: " + ex.Message, ctx)); }
                });
                return Ok(new TaskValue(task), ctx, p1, p2);
            });
            BuiltInRegistry.Register("tcp_request_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("tcp_request_async", args, 3, 4, ctx, p1, p2, out var err)) return err;
                string host = AsString(args[0]), payload = AsString(args[2]);
                int port = AsInt(args[1]);
                int timeout = args.Count == 4 ? AsInt(args[3]) : 5000;
                var task = AsyncScheduler.Schedule("tcp_request_async", ctx?.AsyncCtx, async cc =>
                {
                    try
                    {
                        using var c = new TcpClient();
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cc.Token);
                        cts.CancelAfter(timeout);
                        await c.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                        using var ns = c.GetStream();
                        await ns.WriteAsync(Encoding.UTF8.GetBytes(payload), cts.Token).ConfigureAwait(false);
                        var sb = new StringBuilder();
                        var buf = new byte[8192];
                        try { int n; while ((n = await ns.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
                        catch (OperationCanceledException) { /* timeout — return what arrived */ }
                        return new ValueResult(new StringValue(sb.ToString()), null);
                    }
                    catch (Exception ex) { return new ValueResult(null, new RuntimeError(p1, p2, "tcp_request_async: " + ex.Message, ctx)); }
                });
                return Ok(new TaskValue(task), ctx, p1, p2);
            });
        }

        private static HttpRequestMessage BuildRequest(string method, string url, string? body, string? contentType, RuntimeValue? headers)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");
            if (headers is MapValue hm)
            {
                foreach (var (k, v) in hm.Pairs)
                {
                    string name = AsString(k), value = AsString(v);
                    if (!req.Headers.TryAddWithoutValidation(name, value) && req.Content != null)
                        req.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
            return req;
        }

        private static MapValue ResponseMap(HttpResponseMessage resp, string body)
        {
            var hdrs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var h in resp.Headers) hdrs.Add((new StringValue(h.Key), new StringValue(string.Join(", ", h.Value))));
            foreach (var h in resp.Content.Headers) hdrs.Add((new StringValue(h.Key), new StringValue(string.Join(", ", h.Value))));
            return new MapValue(new List<(RuntimeValue, RuntimeValue)>
            {
                (new StringValue("status"), new IntegerValue((int)resp.StatusCode)),
                (new StringValue("ok"), MakeBool(resp.IsSuccessStatusCode)),
                (new StringValue("body"), new StringValue(body)),
                (new StringValue("headers"), new MapValue(hdrs)),
            });
        }

        private static RuntimeResult DoHttp(Context ctx, Position p1, Position p2, string method, string url, string? body, string? contentType, RuntimeValue? headers)
        {
            try
            {
                using var req = BuildRequest(method, url, body, contentType, headers);
                using var resp = _http.Send(req);
                string respBody;
                using (var reader = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8)) respBody = reader.ReadToEnd();
                return Ok(ResponseMap(resp, respBody), ctx, p1, p2);
            }
            catch (Exception ex) { return Fail(ctx, p1, p2, method.ToLowerInvariant() + ": " + ex.Message); }
        }

        // Async HTTP body: awaited on a fiber so the request never blocks the
        // calling thread. AOT-clean — async/await lowers to a state machine, no
        // reflection (verified: zero IL2026/IL3050 from this file at publish).
        private static async ValueTask<ValueResult> HttpResultAsync(Context ctx, Position p1, Position p2, string method, string url, string? body, string? contentType, RuntimeValue? headers, CancellationToken token)
        {
            try
            {
                using var req = BuildRequest(method, url, body, contentType, headers);
                using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return new ValueResult(ResponseMap(resp, respBody), null);
            }
            catch (Exception ex) { return new ValueResult(null, new RuntimeError(p1, p2, method.ToLowerInvariant() + "_async: " + ex.Message, ctx)); }
        }
    }
}

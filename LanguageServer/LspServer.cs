using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.LanguageServer.Features;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;
using RaLanguage.LanguageServer.Transport;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer
{
    /// <summary>
    /// The language server: a single-threaded read pump that parses each JSON-RPC
    /// message, gates it against the lifecycle state, and routes it to the matching
    /// feature service. Requests are answered synchronously (the front-end is fast
    /// enough that no request blocks the pipe meaningfully); diagnostics are the one
    /// thing pushed asynchronously, via the debounced scheduler.
    /// </summary>
    public sealed class LspServer
    {
        private readonly LspConnection _connection;
        private readonly LspLogger _log;
        private readonly DocumentStore _store = new();
        private readonly DiagnosticsScheduler _diagnosticsScheduler;
        private WorkspaceIndex? _workspaceIndex;

        // Feature engines (behind their interfaces — concrete here, swappable in tests).
        private readonly DiagnosticsService _diagnostics = new DiagnosticsService();
        private readonly ISemanticTokensService _semanticTokens = new SemanticTokensService();
        private readonly IFoldingRangeService _folding = new FoldingRangeService();
        private readonly ISelectionRangeService _selectionRanges = new SelectionRangeService();
        private readonly IDocumentSymbolService _documentSymbols = new DocumentSymbolService();
        private readonly IHoverService _hover = new HoverService();
        private readonly CompletionService _completion = new CompletionService();
        private readonly ISignatureHelpService _signatureHelp = new SignatureHelpService();
        private readonly DefinitionService _definition = new DefinitionService();
        private readonly ReferenceService _references = new ReferenceService();
        private readonly IDocumentHighlightService _highlight = new DocumentHighlightService();
        private readonly IRenameService _rename = new RenameService();

        private bool _initialized;
        private bool _shutdownRequested;
        private bool _running = true;
        private string _positionEncodingWire = "utf-16";

        // In-flight feature requests, keyed by the raw JSON-RPC id text, so
        // $/cancelRequest and content-change invalidation can cancel them.
        private readonly ConcurrentDictionary<string, InflightRequest> _inflight = new();

        private sealed class InflightRequest
        {
            public readonly CancellationTokenSource Cts;
            public readonly string? Uri;
            public volatile bool ContentModified;
            public InflightRequest(CancellationTokenSource cts, string? uri) { Cts = cts; Uri = uri; }
        }

        public LspServer(LspLogger log)
        {
            _log = log;
            _connection = new LspConnection(log);
            _diagnosticsScheduler = new DiagnosticsScheduler(_connection, _store, _diagnostics, log);
        }

        /// <summary>Runs the read loop until <c>exit</c> or stdin EOF. Returns the process exit code.</summary>
        public int Run()
        {
            _log.Info("Ra language server starting (stdio).");
            try
            {
                while (_running)
                {
                    byte[]? body = _connection.ReadMessage();
                    if (body == null)
                    {
                        _log.Info("stdin closed; shutting down.");
                        break;
                    }
                    if (body.Length == 0) continue;

                    Dispatch(body);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("fatal read-loop error", ex);
                return 1;
            }
            finally
            {
                _diagnosticsScheduler.Dispose();
            }

            // Per LSP: exit code 0 only if a shutdown request preceded exit.
            return _shutdownRequested ? 0 : 1;
        }

        private void Dispatch(byte[] body)
        {
            string? method = null;
            bool hasId = false;
            JsonElement id = default;
            JsonElement @params = default;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                    method = m.GetString();
                if (root.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind != JsonValueKind.Null && idEl.ValueKind != JsonValueKind.Undefined)
                {
                    hasId = true;
                    id = idEl.Clone();
                }
                if (root.TryGetProperty("params", out var pEl))
                    @params = pEl.Clone();
            }
            catch (Exception ex)
            {
                _log.Exception("malformed message", ex);
                return;
            }

            if (method == null)
            {
                // A response to a server→client request. v1 issues none, so ignore.
                return;
            }

            if (hasId) HandleRequest(method, id, @params);
            else HandleNotification(method, @params);
        }

        // ---- Requests ----

        private void HandleRequest(string method, JsonElement id, JsonElement @params)
        {
            // Lifecycle gate.
            if (!_initialized && method != "initialize")
            {
                _connection.SendError(id, LspErrorCodes.ServerNotInitialized, "Server not initialized.");
                return;
            }
            if (_shutdownRequested && method != "shutdown")
            {
                _connection.SendError(id, LspErrorCodes.InvalidRequest, "Server is shutting down.");
                return;
            }

            // Lifecycle requests run synchronously on the pump to keep strict ordering.
            if (method == "initialize") { _connection.SendResult(id, Initialize(@params)); return; }
            if (method == "shutdown") { _connection.SendResult(id, Shutdown()); return; }

            // Feature requests run off the pump so the read loop keeps draining stdin
            // (and can observe $/cancelRequest), and so a slow request never blocks the
            // pipe. Each carries a cancellation token.
            string idKey = id.GetRawText();
            var inflight = new InflightRequest(new CancellationTokenSource(), TryPeekUri(@params));
            _inflight[idKey] = inflight;
            var token = inflight.Cts.Token;

            _ = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    object? result = Route(method, @params, token);
                    if (ReferenceEquals(result, s_methodNotFound))
                    {
                        _connection.SendError(id, LspErrorCodes.MethodNotFound, $"Method not found: {method}");
                        return;
                    }
                    token.ThrowIfCancellationRequested();
                    _connection.SendResult(id, result);
                }
                catch (OperationCanceledException)
                {
                    int code = inflight.ContentModified ? LspErrorCodes.ContentModified : LspErrorCodes.RequestCancelled;
                    _connection.SendError(id, code, "Request cancelled.");
                }
                catch (Exception ex)
                {
                    _log.Exception($"request '{method}' failed", ex);
                    _connection.SendError(id, LspErrorCodes.InternalError, ex.Message);
                }
                finally
                {
                    _inflight.TryRemove(idKey, out _);
                    inflight.Cts.Dispose();
                }
            });
        }

        private object? Route(string method, JsonElement p, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return method switch
            {
                "textDocument/hover" => OnHover(p),
                "textDocument/completion" => OnCompletion(p),
                "textDocument/signatureHelp" => OnSignatureHelp(p),
                "textDocument/definition" => OnDefinition(p),
                "textDocument/references" => OnReferences(p),
                "textDocument/documentHighlight" => OnHighlight(p),
                "textDocument/documentSymbol" => OnDocumentSymbol(p),
                "textDocument/foldingRange" => OnFolding(p),
                "textDocument/selectionRange" => OnSelectionRange(p),
                "textDocument/semanticTokens/full" => OnSemanticTokens(p),
                "textDocument/prepareRename" => OnPrepareRename(p),
                "textDocument/rename" => OnRename(p),
                "workspace/symbol" => OnWorkspaceSymbol(p),
                "textDocument/documentLink" => OnDocumentLink(p),
                _ => Unhandled(method),
            };
        }

        private object OnDocumentLink(JsonElement p)
        {
            var args = Deserialize<DocumentLinkParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null || _workspaceIndex == null) return Array.Empty<DocumentLink>();

            var comp = doc.GetCompilation();
            var raw = ImportNavigator.CollectLinks(comp.Ast, comp.Tokens, doc.Document.FileName, _workspaceIndex);
            var result = new DocumentLink[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                result[i] = new DocumentLink
                {
                    Range = doc.Document.RangeOf(raw[i].Start, raw[i].End),
                    Target = UriUtil.FromFileSystemPath(raw[i].TargetPath),
                };
            }
            return result;
        }

        private object OnWorkspaceSymbol(JsonElement p)
        {
            var args = Deserialize<WorkspaceSymbolParams>(p);
            if (_workspaceIndex == null || args == null) return Array.Empty<SymbolInformation>();

            var hits = _workspaceIndex.FuzzySearch(args.Query, 200);
            var result = new SymbolInformation[hits.Count];
            for (int i = 0; i < hits.Count; i++)
            {
                var (file, sym, _) = hits[i];
                result[i] = new SymbolInformation
                {
                    Name = sym.Name,
                    Kind = sym.Kind,
                    Location = new Location(file.Uri, file.RangeOf(sym.SelectionStart, sym.SelectionEnd)),
                };
            }
            return result;
        }

        private static string? TryPeekUri(JsonElement p)
        {
            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("textDocument", out var td) &&
                td.ValueKind == JsonValueKind.Object &&
                td.TryGetProperty("uri", out var u) &&
                u.ValueKind == JsonValueKind.String)
            {
                return u.GetString();
            }
            return null;
        }

        private void CancelInflightForUri(string uri)
        {
            string key = UriUtil.NormalizeKey(uri);
            foreach (var kv in _inflight)
            {
                var r = kv.Value;
                if (r.Uri != null && UriUtil.NormalizeKey(r.Uri) == key)
                {
                    r.ContentModified = true;
                    r.Cts.Cancel();
                }
            }
        }

        private void OnCancelRequest(JsonElement p)
        {
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var idEl))
            {
                string key = idEl.GetRawText();
                if (_inflight.TryGetValue(key, out var r)) r.Cts.Cancel();
            }
        }

        private static readonly object s_methodNotFound = new();
        private static object Unhandled(string method) => s_methodNotFound;

        private object Initialize(JsonElement @params)
        {
            var init = Deserialize<InitializeParams>(@params);
            _initialized = true;
            string? root = init?.RootUri ?? init?.RootPath;
            NegotiatePositionEncoding(init?.Capabilities ?? default);
            _log.Info($"initialize (client='{init?.ClientInfo?.Name ?? "?"}', root='{root ?? "?"}', encoding='{_positionEncodingWire}').");

            InitializeWorkspaceIndex(root);

            return new InitializeResult
            {
                Capabilities = BuildCapabilities(),
                ServerInfo = new ServerInfo { Name = "Ra Language Server", Version = "0.1.0" },
            };
        }

        private object? Shutdown()
        {
            _shutdownRequested = true;
            _log.Info("shutdown requested.");
            return null;
        }

        private void InitializeWorkspaceIndex(string? root)
        {
            try
            {
                string projectRoot = ResolveProjectRoot(root);
                string stdRoot = ResolveStdRoot(projectRoot);
                _workspaceIndex = new WorkspaceIndex(projectRoot, stdRoot, _store.Encoding, _log);
                _definition.Workspace = _workspaceIndex;
                _references.Workspace = _workspaceIndex;
                _diagnostics.Workspace = _workspaceIndex;
                _completion.Workspace = _workspaceIndex;
                _workspaceIndex.StartBackgroundIndex();
                _log.Info($"workspace root: {projectRoot}");
            }
            catch (Exception ex)
            {
                _log.Exception("workspace index init", ex);
            }
        }

        private static string ResolveProjectRoot(string? root)
        {
            if (!string.IsNullOrEmpty(root))
            {
                string p = UriUtil.ToFileSystemPath(root);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
            }
            return Directory.GetCurrentDirectory();
        }

        private static string ResolveStdRoot(string projectRoot)
        {
            string exeStd = Path.Combine(AppContext.BaseDirectory, "std");
            if (Directory.Exists(exeStd)) return exeStd;
            string projStd = Path.Combine(projectRoot, "std");
            if (Directory.Exists(projStd)) return projStd;
            return exeStd;
        }

        private void NegotiatePositionEncoding(JsonElement capabilities)
        {
            var kind = PositionEncodingKind.Utf16;
            string wire = "utf-16";
            try
            {
                if (capabilities.ValueKind == JsonValueKind.Object &&
                    capabilities.TryGetProperty("general", out var general) &&
                    general.ValueKind == JsonValueKind.Object &&
                    general.TryGetProperty("positionEncodings", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    bool has16 = false, has8 = false, has32 = false;
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.String) continue;
                        switch (el.GetString())
                        {
                            case "utf-16": has16 = true; break;
                            case "utf-8": has8 = true; break;
                            case "utf-32": has32 = true; break;
                        }
                    }
                    // Prefer utf-16 (native + zero-cost); otherwise honor what the client offers.
                    if (has16) { kind = PositionEncodingKind.Utf16; wire = "utf-16"; }
                    else if (has8) { kind = PositionEncodingKind.Utf8; wire = "utf-8"; }
                    else if (has32) { kind = PositionEncodingKind.Utf32; wire = "utf-32"; }
                }
            }
            catch
            {
                // Malformed capabilities → keep the safe utf-16 default.
            }

            _store.Encoding = kind;
            _positionEncodingWire = wire;
        }

        private ServerCapabilities BuildCapabilities() => new()
        {
            PositionEncoding = _positionEncodingWire,
            TextDocumentSync = new TextDocumentSyncOptions
            {
                OpenClose = true,
                Change = TextDocumentSyncKind.Incremental,
                Save = new SaveOptions { IncludeText = false },
            },
            HoverProvider = true,
            CompletionProvider = new CompletionOptions
            {
                TriggerCharacters = new[] { ".", ":" },
                ResolveProvider = false,
            },
            SignatureHelpProvider = new SignatureHelpOptions
            {
                TriggerCharacters = new[] { "(", "," },
                RetriggerCharacters = new[] { "," },
            },
            DefinitionProvider = true,
            ReferencesProvider = true,
            DocumentHighlightProvider = true,
            DocumentSymbolProvider = true,
            WorkspaceSymbolProvider = true,
            RenameProvider = new RenameOptions { PrepareProvider = true },
            FoldingRangeProvider = true,
            SelectionRangeProvider = true,
            DocumentLinkProvider = new DocumentLinkOptions { ResolveProvider = false },
            SemanticTokensProvider = new SemanticTokensOptions
            {
                Legend = SemanticTokensService.CreateLegend(),
                Full = true,
                Range = false,
            },
            Workspace = new WorkspaceServerCapabilities
            {
                WorkspaceFolders = new WorkspaceFoldersServerCapabilities { Supported = true },
            },
        };

        private object? OnHover(JsonElement p)
        {
            var args = Deserialize<TextDocumentPositionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null || args == null ? null : _hover.Compute(doc, args.Position);
        }

        private object OnCompletion(JsonElement p)
        {
            var args = Deserialize<CompletionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null || args == null) return new CompletionList();
            return _completion.Compute(doc, args.Position, args.Context);
        }

        private object? OnSignatureHelp(JsonElement p)
        {
            var args = Deserialize<TextDocumentPositionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null || args == null ? null : _signatureHelp.Compute(doc, args.Position);
        }

        private object? OnDefinition(JsonElement p)
        {
            var args = Deserialize<TextDocumentPositionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null || args == null ? null : _definition.Compute(doc, args.Position);
        }

        private object? OnReferences(JsonElement p)
        {
            var args = Deserialize<ReferenceParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null || args == null) return null;
            return _references.Compute(doc, args.Position, args.Context?.IncludeDeclaration ?? false);
        }

        private object? OnHighlight(JsonElement p)
        {
            var args = Deserialize<TextDocumentPositionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null || args == null ? null : _highlight.Compute(doc, args.Position);
        }

        private object OnDocumentSymbol(JsonElement p)
        {
            var args = Deserialize<DocumentSymbolParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null ? Array.Empty<DocumentSymbol>() : _documentSymbols.Compute(doc);
        }

        private object OnFolding(JsonElement p)
        {
            var args = Deserialize<FoldingRangeParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null ? Array.Empty<FoldingRange>() : _folding.Compute(doc);
        }

        private object OnSelectionRange(JsonElement p)
        {
            var args = Deserialize<SelectionRangeParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null || args == null) return Array.Empty<SelectionRange>();
            return _selectionRanges.Compute(doc, args.Positions);
        }

        private object OnSemanticTokens(JsonElement p)
        {
            var args = Deserialize<SemanticTokensParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null) return new SemanticTokens();
            return _semanticTokens.Compute(doc, BuildTypeNameSet(doc));
        }

        // Declared (this file) + imported type names, for semantic 'type' coloring.
        // Uses the last cached compilation so the hot semantic-token path never forces
        // a fresh parse; freshly typed type names colorize after the next full compile.
        private System.Collections.Generic.HashSet<string> BuildTypeNameSet(RaDocument doc)
        {
            var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var cached = doc.TryGetCachedCompilation();
            if (cached != null)
            {
                var index = SymbolIndex.Build(cached.Ast);
                foreach (var s in index.Flat)
                    if (s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Interface)
                        set.Add(s.Name);
                if (_workspaceIndex != null)
                    set.UnionWith(_workspaceIndex.ImportedTypeNames(doc.Document.FileName, cached.Ast));
            }
            return set;
        }

        private object? OnPrepareRename(JsonElement p)
        {
            var args = Deserialize<TextDocumentPositionParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            return doc == null || args == null ? null : _rename.Prepare(doc, args.Position);
        }

        private object? OnRename(JsonElement p)
        {
            var args = Deserialize<RenameParams>(p);
            var doc = DocFor(args?.TextDocument?.Uri);
            if (doc == null || args == null) return null;
            return _rename.Rename(doc, args.Position, args.NewName);
        }

        // ---- Notifications ----

        private void HandleNotification(string method, JsonElement @params)
        {
            if (!_initialized && method != "initialized" && method != "exit")
            {
                return; // drop pre-initialize notifications
            }

            try
            {
                switch (method)
                {
                    case "initialized":
                        _log.Info("client initialized.");
                        break;
                    case "exit":
                        _running = false;
                        break;
                    case "textDocument/didOpen":
                        OnDidOpen(@params);
                        break;
                    case "textDocument/didChange":
                        OnDidChange(@params);
                        break;
                    case "textDocument/didClose":
                        OnDidClose(@params);
                        break;
                    case "textDocument/didSave":
                        OnDidSave(@params);
                        break;
                    case "$/cancelRequest":
                        OnCancelRequest(@params);
                        break;
                    case "$/setTrace":
                    case "workspace/didChangeConfiguration":
                    case "workspace/didChangeWatchedFiles":
                        break;
                    default:
                        _log.Debug($"unhandled notification: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Exception($"notification '{method}' failed", ex);
            }
        }

        private void OnDidOpen(JsonElement p)
        {
            var args = Deserialize<DidOpenTextDocumentParams>(p);
            if (args?.TextDocument == null) return;
            var doc = _store.Open(args.TextDocument);
            _diagnosticsScheduler.PublishImmediately(doc.Document.Uri);
            _workspaceIndex?.Reindex(doc.Document.FileName, doc.Document.Text);
        }

        private void OnDidChange(JsonElement p)
        {
            var args = Deserialize<DidChangeTextDocumentParams>(p);
            if (args?.TextDocument == null) return;
            // Abort in-flight requests against this document: their results would be
            // stale (LSP ContentModified). Cheap; most requests have already completed.
            CancelInflightForUri(args.TextDocument.Uri);
            var doc = _store.Change(args.TextDocument.Uri, args.ContentChanges, args.TextDocument.Version);
            if (doc != null)
            {
                _diagnosticsScheduler.Schedule(doc.Document.Uri);
                _workspaceIndex?.Reindex(doc.Document.FileName, doc.Document.Text);
            }
        }

        private void OnDidClose(JsonElement p)
        {
            var args = Deserialize<DidCloseTextDocumentParams>(p);
            if (args?.TextDocument == null) return;
            string normalized = UriUtil.NormalizeKey(args.TextDocument.Uri);
            _diagnosticsScheduler.Clear(normalized);
            _store.Close(args.TextDocument.Uri);
            // Re-index from disk so the workspace reflects the saved file, not the closed buffer.
            _workspaceIndex?.Reindex(UriUtil.ToFileSystemPath(args.TextDocument.Uri), null);
        }

        private void OnDidSave(JsonElement p)
        {
            var args = Deserialize<DidSaveTextDocumentParams>(p);
            if (args?.TextDocument == null) return;
            var doc = _store.TryGet(args.TextDocument.Uri);
            if (doc != null)
            {
                _diagnosticsScheduler.PublishImmediately(doc.Document.Uri);
                _workspaceIndex?.Reindex(doc.Document.FileName, doc.Document.Text);
            }
        }

        // ---- Helpers ----

        private RaDocument? DocFor(string? uri) => uri == null ? null : _store.TryGet(uri);

        private static T? Deserialize<T>(JsonElement element) where T : class
        {
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
            return JsonSerializer.Deserialize(element, typeof(T), RaLspJsonContext.Default) as T;
        }
    }
}

# Ra Language Support

Rich editor support for the **Ra language**, powered by the Ra language server
(`ra --lsp`). The extension is a thin Language Server Protocol client: VS Code
talks to the Ra compiler front-end over stdio, so the language intelligence stays
in the compiler and is reusable by any LSP-capable editor.

## Features

Provided by the language server:

- **Diagnostics** — lexer / parser / static-analysis errors and warnings, live as you type (debounced).
- **Semantic highlighting** — keywords, types, functions, methods, parameters, properties, decorators, strings, numbers, regex.
- **Hover** — kind + signature for declarations, plus keyword and built-in-type help.
- **Completion** — declared symbols, in-scope identifiers, keywords, built-in types, member suggestions and control-flow snippets.
- **Signature help** — parameter hints inside calls, with the active argument highlighted.
- **Go to definition** and **Find all references**.
- **Document highlights** — read/write occurrences of the symbol under the cursor.
- **Rename** (with prepare/validation).
- **Document symbols / outline** — hierarchical (namespaces → types → members).
- **Folding ranges** — blocks, import headers, `region`/`endregion` comments.
- **Selection ranges** — smart expand/shrink.

Provided statically by this extension (active even without the server):

- TextMate grammar highlighting, snippets, bracket auto-closing and indentation rules.

## Requirements

You need the Ra executable built with language-server support (the `--lsp` mode).
From the Ra repository root:

```
dotnet publish -c Release -r win-x64      # or osx-arm64 / linux-x64
```

This produces a native, ahead-of-time-compiled `RaLanguage` executable. The server
mode is started with `RaLanguage --lsp`.

## Setup

1. Install the client's runtime dependency:

   ```
   cd "Ra Language Support VS Code Extension"
   npm install
   ```

2. Point the extension at your Ra executable — set **`ra.server.path`** in settings
   to the published binary (e.g. `.../bin/Release/net10.0/win-x64/publish/RaLanguage.exe`).
   If `ra` or `RaLanguage` is already on your `PATH`, you can skip this.

3. Press `F5` to launch an Extension Development Host, then open a `.ra` file.

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| `ra.enable` | `true` | Enable the language server. When `false`, only the grammar + snippets are active. |
| `ra.server.path` | `""` | Path to the Ra executable. May be a `.dll` (launched via `dotnet`). Empty → search `PATH`. |
| `ra.server.args` | `["--lsp"]` | Arguments used to start the server. |
| `ra.server.logLevel` | `"info"` | Server stderr verbosity (`error`/`warning`/`info`/`debug`). |
| `ra.trace.server` | `"off"` | Trace JSON-RPC traffic in the output channel (`off`/`messages`/`verbose`). |

Commands: **Ra: Run File**, **Ra: Restart Language Server**, **Ra: Show Language Server Output**.

## Running a file

Press the ▶ button in the editor title bar (or right-click → **Ra: Run File**, or run the
command from the palette) to execute the current `.ra` file. It runs in a reusable
**integrated terminal** (`<ra> <file>`), so you get colors, interactive `stdin`, and the
program's real exit behaviour — like any other language extension.

The executable is resolved the same way as the server (`ra.server.path`, else `ra` /
`RaLanguage` on the `PATH`).

### Optional: Code Runner integration

If you use the *Code Runner* extension, add a mapping so its ▶ button supports `.ra`
(point `ra` at your binary or keep it if it is on the `PATH`):

```jsonc
"code-runner.executorMap": {
  "ra": "ra $fullFileName"
}
```

## Packaging

```
npm install -g @vscode/vsce
vsce package
```

The extension is unbundled: `vscode-languageclient` is a production dependency and
is included in the `.vsix` (dev dependencies are pruned automatically).

## Architecture

```
VS Code  ──stdio / JSON-RPC──▶  RaLanguage --lsp
(this client)                   (Ra compiler front-end: lexer → parser → analysis)
```

The server never runs the Ra VM; it uses only the compiler front-end, so editor
features stay fast and side-effect-free.

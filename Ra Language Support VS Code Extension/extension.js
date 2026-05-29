'use strict';

// Ra Language Support — thin LSP client.
//
// All language intelligence (diagnostics, semantic tokens, hover, completion,
// definition, references, rename, symbols, folding, selection ranges, signature
// help) is provided by the Ra language server, launched as `ra --lsp` over stdio.
// This extension only wires VS Code to that server; the TextMate grammar and
// snippets in this package remain as a static fallback when the server is disabled
// or unavailable.

const vscode = require('vscode');
const fs = require('fs');
const path = require('path');

let lc; // LanguageClient | undefined
let outputChannel; // vscode.OutputChannel

/** Names to probe on PATH when no explicit server path is configured. */
function candidateNames() {
  return process.platform === 'win32'
    ? ['ra.exe', 'RaLanguage.exe', 'ra', 'RaLanguage']
    : ['ra', 'RaLanguage'];
}

/** Search the PATH for the first existing executable among `names`. */
function findOnPath(names) {
  const dirs = (process.env.PATH || '').split(path.delimiter).filter(Boolean);
  for (const dir of dirs) {
    for (const name of names) {
      const full = path.join(dir, name);
      try {
        if (fs.existsSync(full) && fs.statSync(full).isFile()) return full;
      } catch {
        /* ignore unreadable PATH entries */
      }
    }
  }
  return undefined;
}

/**
 * Resolve the base Ra executable (no LSP args). `.dll` paths are launched via dotnet.
 * @returns {{command: string, prefixArgs: string[]} | undefined}
 */
function resolveRaCommand(config) {
  const configured = (config.get('server.path') || '').trim();
  const explicit = configured || process.env.RA_LANGUAGE_SERVER || '';
  if (explicit) {
    if (explicit.toLowerCase().endsWith('.dll')) return { command: 'dotnet', prefixArgs: [explicit] };
    return { command: explicit, prefixArgs: [] };
  }
  const found = findOnPath(candidateNames());
  if (found) return { command: found, prefixArgs: [] };
  return undefined;
}

/**
 * Resolve how to launch the language server (base command + --lsp args).
 * @returns {{command: string, args: string[]} | undefined}
 */
function resolveServer(config) {
  const base = resolveRaCommand(config);
  if (!base) return undefined;
  const lspArgs = (config.get('server.args') || ['--lsp']).slice();
  const logLevel = config.get('server.logLevel');
  if (logLevel) lspArgs.push('--log-level', logLevel);
  return { command: base.command, args: [...base.prefixArgs, ...lspArgs] };
}

// --- Run current file in an interactive integrated terminal ---

let raTerminal;

function quoteArg(a) {
  return /\s/.test(a) ? `"${a}"` : a;
}

/** Run a .ra file (active editor, or an explorer/editor URI) in a reusable terminal. */
async function runFile(uriArg) {
  const config = vscode.workspace.getConfiguration('ra');
  let filePath;
  if (uriArg && uriArg.fsPath) {
    filePath = uriArg.fsPath;
  } else {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'ra') {
      vscode.window.showWarningMessage('Open a .ra file to run it.');
      return;
    }
    await editor.document.save();
    filePath = editor.document.fileName;
  }

  const base = resolveRaCommand(config);
  if (!base) {
    const choice = await vscode.window.showWarningMessage(
      'Ra executable not found. Set "ra.server.path" to your Ra binary.',
      'Open Settings'
    );
    if (choice === 'Open Settings') {
      vscode.commands.executeCommand('workbench.action.openSettings', 'ra.server.path');
    }
    return;
  }

  if (!raTerminal || raTerminal.exitStatus !== undefined) {
    raTerminal = vscode.window.createTerminal('Ra');
  }
  raTerminal.show(false);
  const parts = [base.command, ...base.prefixArgs, filePath].map(quoteArg);
  raTerminal.sendText(parts.join(' '), true);
}

async function startClient(context) {
  const config = vscode.workspace.getConfiguration('ra');
  if (!config.get('enable')) {
    outputChannel.appendLine('Ra language server is disabled (ra.enable = false).');
    return;
  }

  const resolved = resolveServer(config);
  if (!resolved) {
    const choice = await vscode.window.showWarningMessage(
      'Ra language server executable not found. Set "ra.server.path" to your Ra binary (built with `dotnet publish`).',
      'Open Settings'
    );
    if (choice === 'Open Settings') {
      vscode.commands.executeCommand('workbench.action.openSettings', 'ra.server.path');
    }
    return;
  }

  const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

  const exec = {
    command: resolved.command,
    args: resolved.args,
    transport: TransportKind.stdio,
  };

  const serverOptions = { run: exec, debug: exec };

  const clientOptions = {
    documentSelector: [
      { scheme: 'file', language: 'ra' },
      { scheme: 'untitled', language: 'ra' },
    ],
    outputChannel,
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.ra'),
    },
  };

  lc = new LanguageClient('ra', 'Ra Language Server', serverOptions, clientOptions);

  outputChannel.appendLine(`Starting Ra language server: ${resolved.command} ${resolved.args.join(' ')}`);
  try {
    await lc.start();
    outputChannel.appendLine('Ra language server started.');
  } catch (err) {
    lc = undefined;
    outputChannel.appendLine(`Failed to start Ra language server: ${err && err.message ? err.message : err}`);
    vscode.window.showErrorMessage(
      `Ra language server failed to start. Check "Ra Language Server" output. (${resolved.command})`
    );
  }
}

async function stopClient() {
  if (lc) {
    try {
      await lc.stop();
    } catch {
      /* ignore stop errors */
    }
    lc = undefined;
  }
}

/** @param {vscode.ExtensionContext} context */
async function activate(context) {
  outputChannel = vscode.window.createOutputChannel('Ra Language Server');
  context.subscriptions.push(outputChannel);

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.restartServer', async () => {
      outputChannel.appendLine('Restarting Ra language server…');
      await stopClient();
      await startClient(context);
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.showServerOutput', () => outputChannel.show(true))
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.runFile', runFile)
  );

  context.subscriptions.push(
    vscode.window.onDidCloseTerminal((t) => { if (t === raTerminal) raTerminal = undefined; })
  );

  // Restart on relevant configuration changes.
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (
        e.affectsConfiguration('ra.enable') ||
        e.affectsConfiguration('ra.server.path') ||
        e.affectsConfiguration('ra.server.args') ||
        e.affectsConfiguration('ra.server.logLevel')
      ) {
        await stopClient();
        await startClient(context);
      }
    })
  );

  await startClient(context);
}

function deactivate() {
  return stopClient();
}

module.exports = { activate, deactivate };

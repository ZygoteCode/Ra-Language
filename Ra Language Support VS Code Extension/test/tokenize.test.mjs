// TextMate grammar regression test for the Ra language.
//
// Loads syntaxes/ra.tmLanguage.json through the same engine VS Code uses
// (vscode-textmate + vscode-oniguruma), tokenizes representative Ra source,
// and asserts that key lexemes receive the expected scopes. This both
// compiles every Oniguruma regex (catching invalid patterns) and locks in
// the alignment with the real language / LSP.
//
// Run: npm test   (from the extension folder)

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import oniguruma from 'vscode-oniguruma';
import vsctm from 'vscode-textmate';
const { Registry, parseRawGrammar, INITIAL } = vsctm;

const __dirname = dirname(fileURLToPath(import.meta.url));
const extRoot = join(__dirname, '..');
const grammarPath = join(extRoot, 'syntaxes', 'ra.tmLanguage.json');

async function makeGrammar() {
  const wasmPath = join(extRoot, 'node_modules', 'vscode-oniguruma', 'release', 'onig.wasm');
  const wasmBin = readFileSync(wasmPath);
  await oniguruma.loadWASM(wasmBin.buffer.slice(wasmBin.byteOffset, wasmBin.byteOffset + wasmBin.byteLength));
  const onigLib = Promise.resolve({
    createOnigScanner: (patterns) => new oniguruma.OnigScanner(patterns),
    createOnigString: (s) => new oniguruma.OnigString(s),
  });
  const registry = new Registry({
    onigLib,
    loadGrammar: async (scopeName) => {
      if (scopeName === 'source.ra') {
        const text = readFileSync(grammarPath, 'utf8');
        return parseRawGrammar(text, grammarPath);
      }
      return null;
    },
  });
  const grammar = await registry.loadGrammar('source.ra');
  if (!grammar) throw new Error('failed to load source.ra grammar');
  return grammar;
}

// Tokenize a (possibly multi-line) snippet, carrying the rule stack across lines.
function tokenizeDoc(grammar, code) {
  const lines = code.split('\n');
  let stack = INITIAL;
  const out = [];
  for (const line of lines) {
    const r = grammar.tokenizeLine(line, stack);
    out.push({ line, tokens: r.tokens });
    stack = r.ruleStack;
  }
  return out;
}

// Return the scope list of the token that covers the nth occurrence of `sub`.
function scopesAt(doc, sub, nth = 0) {
  let seen = 0;
  for (const { line, tokens } of doc) {
    let from = 0;
    while (true) {
      const col = line.indexOf(sub, from);
      if (col === -1) break;
      if (seen === nth) {
        const tok = tokens.find((t) => t.startIndex <= col && col < t.endIndex);
        return tok ? tok.scopes : null;
      }
      seen++;
      from = col + 1;
    }
  }
  return null;
}

let pass = 0;
const failures = [];

function expectScope(grammar, label, code, sub, scope, nth = 0) {
  const doc = tokenizeDoc(grammar, code);
  const scopes = scopesAt(doc, sub, nth);
  if (scopes && scopes.includes(scope)) {
    pass++;
  } else {
    failures.push(`${label}: '${sub}' expected scope '${scope}', got ${scopes ? scopes.join(', ') : '<no token>'}`);
  }
}

function expectNotScope(grammar, label, code, sub, scope, nth = 0) {
  const doc = tokenizeDoc(grammar, code);
  const scopes = scopesAt(doc, sub, nth);
  if (scopes && scopes.includes(scope)) {
    failures.push(`${label}: '${sub}' should NOT have scope '${scope}', got ${scopes.join(', ')}`);
  } else {
    pass++;
  }
}

const grammar = await makeGrammar();

// --- keywords added in this rework -----------------------------------------
expectScope(grammar, 'match kw', 'let r = match x { case _ -> 0 }', 'match', 'keyword.control.ra');
expectScope(grammar, 'throw kw', 'throw err;', 'throw', 'keyword.control.ra');
expectScope(grammar, 'await kw', 'let r = await t;', 'await', 'keyword.control.ra');
expectScope(grammar, 'spawn kw', 'let r = spawn f();', 'spawn', 'keyword.control.ra');
expectScope(grammar, 'emit kw', 'emit i;', 'emit', 'keyword.control.ra');
expectScope(grammar, 'where kw', 'fn f<T>(x: T): T where T: int { ret x; }', 'where', 'keyword.control.ra');
expectScope(grammar, 'mut mod', 'let mut a = 1;', 'mut', 'storage.modifier.ra');
expectScope(grammar, 'lazy mod', 'pub lazy prop p: int = 0', 'lazy', 'storage.modifier.ra');
expectScope(grammar, 'async mod', 'async fn f() => 1;', 'async', 'storage.modifier.ra');
expectScope(grammar, 'factory mod', 'pub factory T.u() => T();', 'factory', 'storage.modifier.ra');
expectScope(grammar, 'auto mod', 'auto x = 5;', 'auto', 'storage.modifier.ra');
expectScope(grammar, 'cancellable mod', 'pub cancellable event C(m: string)', 'cancellable', 'storage.modifier.ra');
expectScope(grammar, 'tolerant mod', 'pub tolerant event T()', 'tolerant', 'storage.modifier.ra');

// --- declarations (name gets an entity scope) ------------------------------
expectScope(grammar, 'record kw', 'record class Counter(n: int)', 'record', 'storage.type.record.ra');
expectScope(grammar, 'record class kw', 'record class Counter(n: int)', 'class', 'storage.type.class.ra');
expectScope(grammar, 'record name', 'record class Counter(n: int)', 'Counter', 'entity.name.type.record.ra');
expectScope(grammar, 'class name', 'class Widget {}', 'Widget', 'entity.name.type.ra');
expectScope(grammar, 'struct kw subst', 'struct Foo {}', 'struct', 'storage.type.struct.ra');
expectScope(grammar, 'enum kw subst', 'enum E { A }', 'enum', 'storage.type.enum.ra');
expectScope(grammar, 'extend kw subst', 'extend Box {}', 'extend', 'storage.type.extend.ra');
expectScope(grammar, 'fn name', 'fn doThing() {}', 'doThing', 'entity.name.function.ra');
expectScope(grammar, 'prop kw', 'pub prop value: int = 0', 'prop', 'storage.type.property.ra');
expectScope(grammar, 'event kw', 'pub event Click(x: int)', 'event', 'storage.type.event.ra');
expectScope(grammar, 'annotation decl', 'annotation Meta { label: string }', 'Meta', 'entity.name.type.ra');
expectScope(grammar, 'namespace name', 'namespace App.Core {}', 'App.Core', 'entity.name.namespace.ra');
expectScope(grammar, 'extend target', 'extend Box {}', 'Box', 'entity.name.type.ra');

// --- operators: shifts, rotates, pipe, pow, cast ---------------------------
expectScope(grammar, 'logical lshift', 'let a = x <<< 2;', '<<<', 'keyword.operator.bitwise.shift.ra');
expectScope(grammar, 'rotate right', 'let a = x >>>> 2;', '>>>>', 'keyword.operator.bitwise.shift.ra');
expectScope(grammar, 'arith rshift', 'let a = x >> 2;', '>>', 'keyword.operator.bitwise.shift.ra');
expectScope(grammar, 'pipe forward', 'let a = 5 |> dbl;', '|>', 'keyword.operator.ra');
expectScope(grammar, 'pow star', 'let a = 2 ** 3;', '**', 'keyword.operator.arithmetic.ra');
expectScope(grammar, 'pow caret', 'let a = 2 ^ 3;', '^', 'keyword.operator.arithmetic.ra');
expectScope(grammar, 'cast colons', 'let a = x :: int;', '::', 'keyword.operator.ra');
expectScope(grammar, 'rotate assign', 'x <<<<= 2;', '<<<<=', 'keyword.operator.assignment.ra');
expectScope(grammar, 'null coalesce', 'let a = x ?? y;', '??', 'keyword.operator.ra');
expectScope(grammar, 'range incl', 'case 1..=9 -> 0', '..=', 'keyword.operator.ra');
expectScope(grammar, 'spread', 'let a = [1, ...b];', '...', 'keyword.operator.ra');

// --- decrement must stay an operator, NOT a comment ('---' is the comment) --
expectScope(grammar, 'decrement', 'i--;', '--', 'keyword.operator.arithmetic.ra');
expectScope(grammar, 'triple-dash comment', '--- a line comment', '---', 'comment.line.triple-dash.ra');
expectScope(grammar, 'hash comment', '# a line comment', '#', 'comment.line.number-sign.ra');
expectScope(grammar, 'cdata comment', '<!-- block --> ', '<!--', 'comment.block.cdata.ra');

// --- strings, interpolation (dollar prefix REQUIRED) -----------------------
expectScope(grammar, 'interp prefix', 'let s = $"hi ${name}";', '$', 'punctuation.definition.interpolation.prefix.ra');
expectScope(grammar, 'interp hole var', 'let s = $"hi ${name}";', 'name', 'meta.interpolation.ra');
// A plain (non-$) string must NOT interpolate — ${x} is literal text there.
expectNotScope(grammar, 'plain string no interp', 'let s = "hi ${name}";', 'name', 'meta.interpolation.ra');
expectScope(grammar, 'plain string body', 'let s = "hi ${name}";', 'name', 'string.quoted.double.ra');
expectScope(grammar, 'backtick string', 'let s = `raw`;', 'raw', 'string.quoted.backtick.ra');

// --- regex literal ---------------------------------------------------------
expectScope(grammar, 're prefix', 'let r = re"\\d+"i;', 're', 'support.function.regexp.ra');
expectScope(grammar, 'regex body', 'let r = re"abc"i;', 'abc', 'string.regexp.ra');
expectScope(grammar, 'regex flags', 'let r = re"abc"i;', 'i', 'keyword.other.flags.regexp.ra');
// `result` must NOT be mistaken for a regex literal.
expectNotScope(grammar, 'ident re not regex', 'let result = 5;', 'result', 'string.regexp.ra');

// --- lifetimes vs char-like strings ----------------------------------------
expectScope(grammar, 'lifetime', "fn f<'lt>(x: &'lt int) {}", 'lt', 'storage.modifier.lifetime.ra');
expectScope(grammar, 'single-quote string', "let s = 'hello';", 'hello', 'string.quoted.single.ra');

// --- annotations -----------------------------------------------------------
expectScope(grammar, 'annotation use', '@derive(equals=false)', 'derive', 'entity.name.decorator.ra');
expectScope(grammar, 'annotation sigil', '@sealed', '@', 'punctuation.definition.annotation.ra');

// --- primitives, constants, language vars ----------------------------------
expectScope(grammar, 'primitive int', 'let x: int = 0;', 'int', 'support.type.primitive.ra');
expectScope(grammar, 'primitive int128', 'let x: int128 = 0;', 'int128', 'support.type.primitive.ra');
expectScope(grammar, 'const true', 'let b = true;', 'true', 'constant.language.ra');
expectScope(grammar, 'null', 'let b = null;', 'null', 'constant.language.ra');
expectScope(grammar, 'self', 'self.x = 1;', 'self', 'variable.language.ra');

// --- word operators --------------------------------------------------------
expectScope(grammar, 'is not in', 'if x is not in s {}', 'is not in', 'keyword.operator.word.ra');
expectScope(grammar, 'as word', 'let i = c as int;', 'as', 'keyword.operator.word.ra');
expectScope(grammar, 'typeof', 'let t = typeof x;', 'typeof', 'keyword.operator.word.ra');

// --- numbers ---------------------------------------------------------------
expectScope(grammar, 'int suffix', 'let a = 1073741824i;', '1073741824i', 'constant.numeric.integer.ra');
expectScope(grammar, 'underscored', 'let a = 1_000_000;', '1_000_000', 'constant.numeric.integer.ra');
expectScope(grammar, 'hex', 'let a = 0xFF;', '0xFF', 'constant.numeric.hex.ra');
expectScope(grammar, 'binary', 'let a = 0b1010;', '0b1010', 'constant.numeric.binary.ra');
expectScope(grammar, 'float', 'let a = 3.14f;', '3.14f', 'constant.numeric.float.ra');

// --- calls / member access -------------------------------------------------
expectScope(grammar, 'fn call', 'print(x);', 'print', 'entity.name.function.call.ra');
expectScope(grammar, 'ctor call', 'let p = Point(1, 2);', 'Point', 'entity.name.type.ra');
expectScope(grammar, 'member prop', 'let n = obj.field;', 'field', 'variable.other.property.ra');
expectScope(grammar, 'member method', 'obj.run();', 'run', 'entity.name.function.member.ra');
expectScope(grammar, 'type ref', 'let c = Color.Green;', 'Color', 'entity.name.type.ra');

// --- generics, for-in, plain identifiers, type annotations -----------------
expectScope(grammar, 'generic recv type', 'let b = Box<int>(7);', 'Box', 'entity.name.type.ra');
expectScope(grammar, 'generic arg primitive', 'let b = Box<int>(7);', 'int', 'support.type.primitive.ra');
expectScope(grammar, 'for-in in', 'for x in xs {}', 'in', 'keyword.operator.word.ra');
expectScope(grammar, 'for kw', 'for x in xs {}', 'for', 'keyword.control.ra');
expectScope(grammar, 'camel var', 'let myVar = 1;', 'myVar', 'variable.other.ra');
expectScope(grammar, 'type annotation', 'let p: Point = q;', 'Point', 'entity.name.type.ra');
expectScope(grammar, 'lambda param', 'let inc = |x| x + 1;', 'x', 'variable.other.ra');

// --- asm block -------------------------------------------------------------
const asmCode = ['let r = asm {', '    mov rax, %{xv}', '    ret', '};'].join('\n');
expectScope(grammar, 'asm kw', asmCode, 'asm', 'keyword.control.asm.ra');
expectScope(grammar, 'asm body embedded', asmCode, 'mov', 'meta.embedded.block.asm.ra');
expectScope(grammar, 'asm interp', asmCode, 'xv', 'meta.interpolation.asm.ra');

// --- report ----------------------------------------------------------------
console.log(`\nRa grammar tokenizer test: ${pass} passed, ${failures.length} failed.\n`);
if (failures.length) {
  for (const f of failures) console.error('  ✗ ' + f);
  process.exitCode = 1;
} else {
  console.log('  ✓ all scope assertions hold');
}

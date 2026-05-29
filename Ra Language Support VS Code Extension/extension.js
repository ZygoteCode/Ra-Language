const vscode = require('vscode');

const KEYWORDS = [
  'abstract', 'and', 'as', 'break', 'case', 'catch', 'class', 'const', 'continue', 'default',
  'del', 'delay', 'do', 'elif', 'else', 'enum', 'extend', 'false', 'final', 'finally', 'fn',
  'for', 'goto', 'if', 'impl', 'in', 'interface', 'let', 'nameof', 'not', 'not in', 'null',
  'or', 'override', 'pass', 'pub', 'ret', 'retry', 'self', 'static', 'step', 'struct', 'super',
  'switch', 'times', 'to', 'trait', 'true', 'try', 'typeof', 'var', 'while', 'with', 'yield',
  'import', 'from', 'operator', 'ref'
];

const BUILT_IN_TYPES = [
  'int', 'number', 'long', 'float', 'double', 'uint', 'ulong', 'short', 'ushort',
  'int128', 'uint128', 'decimal', 'byte', 'bool', 'string', 'char', 'void', 'object', 'any'
];

const COMMENT_ITEMS = [
  ['Line comment //', '// ', 'Insert a line comment'],
  ['Line comment #', '# ', 'Insert a hash comment'],
  ['Line comment ---', '--- ', 'Insert a triple-dash comment'],
  ['Block comment /* */', '/* $0 */', 'Insert a block comment'],
  ['XML comment <!-- -->', '<!--\n\t$0\n-->', 'Insert an XML-style comment']
];

const BLOCK_SNIPPETS = [
  ['if', 'if (${1:condition})\n{\n\t$0\n}', 'If statement'],
  ['elif', 'elif (${1:condition})\n{\n\t$0\n}', 'Else-if branch'],
  ['if else', 'if (${1:condition})\n{\n\t$2\n}\nelse\n{\n\t$0\n}', 'If / else'],
  ['switch', 'switch (${1:value})\n{\n\tcase ${2:pattern}:\n\t\t$0\n\tdefault:\n\t\tpass\n}', 'Switch block'],
  ['while', 'while (${1:condition})\n{\n\t$0\n}', 'While loop'],
  ['do while', 'do\n{\n\t$1\n}\nwhile ${2:condition}', 'Do/while loop'],
  ['for in', 'for ${1:item} in ${2:collection}\n{\n\t$0\n}', 'For-in loop'],
  ['for range', 'for ${1:item} = ${2:start} to ${3:end} step ${4:step}\n{\n\t$0\n}', 'Range for loop'],
  ['for c-style', 'for (${1:init}; ${2:condition}; ${3:step})\n{\n\t$0\n}', 'C-style for loop'],
  ['fn', 'fn ${1:name}(${2:args})\n{\n\t$0\n}', 'Function definition'],
  ['pub fn', 'pub fn ${1:name}(${2:args})\n{\n\t$0\n}', 'Public function'],
  ['abstract fn', 'abstract fn ${1:name}(${2:args})', 'Abstract function'],
  ['static fn', 'static fn ${1:name}(${2:args})\n{\n\t$0\n}', 'Static function'],
  ['override fn', 'override fn ${1:name}(${2:args})\n{\n\t$0\n}', 'Override function'],
  ['retry', 'retry for ${1:count} times delay ${2:delay}\n{\n\t$0\n}', 'Retry statement'],
  ['try', 'try\n{\n\t$1\n}\ncatch (${2:error})\n{\n\t$3\n}\nfinally\n{\n\t$0\n}', 'Try/catch/finally'],
  ['goto', 'goto ${1:label}', 'Goto statement'],
  ['label', '${1:label}:\n\t$0', 'Label'],
  ['del', 'del ${1:name}', 'Delete binding(s)'],
  ['nameof', 'nameof(${1:identifier})', 'NameOf expression'],
  ['typeof', 'typeof(${1:expression})', 'TypeOf expression'],
  ['cast', '${1:value} as ${2:type}', 'Cast expression'],
  ['range', '${1:start} .. ${2:end}', 'Range expression'],
  ['null coalescing', '${1:left} ?? ${2:right}', 'Null-coalescing expression'],
  ['class', 'class ${1:Name}\n{\n\t$0\n}', 'Class definition'],
  ['pub class', 'pub class ${1:Name}\n{\n\t$0\n}', 'Public class definition'],
  ['abstract class', 'abstract class ${1:Name}\n{\n\t$0\n}', 'Abstract class definition'],
  ['struct', 'struct ${1:Name}\n{\n\t$0\n}', 'Struct definition'],
  ['interface', 'interface ${1:Name}\n{\n\t$0\n}', 'Interface definition'],
  ['trait', 'trait ${1:Name}\n{\n\t$0\n}', 'Trait definition'],
  ['enum', 'enum ${1:Name}\n{\n\t${2:Member1},\n\t${3:Member2} = ${4:0}\n\t$0\n}', 'Enum definition'],
  ['impl', 'impl ${1:Interface1}, ${2:Interface2}\n{\n\t$0\n}', 'Interface implementation list'],
  ['extend', 'extend ${1:Type}\n{\n\tpub fn ${2:method}(${3:args})\n\t{\n\t\t$0\n\t}\n}', 'Extension block'],
  ['with', 'with ${1:Trait1}, ${2:Trait2}', 'Trait mixin list'],
  ['var', 'var ${1:name} = ${2:value}', 'Variable declaration'],
  ['let', 'let ${1:name} = ${2:value}', 'Let binding'],
  ['const', 'const ${1:name} = ${2:value}', 'Const declaration'],
  ['final', 'final ${1:name} = ${2:value}', 'Final declaration'],
];

function makeKeywordItem(text) {
  const item = new vscode.CompletionItem(text, vscode.CompletionItemKind.Keyword);
  item.insertText = text;
  item.detail = 'RA keyword';
  return item;
}

function makeTypeItem(text) {
  const item = new vscode.CompletionItem(text, vscode.CompletionItemKind.TypeParameter);
  item.insertText = text;
  item.detail = 'RA built-in type';
  return item;
}

function makeSnippetItem(label, snippet, detail) {
  const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Snippet);
  item.insertText = new vscode.SnippetString(snippet);
  item.insertTextRules = vscode.CompletionItemInsertTextRule.InsertAsSnippet;
  item.detail = detail;
  return item;
}

function buildItems() {
  const items = [];
  for (const kw of KEYWORDS) items.push(makeKeywordItem(kw));
  for (const typeName of BUILT_IN_TYPES) items.push(makeTypeItem(typeName));
  for (const [label, snippet, detail] of COMMENT_ITEMS) items.push(makeSnippetItem(label, snippet, detail));
  for (const [label, snippet, detail] of BLOCK_SNIPPETS) items.push(makeSnippetItem(label, snippet, detail));

  const specials = [
    ['true', 'true'],
    ['false', 'false'],
    ['null', 'null'],
    ['self', 'self'],
    ['super', 'super'],
    ['not in', 'not in'],
    ['and', 'and'],
    ['or', 'or'],
    ['not', 'not'],
    ['as', 'as'],
    ['with', 'with'],
    ['to', 'to'],
    ['step', 'step'],
    ['times', 'times'],
    ['delay', 'delay']
  ];
  for (const [label, text] of specials) {
    const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Text);
    item.insertText = text;
    item.detail = 'RA token';
    items.push(item);
  }

  return items;
}

function activate(context) {
  const items = buildItems();
  const selector = { language: 'ra', scheme: 'file' };

  const provider = {
    provideCompletionItems(document, position) {
      const line = document.lineAt(position).text;
      const before = line.slice(0, position.character);

      if (/^\s*(\/\/|#|---|\/\*|<!--)?\s*$/.test(before)) {
        return items;
      }

      return items;
    }
  };

  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(
      selector,
      provider,
      ...'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_/#-*\'"`:;,.?()[]{}<>='.split('')
    )
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.insertLineComment', async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) return;
      await editor.insertSnippet(new vscode.SnippetString('// '));
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.insertBlockComment', async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) return;
      await editor.insertSnippet(new vscode.SnippetString('/* $0 */'));
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('ra.insertXmlComment', async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) return;
      await editor.insertSnippet(new vscode.SnippetString('<!--\n\t$0\n-->'));
    })
  );
}

function deactivate() {}

module.exports = { activate, deactivate };

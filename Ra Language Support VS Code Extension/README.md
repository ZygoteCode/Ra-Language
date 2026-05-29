# RA Language Support

VS Code support for `.ra` files.

## Features

- syntax highlighting based on lexer/parser rules  
- auto-closing for parentheses, braces, brackets, and quotes  
- completions for keywords, types, and blocks  
- snippets for classes, structs, traits, interfaces, enums, functions, control flow, `try/catch/finally`, `retry`, `extend`, `impl`  

## Covered Rules

Main keywords: abstract, and, as, break, case, catch, class, const, continue, default, del, delay, do, elif, else, enum, extend, false, final, finally, fn, for, goto, if, impl, in, interface, let, nameof, not, not in, null, or, override, pass, pub, ret, retry, self, static, step, struct, super, switch, times, to, trait, true, try, typeof, var, while, with, yield, import, from.

Built-in types: int, number, long, float, double, uint, ulong, short, ushort, int128, uint128, decimal, byte, bool, string, char, void, object, any.

## Quick Start

1. Open the folder in VS Code  
2. Press `F5`  
3. Open a `.ra` file  

## Notes

The grammar also highlights:
- comments `//`, `#`, `---`, `/* */`, `<!-- -->`  
- strings with single quotes, double quotes, and backticks  
- interpolation `$Ellipsis`  
- integers and floating-point numbers with prefixes and suffixes  
- language operators and punctuation  
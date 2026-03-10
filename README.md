# Ra Language

<p align="center">
  <i>"Your knowledge will ascend with time, like Ra rising through the sky."</i>
</p>

<p align="center">
  <img src="Ra-Language.png" alt="Ra Language" width="40%">
</p>

Every programming language has its learning curve… but with Ra Language, there’s no curve: you start at the zero point of an infinite line, and simply walking along it lets your skills evolve. You can begin with nothing and still have everything you need, or, if you’re already a pro, you can create truly extraordinary things. With Ra Language, growth is continuous, natural, and effortless.

## Work-in-progress tasks or already done features

This list contains what I have already added after the first commit (done features, they'll obviously get updates) and what I am doing right now to improve the language.

  - [x] Fix context in all remaining statements (for, while, fn).
  - [x] Add comparison operators && , || , ! additionally to actual "is", "and", "or", "is not", "not".
  - [x] Add exponent operator "**" which will be an alias of actual pow "^"
  - [x] Add "~" operator to NOT a value.
  - [x] Add bitwise operations (&, |, <<, >>) + mod operator (%).
  - [x] Add normal variable assignment => Example: I declare a variable "var a = 5", and I can do "a = 7" directly.
  - [x] Add assignment operators: += , -= , *= , /= , %= , &= , |= , <<=, >>=, ^=, **=, &&=, ||=.
  - [x] Add "++" and "--" operators (left + right both applicable).
  - [x] Additionally to var, add "const" declaration.
  - [x] Add "final" variable declaration, similar to "const" but can be declared with values at runtime, nextly immediately protected in-code.
  - [x] Can declare a new context using "{}" brackets normally like in every C-like language.,
  - [x] Declare variables also with hexadecimal notation like "0xF8 - 0xC173" - "0x0031".
  - [x] Add "del" operator to delete declared variables like "del a" or "del a, b, c" if you need multiple deletions at the same time.
  - [x] Add "do while" statement.
  - [x] Add 'typeof' operator.
  - [x] Add 'nameof' operator.
  - [x] Declare variables (only "var" + "final") also without a initial value - the first value assigned to the "final" one will be definitive in runtime. Const variables need immediately a value at parsing-time.
  - [x] Make "null" a real type of value, which can be easily used in comparisons, as different from numbers and others.
  - [x] Add "bool" type value, which will finally replace "true" and "false" numeric values - after that, comparisons will get better and have effective sense. But "0" as "false" and "1" as "true" will be a concept.
  - [x] Add a way to declare variables like "var a, b, c = 7" or "var a = 3, b = 5, c = 7" (multiple declarations).
  - [x] Add strict checking operator for comparing not only the value, but also the type: '===' + '!=='.
  - [x] Access easily to list values using the following operation => "var a = [1, 2, 3]; print(a[1]); // will output 2".
  - [x] Add sets with the {} brackets, similar to list but with unique values, like '{1, 2, 3, 4, "str"}'.
  - [x] Add "in" operator for lists that can check if an element is in list => Example: "if 5 in a" or also "if [1, 2, 3] in a"
  - [x] Add factorial operator with "!" as suffix (or "!!" as prefix+suffix).
  - [x] Assign values to list via indexes, like "list[0] = 5".
  - [x] Add template literals with backtick (`) for making strings easily with multi-lines.
  - [x] Add foreach statement, which will be easily "for item in list" instead of "for i = 0 to 10 step 1". The "in" keyword could be used also as a condition semantic, like "if 5 in list".
  - [x] Access to last element of a list using for example a[-1]. Also can access to element in a bottom-up approach, so can use for example: "var list = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];" can use "print(list[-1])" (output 10), "print(list[-2])" (output 9) and so on.
  - [x] Add in-place ranges => For example 1..5 (1, 2, 3, 4) or 1..=5 (1, 2, 3, 4, 5), will return a list of numbers.
  - [x] Access to portions of list using ranges, like for example a[1..3].
  - [x] Add list slicing with step, like a[0..5:2].
  - [x] Add "??" operator to apply a different value if the current inspected value is null => Example: a = b ?? 3;
  - [x] Add "..." operator to insert all-in the elements of a list/set in another list/set. Example: "var list = [1, 2, ...otherList]".
  - [x] Add more assignment operators: '??='.
  - [x] Add ternary operation (for ex. "a == 5 ? 7 : 3").
  - [x] Add maps with the "{}" brackets, like '{"float": 3.2, "int": 1, "string": "astring", "bool": true}'.
  - [x] Add "not in" / "is not in" operation.
  - [x] Add string interpolation, example $"Example string: ${variable}".
  - [x] Implement complete switch statement + expression with case, default, yield support, with fall-through support (via colon ":" + break), right arrow expressions ("->")
  - [x] Added several different types of operations with all available language operators to strings, sets, lists and maps.
  - [x] Added tuples as a new primitive value.
  - [x] Implemented labels + goto keyword.
  - [x] Implemented let declarations with full support for copy types, move action, and invalidate on move.
  - [x] Implemented static variable typization, example: "var n: number = 5".
  - [x] Implemented "auto" keyword for assigning automatically the static type to the variable recognized from the declaration value.
  - [x] Function parameters can be statically typed -> fn test(a: int, b: int): int => print(a + b);
  - [x] Add static return type to functions.
  - [x] Implemented direct type casting using "as" keyword.
  - [x] In functions, implement varargs with "..." (spread) operator with the possibility to assign a custom name to it!
  - [x] Allow function calling with named parameters.
  - [x] In functions, allow specific default values to parameters.

## TODOs for Ra Language (the near future)

This is constantly updated list, I am expanding with new ideas and concepts for the language. If you feel good with the language and have new ideas & suggestions, you can open an issue with some details, or submit a pull request with interesting modifications.

I'll take a look at that as soon as possible! These are the things that I want to implement in the near future, so don't worry, they don't are like far from what the language will be. Many things will be implemented, as I take ispiration from other languages!
  
  - [ ] Add generics, for example, for declaring a "list<number>" or a "tuple<string, number>". Extend the chances of using for example "T" identifier, for example function declarations and callings with generics.
  - [ ] Add real support to multi-line statements & expressions.
  - [ ] Idea: add a operator like "&" to function parameters to pass reference instead of copying only the value.
  - [ ] Idea: import new files with a intelligent path system.
  - [ ] Idea: List comprehensions, like [x*2 for x in a if x>1] to create lists in a compact way.
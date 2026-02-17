using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage
{
    public class Program
    {
        public static SymbolTable GlobalSymbolTable;

        static Program()
        {
            GlobalSymbolTable = new SymbolTable();
            GlobalSymbolTable.Set("NULL", Number.Null);
            GlobalSymbolTable.Set("FALSE", Number.False);
            GlobalSymbolTable.Set("TRUE", Number.True);
            GlobalSymbolTable.Set("MATH_PI", Number.MathPI);
            GlobalSymbolTable.Set("PRINT", new BuiltInFunction("print"));
            GlobalSymbolTable.Set("PRINT_RET", new BuiltInFunction("print_ret"));
            GlobalSymbolTable.Set("INPUT", new BuiltInFunction("input"));
            GlobalSymbolTable.Set("INPUT_INT", new BuiltInFunction("input_int"));
            GlobalSymbolTable.Set("CLEAR", new BuiltInFunction("clear"));
            GlobalSymbolTable.Set("CLS", new BuiltInFunction("clear"));
            GlobalSymbolTable.Set("IS_NUM", new BuiltInFunction("is_number"));
            GlobalSymbolTable.Set("IS_STR", new BuiltInFunction("is_string"));
            GlobalSymbolTable.Set("IS_LIST", new BuiltInFunction("is_list"));
            GlobalSymbolTable.Set("IS_FUN", new BuiltInFunction("is_function"));
            GlobalSymbolTable.Set("APPEND", new BuiltInFunction("append"));
            GlobalSymbolTable.Set("POP", new BuiltInFunction("pop"));
            GlobalSymbolTable.Set("EXTEND", new BuiltInFunction("extend"));
            GlobalSymbolTable.Set("LEN", new BuiltInFunction("len"));
            GlobalSymbolTable.Set("RUN", new BuiltInFunction("run"));
        }

        public static (RuntimeValue?, Error?) Run(string fn, string text)
        {
            var lexer = new Lexer.Lexer(fn, text);
            var (tokens, error) = lexer.MakeTokens();

            if (error != null)
            {
                return (null, error);
            }

            var parser = new Parser.Parser(tokens);
            var ast = parser.Parse();

            if (ast.Error != null)
            {
                return (null, ast.Error);
            }

            var interpreter = new Interpreter.Interpreter();
            var context = new Context("<program>");
            context.SymbolTable = GlobalSymbolTable;
            var result = interpreter.Visit(ast.Node, context);

            return (result.Value, result.Error);
        }

        public static void Main(string[] args)
        {
            Console.Title = "Ra Language | Made by https://github.com/ZygoteCode/";
            Console.WriteLine("Ra Language Interpreter, made by https://github.com/ZygoteCode/ in C# .NET 10.0.");
            Console.WriteLine("Type 'exit' to quit.");

            while (true)
            {
                Console.Write("Ra Language > ");
                string text = Console.ReadLine() ?? "";

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (text == "exit")
                {
                    break;
                }

                var (result, error) = Run("<stdin>", text);

                if (error != null)
                {
                    Console.WriteLine(error.AsString());
                }
                else if (result != null)
                {
                    if (result is ListVal l && l.Elements.Count == 1)
                    {
                        Console.WriteLine(l.Elements[0]);
                    }
                    else
                    {
                        Console.WriteLine(result);
                    }
                }
            }
        }
    }
}
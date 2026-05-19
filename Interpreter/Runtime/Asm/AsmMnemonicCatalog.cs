using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    public static class AsmMnemonicCatalog
    {
        public static IReadOnlyList<string> AllMnemonics => X64Mnemonics.AllKnownMnemonics;
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

#if BROKEN
#error Independent compiler failure must reach the executor.
#endif

internal static partial class Program
{
    private static string SourcePath([CallerFilePath] string path = "") => path;

    [GeneratedRegex("^genbuild$")]
    private static partial Regex Pattern();

    private static void Main()
    {
        Console.WriteLine(SourcePath().Replace('\\', '/'));
        Console.WriteLine(Pattern().IsMatch("genbuild"));
        Console.WriteLine(Producer.Text);
    }
}

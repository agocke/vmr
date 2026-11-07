using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

string response = File.ReadAllText(args.Single(arg => arg.StartsWith("@", StringComparison.Ordinal)).Substring(1));
string[] arguments = Regex.Matches(response, "\"([^\"]*)\"|(\\S+)")
    .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToArray();
string Value(string option) => arguments[Array.IndexOf(arguments, option) + 1];
string input = Value("-reference");
string reference = arguments.Contains("-d")
    ? Path.Combine(Value("-d"), "Reference.dll")
    : arguments[Array.IndexOf(arguments, "-reference", Array.IndexOf(arguments, "-reference") + 1) + 1];
string directory = Value("-out");
Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, Path.GetFileName(input)),
    File.ReadAllText(input) + File.ReadAllText(Value("-x")) +
    File.ReadAllText(Path.ChangeExtension(reference, ".pdb")));
if (arguments.Contains("-b"))
{
    File.Copy(Path.ChangeExtension(input, ".pdb"), Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + ".pdb"), overwrite: true);
}

using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3)
{
    Console.Error.WriteLine("Expected MSBuild manifest, toolset manifest and output path.");
    return 1;
}

JsonObject engine = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
JsonObject toolset = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
string target = engine["runtimeTarget"]!["name"]!.GetValue<string>();
if (target != toolset["runtimeTarget"]!["name"]!.GetValue<string>())
{
    throw new InvalidOperationException("MSBuild and toolset dependency manifests must target the same framework.");
}

// MSBuild deliberately leaves this runtime dependency to its SDK host.
const string dependency = "NuGet.Frameworks";
JsonObject toolLibraries = toolset["libraries"]!.AsObject();
string key = toolLibraries.Select(pair => pair.Key)
    .Single(name => name.StartsWith(dependency + "/", StringComparison.Ordinal));
string version = key[(dependency.Length + 1)..];
JsonObject engineLibraries = engine["libraries"]!.AsObject();
JsonObject engineTarget = engine["targets"]![target]!.AsObject();
JsonObject toolEntry = toolset["targets"]![target]![key]!.AsObject();
if (toolEntry["runtime"]?["NuGet.Frameworks.dll"] is null ||
    toolEntry["dependencies"] is JsonObject { Count: > 0 })
{
    throw new InvalidOperationException("Expected a dependency-free NuGet.Frameworks runtime assembly.");
}

foreach (string existing in engineLibraries.Select(pair => pair.Key))
{
    if (existing.StartsWith(dependency + "/", StringComparison.Ordinal) && existing != key)
    {
        throw new InvalidOperationException("MSBuild already declares a different NuGet.Frameworks dependency.");
    }
}

engineLibraries[key] = toolLibraries[key]!.DeepClone();
engineTarget[key] = toolEntry.DeepClone();
string root = engineTarget.Select(pair => pair.Key)
    .Single(name => name.StartsWith("MSBuild/", StringComparison.Ordinal));
JsonObject rootEntry = engineTarget[root]!.AsObject();
rootEntry["dependencies"] ??= new JsonObject();
rootEntry["dependencies"]![dependency] = version;
File.WriteAllText(args[2], engine.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
return 0;

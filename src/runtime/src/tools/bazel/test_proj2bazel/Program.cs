// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

if (args.Length < 1)
{
    Console.WriteLine("Usage: test_proj2bazle [<proj_files>]");
    return 1;
}

foreach (var projPath in args)
{
    var projFullPath = Path.GetFullPath(projPath);
    Console.WriteLine($"Processing {projFullPath}");

    int priority = 0;
    string? debugType = null;
    var srcs = new List<string>();
    bool allowUnsafe = false;
    bool? optimize = null;
    var tags = new List<string>();

    var projXml = XDocument.Load(projFullPath);
    foreach (var projElement in projXml.Root!.Elements())
    {
        switch (projElement.Name.LocalName)
        {
            case "PropertyGroup":
            {
                foreach (var propElement in projElement.Elements())
                {
                    var propName = propElement.Name.LocalName;
                    switch (propName.ToLowerInvariant())
                    {
                        case "clrtestpriority":
                            switch (propElement.Value.Trim())
                            {
                                case "1":
                                    priority = 1;
                                    break;
                                case "0":
                                    break;
                                default:
                                    Console.Error.WriteLine($"Unknown CLRTestPriority value: {propElement.Value}");
                                    goto UnsupportedProject;
                            }
                            break;
                        case "allowunsafeblocks":
                            if (!propElement.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.Error.WriteLine("AllowUnsafeBlocks must be set to true or not set");
                                goto UnsupportedProject;
                            }
                            allowUnsafe = true;
                            break;
                        case "debugtype":
                            debugType = propElement.Value.Trim().ToLowerInvariant();
                            switch (debugType)
                            {
                                case "none":
                                    // The bazel rules don't support "none" so we'll just use the default.
                                    debugType = null;
                                    break;
                                case "portable":
                                case "embedded":
                                case "pdbonly":
                                case "full":
                                    break;
                                default:
                                    Console.Error.WriteLine($"Unknown DebugType value: {propElement.Value}");
                                    debugType = null;
                                    goto UnsupportedProject;
                            }
                            break;
                        case "optimize":
                        {
                            var optimizeString = propElement.Value.Trim().ToLowerInvariant();
                            if (optimizeString == "true")
                            {
                                optimize = true;
                            }
                            else if (optimizeString == "false")
                            {
                                optimize = false;
                            }
                            else if (optimizeString == "")
                            {
                                optimize = null;
                            }
                            else
                            {
                                Console.Error.WriteLine($"Unknown Optimize value: {propElement.Value}");
                                goto UnsupportedProject;
                            }
                            break;
                        }
                        case "requiresprocessisolation":
                            // Nothing to do here for now since the test runners are all isolated.
                            break;
                        case "jitoptimizationsensitive":
                            tags.Add("JitOptimizationSensitive");
                            break;
                        default:
                            Console.Error.WriteLine($"Unknown PropertyGroup element: {propElement.Name}");
                            goto UnsupportedProject;
                    }
                }
            }
            break;
            case "ItemGroup":
            {
                foreach (var itemElement in projElement.Elements())
                {
                    var itemName = itemElement.Name.LocalName;
                    switch (itemName.ToLowerInvariant())
                    {
                        case "compile":
                        {
                            if (itemElement.Attributes().SingleOrDefault() is { Name.LocalName: "Include", Value: {} attrValue })
                            {
                                attrValue = attrValue.Trim().Replace("$(MSBuildProjectName)", Path.GetFileNameWithoutExtension(projFullPath));
                                srcs.Add(attrValue);
                            }
                            else
                            {
                                Console.Error.WriteLine("Compile item without Include attribute or with unsupported attributes");
                                goto UnsupportedProject;
                            }
                            if (itemElement.Elements().Any())
                            {
                                Console.Error.WriteLine("Compile item with child elements");
                                goto UnsupportedProject;
                            }
                            break;
                        }
                        default:
                            Console.Error.WriteLine($"Unknown ItemGroup element: {itemElement.Name}");
                            goto UnsupportedProject;
                    }
                }
            }
            break;
        }
    }

    WriteBazelTest(
        projFullPath,
        priority,
        debugType,
        optimize,
        allowUnsafe,
        srcs,
        tags);

UnsupportedProject:
    // This project contains data we don't understand -- continue to next one
    ;
}

return 0;

static void WriteBazelTest(
    string projPath,
    int priority,
    string? debugType,
    bool? optimize,
    bool allowUnsafe,
    List<string> sources,
    List<string> tags)
{
    // Write to a BUILD.bazel file in the same directory as the project file
    var projDir = Path.GetDirectoryName(projPath)!;
    var bazelPath = Path.Combine(projDir, "BUILD.bazel");
    Console.WriteLine($"Writing Bazel project to {bazelPath}");

    var optLines = new List<string>();
    if (allowUnsafe)
    {
        optLines.Add(Environment.NewLine + "    allow_unsafe_blocks = True,");
    }
    if (priority != 0)
    {
        optLines.Add(Environment.NewLine + $"    pri = {priority},");
    }
    if (debugType != null)
    {
        optLines.Add(Environment.NewLine + $"    debug_type = \"{debugType}\",");
    }
    if (optimize != null)
    {
        optLines.Add(Environment.NewLine + $"    optimize = {optimize},");
    }
    if (tags.Count > 0)
    {
        optLines.Add(Environment.NewLine + $"    tags = [{string.Join(", ", tags.Select(t => $"\"{t}\""))}],");
    }
    string test_rule_name = projPath.EndsWith("ilproj") ? "il_coreclr_test" : "coreclr_test";
    string bazelTestText = $"""
    load("//src/tests:live_test.bzl", "{test_rule_name}")

    {test_rule_name}(
        name = "{Path.GetFileNameWithoutExtension(projPath)}",
        srcs = [{string.Join(", ", sources.Select(s => $"\"{s}\""))}],{string.Join("", optLines)}
    )

    """;

    // Open file as append to avoid overwriting existing content
    File.AppendAllText(bazelPath, bazelTestText);
}

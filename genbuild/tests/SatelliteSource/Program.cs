using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text.Json;

[assembly: AssemblyCompany("Company \"quote\" \\ value")]
[assembly: AssemblyCopyright("Copyright (c) satellite fixture")]
[assembly: AssemblyDescription("Satellite description\nsecond line")]
[assembly: AssemblyFileVersion("1.2.3.456")]
[assembly: AssemblyInformationalVersion("1.2.3+satellites")]
[assembly: AssemblyProduct("Satellite Product")]
[assembly: AssemblyTitle("Satellite Title")]

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args[0] == "staged-graph")
        {
            CheckStagedGraph(args[1]);
            return;
        }

        Assembly parent = typeof(Program).Assembly;
        ResourceManager resources = new("SatelliteFixture.Strings", parent);
        foreach (string culture in new[] { "fr", "de" })
        {
            string expected = culture == "fr" ? "Bonjour!" : "Hallo!";
            if (resources.GetString("Hello", new CultureInfo(culture)) != expected)
            {
                throw new Exception("Satellite lookup did not return the localized resource.");
            }

            Assembly satellite = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, culture, "SatelliteFixture.resources.dll"));
            if (satellite.GetName().Version.ToString() != args[0] || satellite.GetName().CultureName != culture)
            {
                throw new Exception("Satellite identity must come from the parent DLL and resource culture.");
            }

            foreach (CustomAttributeData attribute in parent.GetCustomAttributesData())
            {
                string name = attribute.AttributeType.Name;
                if (name == nameof(AssemblyCompanyAttribute) || name == nameof(AssemblyCopyrightAttribute) ||
                    name == nameof(AssemblyDescriptionAttribute) || name == nameof(AssemblyFileVersionAttribute) ||
                    name == nameof(AssemblyInformationalVersionAttribute) || name == nameof(AssemblyProductAttribute) ||
                    name == nameof(AssemblyTitleAttribute))
                {
                    CustomAttributeData actual = satellite.GetCustomAttributesData().Single(item => item.AttributeType == attribute.AttributeType);
                    if (!Equals(attribute.ConstructorArguments[0].Value, actual.ConstructorArguments[0].Value))
                    {
                        throw new Exception("Satellite attribute differs from its parent: " + name);
                    }
                }
            }

            AssemblyMetadataAttribute metadata = satellite.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "Fixture");
            if (metadata.Value != "authored \"quote\" \\ value")
            {
                throw new Exception("Authored satellite attribute was lost or incorrectly escaped.");
            }
        }

        Console.WriteLine("Satellite identities, attributes and translated resources verified: " + args[0]);
    }

    private static void CheckStagedGraph(string graph)
    {
        int compilers = 0;
        foreach (string file in Directory.GetFiles(graph + ".actions", "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement invocation = document.RootElement;
            if (invocation.GetProperty("taskType").GetString() != "Microsoft.CodeAnalysis.BuildTasks.CompileManaged")
            {
                continue;
            }

            compilers++;
            JsonElement parameters = invocation.GetProperty("parameters");
            if (!parameters.TryGetProperty("UseSandbox", out JsonElement enabled) ||
                !string.Equals(enabled.GetProperty("scalar").GetString(), "true", StringComparison.OrdinalIgnoreCase) ||
                !parameters.TryGetProperty("ArgumentPaths", out JsonElement paths) ||
                paths.GetProperty("items").GetArrayLength() == 0 ||
                !parameters.TryGetProperty("SandboxArguments", out JsonElement arguments) ||
                !arguments.GetProperty("scalar").GetString().Contains("/__MSBuildSandbox Path/operand ", StringComparison.Ordinal))
            {
                throw new Exception("Primary and satellite compilers must all carry staging and typed argument bindings.");
            }
        }

        if (compilers != 3)
        {
            throw new Exception("Expected one primary and two satellite compiler actions.");
        }
    }
}

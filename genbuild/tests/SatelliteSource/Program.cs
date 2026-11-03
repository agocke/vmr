using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;

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
}

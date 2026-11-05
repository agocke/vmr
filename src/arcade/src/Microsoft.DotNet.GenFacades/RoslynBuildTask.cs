// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Microsoft.DotNet.Build.Tasks;

public abstract partial class RoslynBuildTask : Task
{
    [Required]
    public string RoslynAssembliesPath { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; }

    public ITaskItem[] DeclaredOutputs { get; set; }

    public override bool Execute()
    {
        AssemblyLoadContext currentContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly())!;
        currentContext.Resolving += ResolverForRoslyn;
        try
        {
            return ExecuteCore();
        }
        finally
        {
            currentContext.Resolving -= ResolverForRoslyn;
        }
    }

    public abstract bool ExecuteCore();

    protected bool ValidateDeclaredFiles(IEnumerable<string> inputs, IEnumerable<string> outputs)
    {
        if (DeclaredInputs is null && DeclaredOutputs is null)
        {
            return true;
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expectedInputs = new HashSet<string>(inputs.Select(Path.GetFullPath), comparer)
        {
            Path.GetFullPath(Path.Combine(RoslynAssembliesPath, "Microsoft.CodeAnalysis.dll")),
            Path.GetFullPath(Path.Combine(RoslynAssembliesPath, "Microsoft.CodeAnalysis.CSharp.dll")),
        };
        var expectedOutputs = new HashSet<string>(outputs.Select(Path.GetFullPath), comparer);
        if (DeclaredInputs is null || !expectedInputs.SetEquals(DeclaredInputs.Select(item => Path.GetFullPath(item.ItemSpec))))
        {
            Log.LogError("DeclaredInputs must contain exactly the source inputs and both Roslyn assemblies.");
            return false;
        }

        if (DeclaredOutputs is null || DeclaredOutputs.Length != expectedOutputs.Count
            || !expectedOutputs.SetEquals(DeclaredOutputs.Select(item => Path.GetFullPath(item.ItemSpec))))
        {
            Log.LogError("DeclaredOutputs must contain exactly the generated source paths.");
            return false;
        }

        foreach (string output in expectedOutputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output));
        }

        return true;
    }
    
    private Assembly ResolverForRoslyn(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        return LoadRoslyn(assemblyName, path => context.LoadFromAssemblyPath(path));
    }

    private Assembly LoadRoslyn(AssemblyName name, Func<string, Assembly> loadFromPath)
    {
        const string codeAnalysisName = "Microsoft.CodeAnalysis";
        const string codeAnalysisCsharpName = "Microsoft.CodeAnalysis.CSharp";
        if (name.Name == codeAnalysisName || name.Name == codeAnalysisCsharpName)
        {
            Assembly asm = loadFromPath(Path.Combine(RoslynAssembliesPath!, $"{name.Name}.dll"));
            Version resolvedVersion = asm.GetName().Version;
            if (resolvedVersion < name.Version)
            {
                throw new Exception($"The minimum version required of Roslyn is '{name.Version}' and you are using '{resolvedVersion}' version of the Roslyn. You can update the sdk to get the latest version.");
            }

            // Being extra defensive but we want to avoid that we accidentally load two different versions of either
            // of the roslyn assemblies from a different location, so let's load them both on the first request.
            Assembly _ = name.Name == codeAnalysisName ?
                loadFromPath(Path.Combine(RoslynAssembliesPath!, $"{codeAnalysisCsharpName}.dll")) :
                loadFromPath(Path.Combine(RoslynAssembliesPath!, $"{codeAnalysisName}.dll"));

            return asm;
        }

        return null;
    }
}

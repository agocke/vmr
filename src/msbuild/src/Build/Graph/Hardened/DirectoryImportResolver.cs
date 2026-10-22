// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
#if NETFRAMEWORK
using Path = Microsoft.IO.Path;
#else
using Path = System.IO.Path;
#endif

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class DirectoryImportResolver(string? repositoryRoot, IFileSystem fileSystem)
{
    internal const string RepositoryRootProperty = "MSBuildGraphRepositoryRoot";

    internal static bool IsDirectoryFileName(string fileName)
        => fileName.Equals("Directory.Build.props", FileUtilities.PathComparison) ||
           fileName.Equals("Directory.Build.targets", FileUtilities.PathComparison) ||
           fileName.Equals("Directory.Packages.props", FileUtilities.PathComparison);

    internal string Resolve(string startingDirectory, string fileName, bool returnFile, IElementLocation location)
    {
        ProjectErrorUtilities.VerifyThrowInvalidProject(
            IsDirectoryFileName(fileName),
            location, "HardenedGraphProhibitedFunction",
            $"ancestor lookup for '{fileName}'", "directory import resolution");
        ProjectErrorUtilities.VerifyThrowInvalidProject(
            !string.IsNullOrEmpty(repositoryRoot) && Path.IsPathFullyQualified(repositoryRoot),
            location, "HardenedGraphUnsupportedConstruct",
            $"a missing or nonabsolute global {RepositoryRootProperty}", "directory import resolution");
        ProjectErrorUtilities.VerifyThrowInvalidProject(
            Path.IsPathFullyQualified(startingDirectory),
            location, "HardenedGraphUnsupportedConstruct",
            $"a nonabsolute directory import search start '{startingDirectory}'", "directory import resolution");

        string root = HardenedGlobPolicy.NormalizeRoot(repositoryRoot!);
        string directory = HardenedGlobPolicy.NormalizeRoot(startingDirectory);
        ElementLocation elementLocation = ElementLocation.Create(location.File, location.Line, location.Column);
        HardenedGlobPolicy.ValidatePath(fileName, directory, root, elementLocation);
        while (true)
        {
            string candidate = Path.Combine(directory, fileName);
            HardenedGlobPolicy.ValidatePath(fileName, candidate, root, elementLocation);
            if (fileSystem.FileExists(candidate))
            {
                return returnFile ? candidate : directory;
            }

            if (FileUtilities.PathsEqual(directory, root))
            {
                return string.Empty;
            }

            directory = Path.GetDirectoryName(directory)!;
        }
    }
}

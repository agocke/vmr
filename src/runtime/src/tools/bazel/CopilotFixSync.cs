// CopilotFixSync.cs
//
// Uses the GitHub Copilot SDK for .NET to automatically attempt BUILD.bazel
// fixes based on a sync detection report.
//
// Usage: dotnet run CopilotFixSync.cs -- <report-file-path>
//
// Requires:
//   - GitHub.Copilot.SDK NuGet package
//   - Copilot CLI installed and authenticated (COPILOT_GITHUB_TOKEN env var)

#:package GitHub.Copilot.SDK@*

using GitHub.Copilot.SDK;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run CopilotFixSync.cs -- <report-file-path>");
    return 1;
}

var reportPath = args[0];
if (!File.Exists(reportPath))
{
    Console.Error.WriteLine($"Report file not found: {reportPath}");
    return 1;
}

var detectionReport = File.ReadAllText(reportPath);

Console.WriteLine("Starting Copilot SDK session...");

try
{
    await using var client = new CopilotClient();
    await client.StartAsync();

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = "claude-sonnet-4",
        OnPermissionRequest = PermissionHandler.ApproveAll
    });

    var prompt = $"""
        You are updating Bazel build files for the dotnet/runtime repository after
        merging upstream changes from release/10.0 into the bazel branch.

        NuGet version bumps have already been handled by a deterministic script.
        Focus on non-version changes only.

        ## Context: How Bazel build files work in this repository

        ### Source files in BUILD.bazel

        Libraries under src/libraries/ have BUILD.bazel files that define Bazel build
        targets. The key patterns are:

        1. **Explicit srcs lists**: Most libraries list source files explicitly:
           ```
           srcs = [
               "src/System/Collections/Generic/LinkedList.cs",
               "src/System/Collections/ThrowHelper.cs",
           ]
           ```

        2. **Glob patterns**: Some libraries use glob() which auto-includes files:
           ```
           srcs = glob(["src/System/**/*.cs"])
           ```
           Files in glob-based libraries are auto-included and need no changes.

        3. **Cross-package references**: Shared source files use Bazel labels:
           - `$(CoreLibSharedDir)path` in MSBuild → `"//src/libraries/System.Private.CoreLib:src/path"` in Bazel
           - `$(CommonPath)path` in MSBuild → `"//src/libraries/Common:src/path"` in Bazel

        4. **Dependencies**: `<ProjectReference>` in .csproj → `deps` list in BUILD.bazel:
           - `//src/libraries:ref_<AssemblyName>` for reference assemblies
           - `//src/libraries:impl_<AssemblyName>` for implementation assemblies

        5. **Defines**: `<DefineConstants>` in .csproj → `defines` list in BUILD.bazel

        ## Detection Report

        {detectionReport}

        ## Instructions

        Based on the detection report above, make the necessary changes to Bazel build
        files. Version bumps have already been applied — skip any version-related items.

        For each remaining item flagged as needing an update:

        - **New .cs files (explicit srcs)**: Add the file path to the `srcs` list in
          the appropriate BUILD.bazel file. Maintain alphabetical ordering.

        - **Removed .cs files (explicit srcs)**: Remove the file path from the `srcs`
          list in the appropriate BUILD.bazel file.

        - **New ProjectReferences in .csproj**: Add the corresponding
          `//src/libraries:ref_<AssemblyName>` entry to the `deps` list.

        - **New DefineConstants in .csproj**: Add the constant to the `defines` list.

        - **New .resx files**: Add a `resx_file` parameter if not already present.

        You may modify: BUILD.bazel, MODULE.bazel, paket/paket.main.bzl, and
        src/libraries/defs.bzl files. Do not modify any other file types.
        """;

    var response = await session.SendAndWaitAsync(
        new MessageOptions { Prompt = prompt },
        TimeSpan.FromMinutes(20));

    Console.WriteLine(response?.Data?.Content ?? "(no response)");
    Console.WriteLine("Copilot session completed.");
    return 0;
}
catch (TimeoutException ex)
{
    Console.Error.WriteLine($"Copilot session timed out: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Copilot session was cancelled");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Copilot session failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

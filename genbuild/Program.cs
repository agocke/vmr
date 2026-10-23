using System.ComponentModel;
using System.Diagnostics;

if (args is ["--help"] or ["-h"] or [])
{
    Console.WriteLine("Usage: dotnet genbuild.dll <restore|build> <project> [MSBuild arguments]");
    Console.WriteLine("restore: prepare dependencies and capture graph inputs.");
    Console.WriteLine("build: emit obj/genbuild/build.ninja without executing its actions.");
    return args.Length == 0 ? 1 : 0;
}

if (args.Length < 2 || args[0] is not ("restore" or "build"))
{
    Console.Error.WriteLine("genbuild: expected 'restore' or 'build', followed by a project path. See --help.");
    return 1;
}

try
{
    string toolset = Path.Combine(AppContext.BaseDirectory, "toolset");
    string engine = Path.Combine(toolset, "MSBuild.dll");
    string host = Environment.ProcessPath
        ?? throw new InvalidOperationException("The external dotnet host path is unavailable.");
    string project = Path.GetFullPath(args[1]);

    if (!File.Exists(engine))
    {
        Console.Error.WriteLine($"genbuild: the app toolset is missing: '{engine}'. Build genbuild.proj first.");
        return 1;
    }

    ProcessStartInfo start = new(host) { UseShellExecute = false };
    // NuGet's runtime closure normally comes from the SDK driver's dependency manifest.
    start.ArgumentList.Add("exec");
    start.ArgumentList.Add("--additional-deps");
    start.ArgumentList.Add(Path.Combine(toolset, "Toolset.deps.json"));
    start.ArgumentList.Add(engine);
    start.ArgumentList.Add(project);
    start.ArgumentList.Add("-nologo");
    start.ArgumentList.Add("-m:1");
    start.ArgumentList.Add("-nr:false");
    start.ArgumentList.Add("-p:MSBuildRestoreGraphInputs=true");
    start.ArgumentList.Add($"-p:MSBuildGraphSdkBindingsDirectory={Path.Combine(Path.GetDirectoryName(project)!, "obj", "genbuild", "sdk-bindings")}");

    if (args[0] == "restore")
    {
        start.ArgumentList.Add("-t:Restore");
    }
    else
    {
        // Package SDK discovery belongs to Restore, not hardened construction.
        start.Environment["MSBUILDDISABLENUGETSDKRESOLVER"] = "1";
        string graph = Path.Combine(Path.GetDirectoryName(project)!, "obj", "genbuild", "build.ninja");
        start.ArgumentList.Add("-t:Build");
        start.ArgumentList.Add("--hardened-graph");
        start.ArgumentList.Add($"-p:MSBuildEmitActionGraph={graph}");
        start.ArgumentList.Add($"-p:MSBuildActionTaskHost={Path.Combine(toolset, "action-host", "MSBuild.ActionTaskHost.dll")}");
        start.ArgumentList.Add($"-p:MSBuildActionDotNetHost={host}");
    }

    foreach (string argument in args.AsSpan(2))
    {
        start.ArgumentList.Add(argument);
    }

    start.Environment["DOTNET_HOST_PATH"] = host;
    start.Environment["MSBUILD_EXE_PATH"] = engine;
    start.Environment["MSBuildSDKsPath"] = Path.Combine(toolset, "Sdks");
    start.Environment["MSBuildExtensionsPath"] = toolset;
    start.Environment["MSBuildExtensionsPath32"] = toolset;
    start.Environment["NetCoreRoot"] = toolset + Path.DirectorySeparatorChar;
    start.Environment["MSBuildEnableWorkloadResolver"] = "false";
    start.Environment["MSBUILDUSESERVER"] = "0";
    start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

    using Process process = Process.Start(start)
        ?? throw new InvalidOperationException("MSBuild could not be started.");
    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"genbuild: {ex.Message}");
    return 1;
}

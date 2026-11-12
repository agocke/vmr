// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

public sealed class CollectFrameworkPackInputs : TaskBase
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    [Required]
    public string ProjectPath { get; set; } = "";
    [Required]
    public string AssetsFile { get; set; } = "";
    [Required]
    public string OutputFile { get; set; } = "";
    [Required]
    public string ResourceDirectory { get; set; } = "";
    public string TargetingPackRoot { get; set; } = "";
    public string NetCoreRoot { get; set; } = "";
    public string NETCoreSdkVersion { get; set; } = "";
    public string RuntimeGraphPath { get; set; } = "";
    public ITaskItem[] PackRequests { get; set; } = [];
    public ITaskItem[] LookupFacts { get; set; } = [];
    public ITaskItem[] VersionFacts { get; set; } = [];
    public ITaskItem[] PackMembers { get; set; } = [];
    [Output]
    public ITaskItem[] Bindings { get; private set; } = [];

    protected override void ExecuteCore()
    {
        string binding = Guid.NewGuid().ToString();
        TaskItem header = new(binding);
        Set(header, "Type", "FrameworkPackInputRequest");
        Set(header, "ProjectUniqueName", ProjectPath);
        Set(header, "AssetsFile", Absolute(AssetsFile));
        Set(header, "OutputFile", Absolute(OutputFile));
        Set(header, "ResourceDirectory", Absolute(ResourceDirectory));
        Set(header, "TargetingPackRoot", Absolute(TargetingPackRoot));
        Set(header, "NetCoreRoot", Absolute(NetCoreRoot));
        Set(header, "NETCoreSdkVersion", NETCoreSdkVersion);
        Set(header, "RuntimeGraphPath", Absolute(RuntimeGraphPath));
        List<ITaskItem> rows = [header];
        Add(PackRequests, "PackRequest");
        Add(LookupFacts, "Lookup");
        Add(VersionFacts, "Version");
        Add(PackMembers, "Member");
        Bindings = rows.ToArray();

        void Add(ITaskItem[] inputs, string kind)
        {
            foreach (ITaskItem input in inputs)
            {
                TaskItem row = new(input);
                row.ItemSpec = Guid.NewGuid().ToString();
                Set(row, "Type", "FrameworkPackInput");
                Set(row, "ProjectUniqueName", ProjectPath);
                Set(row, "Binding", binding);
                Set(row, "InputKind", kind);
                Set(row, "InputIdentity", input.ItemSpec);
                foreach (string name in kind == "Member" ? new[] { "PackRoot" } : new[] { "Path" })
                {
                    if (input.GetMetadata(name).Length != 0)
                    {
                        Set(row, name, Absolute(input.GetMetadata(name)));
                    }
                }
                rows.Add(row);
            }
        }
    }

    private string Absolute(string value) => value.Length == 0 ? "" : TaskEnvironment.GetAbsolutePath(value).Value;

    internal static void Set(ITaskItem item, string name, string value)
        => item.SetMetadata(name, ProjectCollection.Escape(value));
}

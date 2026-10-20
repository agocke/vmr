// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ActionTaskHost.UnitTests;

public sealed class ActionGraph_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [UnixOnlyTheory]
    [InlineData("assigned")]
    [InlineData("unassigned")]
    [InlineData("disabled")]
    [InlineData("synthetic")]
    [InlineData("invalid-guid")]
    [InlineData("invalid-xml")]
    public void AssignedProjectConfigurationsAreStaticWithoutReferencedFiles(string mode)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "root.proj");
        const string rootGuid = "{11111111-1111-1111-1111-111111111111}";
        const string childGuid = "{22222222-2222-2222-2222-222222222222}";
        const string syntheticGuid = "{33333333-3333-3333-3333-333333333333}";
        XElement solution = new("SolutionConfiguration",
            new XElement("ProjectConfiguration",
                new XAttribute("Project", rootGuid), new XAttribute("AbsolutePath", project), "Debug|AnyCPU"),
            new XElement("ProjectConfiguration",
                new XAttribute("Project", childGuid),
                new XAttribute("AbsolutePath", Path.Combine(directory, "missing", "Library.proj")),
                new XAttribute("BuildProjectInSolution", mode != "disabled"), "Release|AnyCPU"));
        if (mode == "unassigned")
        {
            solution.Elements().Last().Remove();
        }
        else if (mode == "synthetic")
        {
            solution.Elements().First().Add(new XElement("ProjectDependency", new XAttribute("Project", syntheticGuid)));
            solution.Add(new XElement("ProjectConfiguration",
                new XAttribute("Project", syntheticGuid),
                new XAttribute("AbsolutePath", Path.Combine(directory, "missing", "Synthetic.proj")), "Debug|AnyCPU"));
        }

        XElement root = XElement.Parse("""
            <Project DefaultTargets="Build">
              <ItemGroup>
                <ProjectReference Include="missing/Library.proj" GlobalPropertiesToRemove="Existing" Payload="literal%3Bvalue" />
              </ItemGroup>
              <Target Name="Build" DependsOnTargets="AssignProjectConfiguration">
                <WriteLinesToFile File="$(ResultFile)" Overwrite="true"
                                  Lines="@(ProjectReferenceWithConfiguration->'%(Identity)|%(SetConfiguration)|%(SetPlatform)|%(BuildReference)|%(ReferenceOutputAssembly)|%(GlobalPropertiesToRemove)|%(Payload)|%(SkipGetTargetFrameworkProperties)')"
                                  DeclaredInputs="" DeclaredOutputs="$(ResultFile)" />
              </Target>
            </Project>
            """);
        root.Element("ItemGroup")!.Element("ProjectReference")!
            .SetAttributeValue("Project", mode == "invalid-guid" ? "not-a-guid" : childGuid);
        root.AddFirst(new XElement("PropertyGroup",
            new XElement("CurrentSolutionConfigurationContents", mode == "invalid-xml" ? "<invalid" : solution.ToString()),
            new XElement("ShouldUnsetParentConfigurationAndPlatform", "true")));
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        root.Add(new XElement(common.Elements("Target")
            .Where(target => target.Attribute("Name")?.Value == "AssignProjectConfiguration").ShouldHaveSingleItem()));
        File.WriteAllText(project, root.ToString());
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -p:ResultFile=ordinary.txt -nr:false -v:minimal", out bool success, outputHelper: _output);
        bool valid = !mode.StartsWith("invalid", StringComparison.Ordinal);
        success.ShouldBe(valid);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-p:ResultFile=action.txt").ShouldBe(valid);

        Directory.Exists(Path.Combine(directory, "missing")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "action.txt")).ShouldBeFalse();
        if (!valid)
        {
            File.Exists(graph).ShouldBeFalse();
            Directory.Exists(graph + ".actions").ShouldBeFalse();
            return;
        }

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(1);
        Ninja(graph, out success);

        success.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(directory, "action.txt"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(directory, "ordinary.txt")));
        string result = File.ReadAllText(Path.Combine(directory, "action.txt"));
        result.ShouldContain("literal;value");
        if (mode == "unassigned")
        {
            result.ShouldContain("Existing;Configuration;Platform");
        }
        else
        {
            result.ShouldContain("Configuration=Release|Platform=AnyCPU|");
        }

        if (mode == "disabled")
        {
            result.ShouldContain("|false|false|");
        }
        else if (mode == "synthetic")
        {
            result.ShouldContain("Synthetic.proj|Configuration=Debug|Platform=AnyCPU|true|false|TargetFramework|");
        }
    }

    [UnixOnlyTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void HardenedProjectReferenceExistenceIsValidatedByEngineRequests(bool exists)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        if (exists)
        {
            File.WriteAllText(Path.Combine(directory, "child.proj"), """
                <Project><Target Name="Describe" Returns="static-value" /></Project>
                """);
        }

        XElement root = XElement.Parse("""
            <Project DefaultTargets="Build">
              <ItemGroup><ProjectReferenceWithConfiguration Include="child.proj" /></ItemGroup>
              <Target Name="Build" DependsOnTargets="_SplitProjectReferencesByFileExistence">
                <MSBuild Projects="@(_MSBuildProjectReferenceExistent)" Targets="Describe">
                  <Output TaskParameter="TargetOutputs" ItemName="Returned" />
                </MSBuild>
                <WriteLinesToFile File="result.txt" Lines="@(Returned)" Overwrite="true"
                                  DeclaredInputs="" DeclaredOutputs="result.txt" />
              </Target>
            </Project>
            """);
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        root.Add(new XElement(common.Elements("Target")
            .Where(target => target.Attribute("Name")?.Value == "_SplitProjectReferencesByFileExistence").ShouldHaveSingleItem()));
        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, root.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBe(exists);

        File.Exists(Path.Combine(directory, "result.txt")).ShouldBeFalse();
        if (!exists)
        {
            log.ShouldContain("MSB3202");
            File.Exists(graph).ShouldBeFalse();
            Directory.Exists(graph + ".actions").ShouldBeFalse();
            return;
        }

        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "result.txt")).Trim().ShouldBe("static-value");
    }

    [UnixOnlyTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("$(MissingTargets)")]
    [InlineData("Describe")]
    public void ChildDefaultTargetOutputsMatchOrdinaryBuild(string? targets)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(directory, "child.proj"), """
            <Project DefaultTargets="Describe">
              <Target Name="Describe" Returns="@(Product)">
                <ItemGroup>
                  <Product Include="static-child" Tag="literal%3Bvalue" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        XElement call = new("MSBuild", new XAttribute("Projects", "child.proj"),
            new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"),
                new XAttribute("ItemName", "Returned")));
        if (targets is not null)
        {
            call.Add(new XAttribute("Targets", targets));
        }

        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, new XElement("Project",
            new XElement("Target", new XAttribute("Name", "Build"), call,
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(ResultFile)"),
                    new XAttribute("Lines", "@(Returned->'%(Identity)|%(Tag)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("DeclaredInputs", ""),
                    new XAttribute("DeclaredOutputs", "$(ResultFile)")))).ToString());
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -p:ResultFile=ordinary.txt -nr:false -v:minimal", out bool success, outputHelper: _output);
        success.ShouldBeTrue();
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-p:ResultFile=action.txt").ShouldBeTrue();
        Ninja(graph, out success);

        success.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(directory, "action.txt"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(directory, "ordinary.txt")));
        File.ReadAllText(Path.Combine(directory, "action.txt")).Trim().ShouldBe("static-child|literal;value");
    }

    [UnixOnlyTheory]
    [InlineData("returns", false, 2)]
    [InlineData("returns", true, 3)]
    [InlineData("legacy", false, 2)]
    [InlineData("legacy", true, 3)]
    [InlineData("suppressed", false, 0)]
    public void ChildTargetReturnRulesMatchOrdinaryBuild(string mode, bool keepDuplicates, int count)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        XElement child = new("Project",
            new XElement("Target",
                new XAttribute("Name", "Query"),
                new XAttribute(mode == "returns" ? "Returns" : "Outputs", "@(Product)"),
                new XAttribute("KeepDuplicateOutputs", keepDuplicates),
                new XElement("ItemGroup",
                    new XElement("Product", new XAttribute("Include", "one;one;two"),
                        new XAttribute("KeepDuplicates", "true"), new XAttribute("Tag", "literal%3Bvalue")))));
        if (mode == "suppressed")
        {
            child.Add(new XElement("Target", new XAttribute("Name", "Unused"), new XAttribute("Returns", "")));
        }

        File.WriteAllText(Path.Combine(directory, "child.proj"), child.ToString());
        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, """
            <Project>
              <Target Name="Build">
                <MSBuild Projects="child.proj" Targets="Missing;Query" SkipNonexistentTargets="true" RunEachTargetSeparately="true">
                  <Output TaskParameter="TargetOutputs" ItemName="Returned" />
                </MSBuild>
                <PropertyGroup><ReturnedCount>@(Returned->Count())</ReturnedCount></PropertyGroup>
                <WriteLinesToFile File="$(ResultFile)" Lines="count=$(ReturnedCount);@(Returned->'%(Identity)|%(Tag)')"
                                  Overwrite="true" DeclaredInputs="" DeclaredOutputs="$(ResultFile)" />
              </Target>
            </Project>
            """);
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -p:ResultFile=ordinary.txt -nr:false -v:minimal", out bool success, outputHelper: _output);
        success.ShouldBeTrue();
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-p:ResultFile=action.txt").ShouldBeTrue();
        Ninja(graph, out success);

        success.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(directory, "action.txt"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(directory, "ordinary.txt")));
        File.ReadAllLines(Path.Combine(directory, "action.txt"))[0].ShouldBe("count=" + count);
    }

    [UnixOnlyFact]
    public void DiamondReferencesRecordTheSharedProducerOnlyOnce()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(directory, "leaf.proj"), """
            <Project>
              <Target Name="Build" Returns="$(MSBuildProjectDirectory)/leaf.txt">
                <WriteLinesToFile File="leaf.txt" Lines="shared" Overwrite="true"
                                  DeclaredInputs="" DeclaredOutputs="leaf.txt" />
              </Target>
            </Project>
            """);
        foreach (string name in new[] { "left", "right" })
        {
            File.WriteAllText(Path.Combine(directory, name + ".proj"), """
                <Project>
                  <Target Name="Build" Returns="@(Leaf)">
                    <MSBuild Projects="leaf.proj" Targets="Build">
                      <Output TaskParameter="TargetOutputs" ItemName="Leaf" />
                    </MSBuild>
                  </Target>
                </Project>
                """);
        }

        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, """
            <Project>
              <Target Name="Build">
                <MSBuild Projects="left.proj;right.proj" Targets="Build" BuildInParallel="true">
                  <Output TaskParameter="TargetOutputs" ItemName="References" />
                </MSBuild>
                <PropertyGroup><ReferenceCount>@(References->Count())</ReferenceCount></PropertyGroup>
                <WriteLinesToFile File="result.txt" Lines="count=$(ReferenceCount)" Overwrite="true"
                                  DeclaredInputs="@(References)" DeclaredOutputs="result.txt" />
              </Target>
            </Project>
            """);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "leaf.txt")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "leaf.txt")).Trim().ShouldBe("shared");
        File.ReadAllText(Path.Combine(directory, "result.txt")).Trim().ShouldBe("count=2");
    }

    [UnixOnlyFact]
    public void AConditionallySkippedChildTargetCanRunInALaterRequest()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(directory, "child.proj"), """
            <Project>
              <Target Name="Conditional" Condition="'$(Enabled)' == 'true'" Returns="executed">
                <WriteLinesToFile File="child.txt" Lines="executed" Overwrite="true"
                                  DeclaredInputs="" DeclaredOutputs="child.txt" />
              </Target>
              <Target Name="Enable">
                <PropertyGroup><Enabled>true</Enabled></PropertyGroup>
              </Target>
            </Project>
            """);
        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, """
            <Project>
              <Target Name="Build">
                <MSBuild Projects="child.proj" Targets="Conditional" />
                <MSBuild Projects="child.proj" Targets="Enable" />
                <MSBuild Projects="child.proj" Targets="Conditional">
                  <Output TaskParameter="TargetOutputs" ItemName="Returned" />
                </MSBuild>
                <WriteLinesToFile File="root.txt" Lines="@(Returned)" Overwrite="true"
                                  DeclaredInputs="child.txt" DeclaredOutputs="root.txt" />
              </Target>
            </Project>
            """);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "root.txt")).Trim().ShouldBe("executed");
    }

    [UnixOnlyFact]
    public void ProjectReferencesShareOneGraphAndPreserveConfiguredTargetState()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string childDirectory = Path.Combine(directory, "child");
        Directory.CreateDirectory(childDirectory);
        string child = Path.Combine(childDirectory, "child.proj");
        File.WriteAllText(child, """
            <Project>
              <Target Name="Query" Returns="@(Product)">
                <PropertyGroup>
                  <Prepared>$(Common)$(Removed)-$(Variant)</Prepared>
                </PropertyGroup>
                <ItemGroup>
                  <Product Include="out/$(Variant).txt" Payload="$(Prepared)%3Bescaped" />
                </ItemGroup>
              </Target>
              <Target Name="Build" DependsOnTargets="Query" Returns="@(Product)">
                <WriteLinesToFile File="@(Product)" Lines="$(Prepared)" Overwrite="true"
                                  DeclaredInputs="" DeclaredOutputs="@(Product)" />
              </Target>
            </Project>
            """);
        string project = Path.Combine(directory, "root.proj");
        File.WriteAllText(project, """
            <Project>
              <ItemGroup>
                <Child Include="child/child.proj" AdditionalProperties="Variant=one" UndefineProperties="Removed" Caller="first" />
                <Child Include="child/child.proj" AdditionalProperties="Variant=two" UndefineProperties="Removed" Caller="second" />
              </ItemGroup>
              <Target Name="Build">
                <MSBuild Projects="@(Child)" Targets="Query" RebaseOutputs="true" Properties="Common=shared;Removed=stale">
                  <Output TaskParameter="TargetOutputs" ItemName="Queries" />
                </MSBuild>
                <MSBuild Projects="@(Child)" Targets="Build" RebaseOutputs="true" Properties="Common=shared;Removed=stale">
                  <Output TaskParameter="TargetOutputs" ItemName="Built" />
                </MSBuild>
                <MSBuild Projects="@(Child)" Targets="Build" RebaseOutputs="true" Properties="Common=shared;Removed=stale" />
                <WriteLinesToFile File="$(SummaryFile)" Overwrite="true"
                                  Lines="@(Queries->'%(Identity)|%(Payload)|%(Caller)|%(MSBuildSourceProjectFile)|%(MSBuildSourceTargetName)');@(Built->'%(Identity)|%(Payload)|%(Caller)|%(MSBuildSourceProjectFile)|%(MSBuildSourceTargetName)')"
                                  DeclaredInputs="@(Built)" DeclaredOutputs="$(SummaryFile)" />
              </Target>
            </Project>
            """);
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -p:SummaryFile=ordinary.txt -nr:false -v:minimal", out bool success, outputHelper: _output);
        success.ShouldBeTrue();
        string[] outputs = [Path.Combine(childDirectory, "out", "one.txt"), Path.Combine(childDirectory, "out", "two.txt")];
        foreach (string file in outputs)
        {
            File.Delete(file);
        }

        string graph = Path.Combine(directory, "build.ninja");
        Generate(project, graph, "-p:SummaryFile=action.txt").ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(3);
        File.Exists(Path.Combine(directory, "action.txt")).ShouldBeFalse();
        foreach (string file in outputs)
        {
            File.Exists(file).ShouldBeFalse();
        }

        Ninja(graph, out success);

        success.ShouldBeTrue();
        File.ReadAllText(outputs[0]).Trim().ShouldBe("shared-one");
        File.ReadAllText(outputs[1]).Trim().ShouldBe("shared-two");
        File.ReadAllBytes(Path.Combine(directory, "action.txt"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(directory, "ordinary.txt")));
        string summary = File.ReadAllText(Path.Combine(directory, "action.txt"));
        summary.ShouldContain("shared-one;escaped|first|");
        summary.ShouldContain("shared-two;escaped|second|");
        summary.ShouldContain("|Query");
        summary.ShouldContain("|Build");
    }

    [UnixOnlyTheory]
    [InlineData("missing", "MSB3202")]
    [InlineData("cycle", "circular")]
    [InlineData("conflict", "conflicting outputs")]
    [InlineData("override", "unaudited tasks")]
    [InlineData("destination", "child project changing")]
    [InlineData("remove-host", "MSBuildGraphHostOS")]
    [InlineData("child-failure", "DeclaredIO")]
    public void InvalidProjectReferenceClosurePublishesNothing(string mode, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "root.proj");
        string child = Path.Combine(directory, "child.proj");
        XElement call = new("MSBuild", new XAttribute("Projects", "child.proj"), new XAttribute("Targets", "Build"));
        if (mode == "destination")
        {
            call.SetAttributeValue("Properties", "MSBuildEmitActionGraph=" + Path.Combine(directory, "other.ninja"));
        }
        else if (mode == "remove-host")
        {
            call.SetAttributeValue("RemoveProperties", "MSBuildGraphHostOS");
        }

        XElement root = new("Project",
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "root.txt"), new XAttribute("Lines", "root"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("DeclaredInputs", ""), new XAttribute("DeclaredOutputs", "root.txt")),
                call));
        if (mode == "override")
        {
            root.AddFirst(new XElement("UsingTask",
                new XAttribute("TaskName", typeof(MSBuild).FullName!),
                new XAttribute("AssemblyFile", typeof(MSBuild).Assembly.Location)));
            call.SetAttributeValue("File", Path.Combine(directory, "unaudited.txt"));
            call.Attribute("Targets")!.Remove();
        }

        File.WriteAllText(project, root.ToString());
        if (mode != "missing")
        {
            string body = mode switch
            {
                "cycle" => """<MSBuild Projects="root.proj" Targets="Build" />""",
                "child-failure" => """<Exec Command="must-not-run" />""",
                _ => $"""<WriteLinesToFile File="{(mode == "conflict" ? "root.txt" : "child.txt")}" Lines="child" Overwrite="true" DeclaredInputs="" DeclaredOutputs="{(mode == "conflict" ? "root.txt" : "child.txt")}" />""",
            };
            File.WriteAllText(child, $"""<Project><Target Name="Build">{body}</Target></Project>""");
        }

        string graph = Path.Combine(directory, "build.ninja");
        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(expected, Case.Insensitive);
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "other.ninja")).ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, "other.ninja.actions")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "root.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "child.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "unaudited.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData(null, "<null>|constructor-default")]
    [InlineData("", "<null>|constructor-default")]
    [InlineData("$(Missing)", "<null>|constructor-default")]
    [InlineData("literal%253B%3Bvalue", "literal%3B;value|literal%3B;value")]
    public void CapturedScalarParametersMatchOrdinaryDefaults(string? value, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "parameters.proj");
        XElement task = new("GraphParameterTask",
            new XAttribute("File", "$(ResultFile)"),
            new XAttribute("DeclaredInputs", ""),
            new XAttribute("DeclaredOutputs", "$(ResultFile)"));
        if (value is not null)
        {
            task.Add(new XAttribute("NullText", value), new XAttribute("DefaultText", value));
        }

        XDocument contents = new(new XElement("Project",
            new XElement("UsingTask",
                new XAttribute("TaskName", "GraphParameterTask"),
                new XAttribute("AssemblyFile", typeof(GraphParameterTask).Assembly.Location)),
            new XElement("Target", new XAttribute("Name", "Build"), task)));
        File.WriteAllText(project, contents.ToString());

        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -p:ResultFile=ordinary.txt -nr:false -v:minimal", out bool succeeded, outputHelper: _output);
        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "ordinary.txt")).ShouldBe(expected);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-p:ResultFile=action.txt").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "action.txt")).ShouldBeFalse();
        string manifest = Directory.GetFiles(graph + ".actions", "*.json").ShouldHaveSingleItem();
        using JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement parameters = invocation.RootElement.GetProperty("parameters");
        bool hasValue = value is { Length: > 0 } && value != "$(Missing)";
        parameters.TryGetProperty("NullText", out _).ShouldBe(hasValue);
        parameters.TryGetProperty("DefaultText", out _).ShouldBe(hasValue);

        Ninja(graph, out succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(directory, "action.txt"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(directory, "ordinary.txt")));
    }

    [UnixOnlyTheory]
    [InlineData("")]
    [InlineData("$(Missing)")]
    public void CapturedRequiredScalarParametersRejectEmptyValues(string value)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "parameters.proj");
        XDocument contents = new(new XElement("Project",
            new XElement("UsingTask",
                new XAttribute("TaskName", "GraphParameterTask"),
                new XAttribute("AssemblyFile", typeof(GraphParameterTask).Assembly.Location)),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("GraphParameterTask",
                    new XAttribute("File", value),
                    new XAttribute("DeclaredInputs", ""),
                    new XAttribute("DeclaredOutputs", "result.txt")))));
        File.WriteAllText(project, contents.ToString());

        string ordinaryLog = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -nr:false -v:minimal", out bool succeeded, outputHelper: _output);
        succeeded.ShouldBeFalse();
        ordinaryLog.ShouldContain("MSB4044");
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("MSB4044");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "result.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HardenedReferenceDefaultsIgnoreInstallationAssemblyFolders(bool sdkProfile, bool configExists)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile, configExists);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt"))
            .ShouldBe("literal%3B;value|{CandidateAssemblyFiles};{HintPathFromItem};{TargetFrameworkDirectory}|");
    }

    [UnixOnlyTheory]
    [InlineData("{gac}")]
    [InlineData("{Registry:machine,v4.8,AssemblyFoldersEx}")]
    [InlineData("{AssemblyFoldersFromConfig:machine.config,v4.8}")]
    [InlineData("{RawFileName}")]
    [InlineData("relative-reference-directory")]
    public void RejectsUnsafeExplicitReferenceSearchOverridesBeforePublishingGraph(string searchPath)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile: true, configExists: false, searchPath);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("MSB3669");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void AcceptsAnExplicitAbsoluteReferenceSearchDirectoryWithoutProbingIt()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string references = Path.Combine(directory, "nonexistent-reference-pack");
        string project = WriteReferenceSearchProject(directory, sdkProfile: true, configExists: false, references);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe("literal%3B;value|" + references + "|");
        Directory.Exists(references).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CommonDirectoryPreparationUsesOutputParents(bool callTarget, bool withDirectories)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteDirectoryPreparationProject(directory, callTarget, withDirectories);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        Directory.Exists(Path.Combine(directory, "obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, "bin")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        string result = Path.Combine(directory, "bin", "app", "second.txt");
        File.ReadAllText(result).ShouldBe("literal%3B;value|metadata%25|consumed");
        DateTime modified = File.GetLastWriteTimeUtc(result);
        string unchanged = Ninja(graph, out success);

        success.ShouldBeTrue();
        unchanged.ShouldContain("no work to do");
        File.GetLastWriteTimeUtc(result).ShouldBe(modified);

        Directory.Delete(Path.Combine(directory, "obj"), recursive: true);
        Ninja(graph, out success);

        success.ShouldBeTrue();
        File.ReadAllText(result).ShouldBe("literal%3B;value|metadata%25|consumed");
        File.Exists(Path.Combine(directory, "obj", "nested", "first.txt")).ShouldBeTrue();
    }

    [UnixOnlyTheory]
    [InlineData("unrelated", false, false)]
    [InlineData("unrelated", true, false)]
    [InlineData("unrelated", false, true)]
    [InlineData("obj-sibling", false, false)]
    [InlineData("obj/nested/empty", false, false)]
    [InlineData("bin/app/second.txt", false, false)]
    public void RejectsUncoveredDirectoryEffects(string requested, bool preexisting, bool callTarget)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteDirectoryPreparationProject(directory, callTarget);
        XDocument contents = XDocument.Load(project);
        contents.Root!.AddFirst(new XElement("ItemGroup",
            new XElement("CreateDirectory", new XAttribute("Include", requested))));
        File.WriteAllText(project, contents.ToString());
        if (preexisting)
        {
            Directory.CreateDirectory(Path.Combine(directory, requested));
        }

        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("directory requirement");
        log.ShouldContain("without a declared file output beneath it");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, "obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, "bin")).ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, requested)).ShouldBe(preexisting);
    }

    [UnixOnlyFact]
    public void DirectoryRequirementsDoNotExpandWildcards()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteDirectoryPreparationProject(directory);
        XDocument contents = XDocument.Load(project);
        contents.Root!.Element("PropertyGroup")!.Element("OutDir")!.Value = "wildcard*";
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("nonexact declared path");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        Directory.Exists(Path.Combine(directory, "obj")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void OrdinaryDirectoryPreparationStillCreatesArbitraryDirectories()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteDirectoryPreparationProject(directory);
        XDocument contents = XDocument.Load(project);
        contents.Root!.Elements("Target").Single(target => target.Attribute("Name")!.Value == "Build").RemoveNodes();
        contents.Root.AddFirst(new XElement("ItemGroup",
            new XElement("CreateDirectory", new XAttribute("Include", "empty-extra-directory"))));
        File.WriteAllText(project, contents.ToString());

        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -nr:false -v:minimal", out bool succeeded, outputHelper: _output);

        succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(directory, "bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(directory, "obj", "nested")).ShouldBeTrue();
        Directory.Exists(Path.Combine(directory, "empty-extra-directory")).ShouldBeTrue();
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryReferenceDefaultsRetainTheirExistingSearchSources(bool sdkProfile)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile, configExists: true);

        string contents = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -getProperty:AssemblySearchPaths,AssemblyFoldersConfigFileSearchPath -nr:false -nologo -v:q",
            out bool succeeded, outputHelper: _output);

        succeeded.ShouldBeTrue();
        contents.ShouldContain("{RawFileName}");
        contents.ShouldContain("{AssemblyFoldersFromConfig:");
        if (!sdkProfile)
        {
            contents.ShouldContain("{GAC}");
            contents.ShouldContain("{Registry:");
            contents.ShouldContain("{AssemblyFolders}");
        }
    }

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RegularRestoreCapturesGraphInputsOnlyWhenRequested(bool capture, bool multiTarget)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "restore.csproj");
        XDocument contents = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources", "HelloWorld.csproj"));
        XElement targetFramework = contents.Descendants("TargetFramework").ShouldHaveSingleItem();
        string[] frameworks = multiTarget
            ? [targetFramework.Value, targetFramework.Value + "-windows"]
            : [targetFramework.Value];
        targetFramework.Name = multiTarget ? "TargetFrameworks" : "TargetFramework";
        targetFramework.Value = string.Join(";", frameworks);
        contents.Root!.Element("PropertyGroup")!.Add(
            new XElement("RestoreSources", directory),
            new XElement("NuGetAudit", "false"));
        File.WriteAllText(project, contents.ToString());
        if (multiTarget)
        {
            foreach (string framework in frameworks)
            {
                string intermediate = Path.Combine(directory, "obj", "Debug", framework);
                Directory.CreateDirectory(intermediate);
                File.WriteAllLines(Path.Combine(intermediate, "restore.csproj.FileListAbsolute.txt"),
                    [Path.Combine(directory, "prior-" + framework + ".dll")]);
            }

            File.WriteAllText(Path.Combine(directory, "obj", "restore.csproj.UnmanagedRegistration.cache"), "");
        }

        string arguments = $"\"{project}\" -t:Restore -p:MSBuildRestoreGraphInputs={capture} -nr:false -v:minimal";
        RunnerUtilities.ExecBootstrapedMSBuild(arguments, out bool succeeded, outputHelper: _output);
        succeeded.ShouldBeTrue();
        Dictionary<string, DateTime> writes = new();
        foreach (string framework in frameworks)
        {
            string snapshotDirectory = Path.Combine(directory, "obj", "hardened", "Debug", "AnyCPU", framework);
            string sdk = Path.Combine(snapshotDirectory, "restore.csproj.platform-sdk.props");
            string unmanaged = Path.Combine(snapshotDirectory, "restore.csproj.UnmanagedRegistration.cache.props");
            string clean = Path.Combine(directory, "obj", "Debug", framework, "restore.csproj.FileListAbsolute.txt.hardened.props");
            File.Exists(clean).ShouldBeFalse();
            string[] imports = [sdk, unmanaged];
            foreach (string file in imports)
            {
                File.Exists(file).ShouldBe(capture, file);
                if (capture)
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
                    writes.Add(file, File.GetLastWriteTimeUtc(file));
                }
            }

            if (capture)
            {
                XElement cache = XDocument.Load(unmanaged).Root!;
                XNamespace ns = cache.Name.Namespace;
                (cache.Element(ns + "PropertyGroup")?.Element(ns + "_HardenedUnmanagedRegistrationCacheExists")?.Value ?? "")
                    .ShouldBe(multiTarget ? "true" : "");
            }
        }

        if (capture)
        {
            RunnerUtilities.ExecBootstrapedMSBuild(arguments, out succeeded, outputHelper: _output);
            succeeded.ShouldBeTrue();
            foreach (KeyValuePair<string, DateTime> write in writes)
            {
                File.GetLastWriteTimeUtc(write.Key).ShouldBe(write.Value);
            }
        }
    }

    [UnixOnlyTheory]
    [InlineData(false, "out $ space'quote/", "out $ space'quote/")]
    [InlineData(true, "out $ space'quote/", "out $ space'quote/")]
    [InlineData(false, "unused/../out %253B%3B space/", "out %3B; space/")]
    public void ConstructsTargetDirectoryFromTheExplicitProjectBase(bool absolute, string outDir, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string commonTargets = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion, "Microsoft.Common.CurrentVersion.targets");
        XElement properties = new("PropertyGroup",
            new XElement("OutDir", absolute ? Path.Combine(directory, outDir) : outDir),
            new XElement("TargetDir", "must-be-overridden"));
        foreach (XElement element in XDocument.Load(commonTargets).Root!.Elements("PropertyGroup").Elements("TargetDir"))
        {
            properties.Add(new XElement(element));
        }

        string contents = File.ReadAllText(project)
            .Replace("<Target Name=\"Build\"", properties + "\n<Target Name=\"Build\"")
            .Replace("</GraphValueTask>", """
                </GraphValueTask>
                <ItemGroup>
                  <Values>
                    <Tag>$(TargetDir)</Tag>
                  </Values>
                </ItemGroup>
                """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt"))
            .ShouldBe("literal%3B;value|" + Path.GetFullPath(Path.Combine(directory, expected)));
    }

    [UnixOnlyTheory]
    [InlineData("Windows Metadata")]
    [InlineData("References/CommonConfiguration/Neutral")]
    public void RestoreCapturesSDKIOButLeavesPropertyDerivationInGraphConstruction(string metadataSubdirectory)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string sdk = Path.Combine(directory, "sdk & space'quote");
        string metadata = Path.Combine(sdk, metadataSubdirectory);
        Directory.CreateDirectory(metadata);
        string project = WritePlatformSDKProject(directory, sdk);
        RestorePlatformSDK(project).ShouldBeTrue();
        string snapshot = Path.Combine(directory, "sdk.props");
        string contents = File.ReadAllText(snapshot);
        contents.ShouldContain("<_PlatformSDKPathExists>true</_PlatformSDKPathExists>");
        contents.ShouldNotContain("<TargetPlatformSdkPath>");
        contents.ShouldNotContain("<TargetPlatformWinMDLocation>");
        File.SetLastWriteTimeUtc(snapshot, DateTime.UtcNow.AddMinutes(-10));
        DateTime written = File.GetLastWriteTimeUtc(snapshot);
        RestorePlatformSDK(project).ShouldBeTrue();
        File.GetLastWriteTimeUtc(snapshot).ShouldBe(written);
        Directory.Move(sdk, sdk + ".moved");
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph,
            "-p:TargetPlatformWinMDLocation=explicit-winmd -p:TargetPlatformDisplayName=explicit-display").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "result.txt")).ShouldBeFalse();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "result.txt")).Replace('\\', '/')
            .ShouldBe(metadata.Replace('\\', '/') + "|explicit-winmd|GraphMissingSDK,Version=1.0|explicit-display|");
    }

    [UnixOnlyTheory]
    [InlineData("MSBuildRestoreGraphInputs")]
    [InlineData("MSBuildRestorePlatformSDK")]
    public void RestoreRecordsAnAbsentSDKWithoutRepeatingMachineDiscovery(string restoreProperty)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        RestorePlatformSDK(project, restoreProperty).ShouldBeTrue();
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "result.txt"))
            .ShouldBe("||GraphMissingSDK,Version=1.0|GraphMissingSDK 1.0|");
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingOrMismatchedPlatformSDKDiscovery(bool restore)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        if (restore)
        {
            RestorePlatformSDK(project).ShouldBeTrue();
        }

        string graph = Path.Combine(directory, "build.ninja");
        Generate(project, graph, out string log, "-p:TargetPlatformVersion=2.0").ShouldBeFalse();

        log.ShouldContain(restore ? "MSB3668" : "MSB4019");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void RestoreCapturesPlatformSDKDiscoveryForEachFramework()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        string contents = File.ReadAllText(project)
            .Replace("<MSBuildPlatformSDKRestoreFile>$(MSBuildThisFileDirectory)sdk.props</MSBuildPlatformSDKRestoreFile>", "")
            .Replace("<Target Name=\"Restore\" />", """
                <Target Name="Restore">
                  <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="_GenerateProjectRestoreGraphPerFramework"
                           Properties="TargetFramework=first" />
                  <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="_GenerateProjectRestoreGraphPerFramework"
                           Properties="TargetFramework=second" />
                </Target>
                <Target Name="_GenerateProjectRestoreGraphPerFramework" />
                """);
        File.WriteAllText(project, contents);

        RestorePlatformSDK(project).ShouldBeTrue();

        Directory.GetFiles(Path.Combine(directory, "obj"), "*.platform-sdk.props", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
        Generate(project, Path.Combine(directory, "first.ninja"), "-p:TargetFramework=first").ShouldBeTrue();
        Generate(project, Path.Combine(directory, "second.ninja"), "-p:TargetFramework=second").ShouldBeTrue();
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NinjaCompilesAndRunsHelloWorldWithoutTheEngine(bool invalidSource)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "compiler $ space'quote").Path;
        string source = Path.Combine(directory, "Program.cs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestResources", "Program.cs"), source);
        if (invalidSource)
        {
            File.WriteAllText(source, "System.Console.WriteLine(DoesNotExist);");
        }

        string dotnet = RunnerUtilities.BootstrapDotnetHostPath;
        string dotnetDirectory = Path.GetDirectoryName(dotnet)!;
        string framework = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string runtimeDirectory = Directory.GetDirectories(
            Path.Combine(dotnetDirectory, "shared", "Microsoft.NETCore.App"),
            framework["net".Length..] + ".*").ShouldHaveSingleItem();
        string runtimeVersion = Path.GetFileName(runtimeDirectory);
        string compilerDirectory = Path.Combine(dotnetDirectory, "sdk", RunnerUtilities.BootstrapSdkVersion, "Roslyn", "bincore");
        string referencePack = Directory.GetDirectories(
            Path.Combine(dotnetDirectory, "packs", "Microsoft.NETCore.App.Ref"),
            framework["net".Length..] + ".*").ShouldHaveSingleItem();
        string references = Path.Combine(referencePack, "ref", framework);
        Directory.Exists(references).ShouldBeTrue();
        string project = Path.Combine(AppContext.BaseDirectory, "TestResources", "HelloWorld.proj");
        string graph = Path.Combine(directory, "build.ninja");
        string assembly = Path.Combine(directory, "HelloWorld.dll");
        string runtimeConfig = Path.Combine(directory, "HelloWorld.runtimeconfig.json");
        string arguments = $"-p:CompilerTaskAssembly=\"{typeof(GraphCSharpCompileTask).Assembly.Location}\""
            + $" -p:CompilerDirectory=\"{compilerDirectory}\""
            + $" -p:ReferenceAssemblyDirectory=\"{references}\""
            + $" -p:RuntimeDirectory=\"{runtimeDirectory}\""
            + $" -p:RuntimeVersion=\"{runtimeVersion}\""
            + $" -p:HelloWorldSource=\"{source}\""
            + $" -p:OutputDirectory=\"{directory}\"";

        Generate(project, graph, arguments).ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(assembly).ShouldBeFalse();
        File.Exists(runtimeConfig).ShouldBeFalse();

        string compilation = Ninja(graph, out bool success);
        if (invalidSource)
        {
            success.ShouldBeFalse();
            compilation.ShouldContain("CS0103");
            File.Exists(assembly).ShouldBeFalse();
            File.Exists(runtimeConfig).ShouldBeFalse();
            return;
        }

        success.ShouldBeTrue();
        string result = RunnerUtilities.RunProcessAndGetOutput(
            dotnet, $"\"{assembly}\"", out success, outputHelper: _output,
            attachProcessId: false, environmentVariables: RunnerUtilities.GetBootstrapMSBuildEnvironmentVariables());
        success.ShouldBeTrue();
        result.Trim().ShouldBe("Hello, World!");
        DateTime assemblyWrite = File.GetLastWriteTimeUtc(assembly);
        DateTime configWrite = File.GetLastWriteTimeUtc(runtimeConfig);

        string noOp = Ninja(graph, out success);
        success.ShouldBeTrue();
        noOp.ShouldContain("no work to do");
        File.GetLastWriteTimeUtc(assembly).ShouldBe(assemblyWrite);
        File.GetLastWriteTimeUtc(runtimeConfig).ShouldBe(configWrite);

        File.WriteAllText(source, """System.Console.WriteLine("Hello again!");""");
        File.SetLastWriteTimeUtc(source, assemblyWrite.AddSeconds(2));
        Ninja(graph, out success);
        success.ShouldBeTrue();
        result = RunnerUtilities.RunProcessAndGetOutput(
            dotnet, $"\"{assembly}\"", out success, outputHelper: _output,
            attachProcessId: false, environmentVariables: RunnerUtilities.GetBootstrapMSBuildEnvironmentVariables());
        success.ShouldBeTrue();
        result.Trim().ShouldBe("Hello again!");
        File.GetLastWriteTimeUtc(runtimeConfig).ShouldBe(configWrite);
    }

    [UnixOnlyTheory]
    [InlineData("windows", "WINDOWS", "True")]
    [InlineData("linux", "OsX", "False")]
    [InlineData("osx", "oSx", "True")]
    [InlineData("freebsd", "WINDOWS", "False")]
    [InlineData("LINUX", "linux", "True")]
    public void UsesCapturedHostOSDuringEvaluationAndConstruction(string hostOS, string query, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("<Target Name=\"Build\"", $$"""
            <PropertyGroup>
              <MSBuildGraphHostOS>ignored-project-value</MSBuildGraphHostOS>
              <RequestedOS>{{query}}</RequestedOS>
              <Evaluated>$([MSBuild]::IsOSPlatform('$(RequestedOS)'))</Evaluated>
            </PropertyGroup>
            <Target Name="Build"
            """);
        contents = contents.Replace("<GraphValueTask", """
            <PropertyGroup>
              <Constructed>$([MSBuild]::IsOSPlatform('$(RequestedOS)'))</Constructed>
            </PropertyGroup>
            <GraphValueTask
            """);
        contents = contents.Replace("</GraphValueTask>", """
            </GraphValueTask>
            <ItemGroup>
              <Values>
                <Tag>$(Evaluated)|$(Constructed)|$(MSBuildGraphHostOS)</Tag>
              </Values>
            </ItemGroup>
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, $"-p:MSBuildGraphHostOS={hostOS}").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.ReadAllText(graph).ShouldContain("msbuild_construction_host_os = " + hostOS.ToLowerInvariant());
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe($"literal%3B;value|{expected}|{expected}|{hostOS}");
    }

    [UnixOnlyFact]
    public void CapturedHostOSParticipatesInGraphIdentity()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        string detectedHostOS = OperatingSystem.IsMacOS() ? "osx" :
            OperatingSystem.IsFreeBSD() ? "freebsd" : "linux";
        File.ReadAllText(graph).ShouldContain("msbuild_construction_host_os = " + detectedHostOS);
        Generate(project, graph, "-p:MSBuildGraphHostOS=osx").ShouldBeTrue();
        string firstGraph = File.ReadAllText(graph);
        firstGraph.ShouldContain("msbuild_construction_host_os = osx");
        Generate(project, graph, "-p:MSBuildGraphHostOS=windows").ShouldBeTrue();

        string secondGraph = File.ReadAllText(graph);
        secondGraph.ShouldNotBe(firstGraph);
        secondGraph.ShouldContain("msbuild_construction_host_os = windows");
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("Empty")]
    [InlineData("Unknown")]
    [InlineData("TreatAsLocal")]
    [InlineData("TargetProperty")]
    [InlineData("TaskOutput")]
    [InlineData("NoArguments")]
    [InlineData("EmptyArgument")]
    [InlineData("ExtraArgument")]
    public void RejectsInvalidOrMutableHostConfiguration(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = invalid switch
        {
            "TreatAsLocal" => contents.Replace("<Project>", "<Project TreatAsLocalProperty=\"msbuildgraphhostos\">"),
            "TargetProperty" => contents.Replace("<GraphValueTask", "<PropertyGroup><MSBuildGraphHostOS>windows</MSBuildGraphHostOS></PropertyGroup><GraphValueTask"),
            "TaskOutput" => contents.Replace("ItemName=\"Values\"", "PropertyName=\"MSBuildGraphHostOS\""),
            "NoArguments" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform())</Bad></PropertyGroup><Target Name=\"Build\""),
            "EmptyArgument" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform(''))</Bad></PropertyGroup><Target Name=\"Build\""),
            "ExtraArgument" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform('osx', 'linux'))</Bad></PropertyGroup><Target Name=\"Build\""),
            _ => contents,
        };
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");
        string arguments = invalid switch
        {
            "Empty" => "-p:MSBuildGraphHostOS=",
            "Unknown" => "-p:MSBuildGraphHostOS=unknown",
            _ => "-p:MSBuildGraphHostOS=osx",
        };

        Generate(project, graph, arguments).ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void OrdinaryEvaluationStillQueriesTheActualHostOS()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string project = env.CreateFile(".proj").Path;
        File.WriteAllText(project, """
            <Project>
              <PropertyGroup>
                <ActualHostWindows>$([MSBuild]::IsOSPlatform('WINDOWS'))</ActualHostWindows>
              </PropertyGroup>
            </Project>
            """);

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" --hardened-graph:false -p:MSBuildGraphHostOS=windows -getProperty:ActualHostWindows -nr:false",
            out bool success, outputHelper: _output);

        success.ShouldBeTrue();
        output.ShouldContain("False");
    }

    [UnixOnlyTheory]
    [InlineData("$([MSBuild]::GetTargetFrameworkIdentifier('net11.0'))", ".NETCoreApp")]
    [InlineData("$([MSBuild]::GetTargetFrameworkVersion('net11.0'))", "11.0")]
    [InlineData("$([MSBuild]::IsTargetFrameworkCompatible('net11.0', 'netstandard2.0'))", "True")]
    [InlineData("$([MSBuild]::GetTargetPlatformIdentifier('net11.0-windows10.0'))", "windows")]
    [InlineData("$([MSBuild]::GetTargetPlatformVersion('net11.0-windows10.0'))", "10.0")]
    [InlineData("$([MSBuild]::FilterTargetFrameworks('net11.0;netstandard2.0', 'net11.0'))", "net11.0")]
    [InlineData("$(Parts.Split('.')[0])", "alpha")]
    [InlineData("$(Parts.Split('.')[$(ArrayIndex)])", "beta")]
    [InlineData("$(Parts.Split('.')[2].ToUpperInvariant())", "GAMMA")]
    [InlineData("$(Parts.ToCharArray()[0])", "a")]
    [InlineData("$([MSBuild]::Add(9, $([MSBuild]::Subtract($(_TargetFrameworkVersionWithoutV.Split('.')[0]), 5)))).0", "15.0")]
    public void AllowsPureExpressionsOverDeclaredValues(string expression, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = contents.Replace("<Target Name=\"Build\"",
            $"""
            <PropertyGroup>
              <Parts>alpha.beta.gamma</Parts>
              <ArrayIndex>1</ArrayIndex>
              <_TargetFrameworkVersionWithoutV>11.0</_TargetFrameworkVersionWithoutV>
              <Parsed>{expression}</Parsed>
            </PropertyGroup>
            <Target Name="Build"
            """);
        contents = contents.Replace("<GraphValueTask",
            $"<PropertyGroup><Constructed>{expression}</Constructed></PropertyGroup><GraphValueTask");
        contents = contents.Replace("</GraphValueTask>", """
            </GraphValueTask>
            <ItemGroup>
              <Values>
                <Tag>$(Parsed)|$(Constructed)</Tag>
              </Values>
            </ItemGroup>
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe($"literal%3B;value|{expected}|{expected}");
    }

    [UnixOnlyTheory]
    [InlineData("$(Parts.Split('.')[3])", "MSB4184")]
    [InlineData("$(Parts.Split('.')[-1])", "MSB4184")]
    [InlineData("$(Parts.Split('.').SetValue('changed', 0))", "MSB4287")]
    [InlineData("$(Parts.Split('.').Clone())", "MSB4287")]
    public void RejectsInvalidArrayIndicesAndUnapprovedArrayMethods(string expression, string errorCode)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("<Target Name=\"Build\"",
            $"""
            <PropertyGroup>
              <Parts>alpha.beta.gamma</Parts>
              <Parsed>{expression}</Parsed>
            </PropertyGroup>
            <Target Name="Build"
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(errorCode);
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void EmitsWithoutExecutingActionsAndNinjaBuildsIncrementally()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "graph $ space:quote'").Path;
        string project = WriteProject(directory);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        string first = Path.Combine(directory, "first.txt");
        string second = Path.Combine(directory, "second.txt");
        File.Exists(first).ShouldBeFalse();
        File.Exists(second).ShouldBeFalse();
        string[] manifests = Directory.GetFiles(graph + ".actions", "*.json");
        manifests.Length.ShouldBe(2);
        using (JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(manifests[0])))
        {
            invocation.RootElement.GetProperty("taskType").GetString().ShouldBe(typeof(GraphTransformTask).FullName);
        }
        foreach (string manifest in manifests)
        {
            using JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(manifest));
            if (invocation.RootElement.GetProperty("parameters").TryGetProperty("Text", out JsonElement text))
            {
                text[0].GetProperty("metadata").GetProperty("ConstructionDirectory").GetString().ShouldBe(directory);
            }
        }

        Ninja(graph, out bool success);
        success.ShouldBeTrue();
        File.ReadAllText(first).ShouldBe("literal%3B;value|metadata%25");
        File.ReadAllText(second).ShouldBe("literal%3B;value|metadata%25|consumed");
        DateTime firstWrite = File.GetLastWriteTimeUtc(first);
        DateTime secondWrite = File.GetLastWriteTimeUtc(second);
        DateTime manifestWrite = File.GetLastWriteTimeUtc(manifests[0]);
        DateTime graphWrite = File.GetLastWriteTimeUtc(graph);

        Generate(project, graph).ShouldBeTrue();
        File.GetLastWriteTimeUtc(graph).ShouldBe(graphWrite);
        File.GetLastWriteTimeUtc(manifests[0]).ShouldBe(manifestWrite);
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.GetLastWriteTimeUtc(first).ShouldBe(firstWrite);
        File.GetLastWriteTimeUtc(second).ShouldBe(secondWrite);

        File.WriteAllText(first, "changed");
        File.SetLastWriteTimeUtc(first, secondWrite.AddSeconds(2));
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.ReadAllText(second).ShouldBe("changed|consumed");
    }

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MessageLogsWithoutCreatingAnAction(bool critical, bool hardened)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        XElement message = new("Message",
            new XAttribute("Text", "graph-message:$(MSBuildProjectName)"),
            new XAttribute("Importance", "High"),
            new XAttribute("IsCritical", critical),
            new XAttribute("Code", "GRAPH001"),
            new XAttribute("File", "not-on-disk.txt"),
            new XAttribute("DeclaredInputs", ""),
            new XAttribute("DeclaredOutputs", ""));
        if (!hardened)
        {
            message.Attribute("DeclaredInputs")!.Remove();
            message.Attribute("DeclaredOutputs")!.Remove();
            contents.Root!.Element("Target")!.RemoveNodes();
        }

        contents.Root!.Element("Target")!.AddFirst(message);
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        if (!hardened)
        {
            string ordinaryLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"\"{project}\" -nr:false -v:minimal",
                out bool succeeded, outputHelper: _output);

            succeeded.ShouldBeTrue();
            ordinaryLog.ShouldContain("graph-message:build");
            File.Exists(graph).ShouldBeFalse();
            File.Exists(Path.Combine(directory, "not-on-disk.txt")).ShouldBeFalse();
            return;
        }

        Generate(project, graph, out string log).ShouldBeTrue();

        log.ShouldContain("graph-message:build");
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "not-on-disk.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        string actionLog = Ninja(graph, out bool success);

        success.ShouldBeTrue();
        actionLog.ShouldNotContain("graph-message:");
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    [UnixOnlyFact]
    public void CommonConfigurationMessagesAllowGraphConstruction()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        XDocument contents = XDocument.Load(project);
        contents.Root!.Element("Target")!.SetAttributeValue("DependsOnTargets", "_CheckForInvalidConfigurationAndPlatform");
        contents.Root.AddFirst(new XElement("PropertyGroup",
            new XElement("Configuration", "GraphConfiguration"),
            new XElement("Platform", "GraphPlatform")));
        contents.Root.Add(new XElement(common.Elements("Target")
            .Where(target => target.Attribute("Name")?.Value == "_CheckForInvalidConfigurationAndPlatform").ShouldHaveSingleItem()));
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log, "-v:diagnostic").ShouldBeTrue();

        log.ShouldContain("Configuration=GraphConfiguration");
        log.ShouldContain("Platform=GraphPlatform");
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("")]
    [InlineData(" ; @(Missing) ; ")]
    [InlineData("$(Missing)")]
    public void EmptyMSBuildReturnsStaticOutputsWithoutAnAction(string projects)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        contents.Root!.AddFirst(new XElement("PropertyGroup", new XElement("ReturnedProperty", "stale")),
            new XElement("ItemGroup", new XElement("ReturnedItems", new XAttribute("Include", "existing"))));
        XElement target = contents.Root.Element("Target")!;
        XElement secondAction = target.Elements("GraphTransformTask").Last();
        secondAction.AddBeforeSelf(
            new XElement("MSBuild", new XAttribute("Projects", projects), new XAttribute("Targets", "Missing"),
                new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"),
                    new XAttribute("PropertyName", "ReturnedProperty")),
                new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"),
                    new XAttribute("ItemName", "ReturnedItems"))),
            new XElement("PropertyGroup",
                new XElement("Observed", "$(ReturnedProperty)|@(ReturnedItems)|$(MSBuildLastTaskResult)")));
        secondAction.SetAttributeValue("Condition", "'$(Observed)' == '|existing|true'");
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonFrameworkNegotiationAcceptsEmptyProjectReferences(bool dynamicPlatform)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        XElement[] calls = [.. common.Descendants("MSBuild").Where(task =>
            task.Attribute("Projects")?.Value == "@(_MSBuildProjectReferenceExistent)" &&
            task.Attribute("Targets")?.Value == "GetTargetFrameworks")];
        calls.Length.ShouldBe(2);
        XDocument contents = XDocument.Load(project);
        contents.Root!.AddFirst(new XElement("PropertyGroup",
            new XElement("BuildingProject", "true"),
            new XElement("EnableDynamicPlatformResolution", dynamicPlatform)));
        contents.Root.Element("Target")!.AddFirst(calls.Select(call => new XElement(call)));
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    [UnixOnlyTheory]
    [InlineData("Projects", "missing-child.proj", "MSB3202")]
    [InlineData("Projects", "%3B", "MSB3202")]
    [InlineData("BuildInParallel", "invalid-boolean", "invalid-boolean")]
    [InlineData("UnknownParameter", "value", "UnknownParameter")]
    [InlineData("TaskParameter", "UnknownOutput", "UnknownOutput")]
    [InlineData("ContinueOnError", "true", "ContinueOnError")]
    [InlineData("MSBuildRuntime", "CurrentRuntime", "task hosting")]
    [InlineData("MissingProjects", "", "MSB4286")]
    public void UnsupportedOrInvalidMSBuildDoesNotPublishActions(string parameter, string value, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        XElement call = new("MSBuild", new XAttribute("Projects", ""), new XAttribute("Targets", "Build"));
        if (parameter == "TaskParameter")
        {
            call.Add(new XElement("Output", new XAttribute("TaskParameter", value),
                new XAttribute("ItemName", "ReturnedItems")));
        }
        else if (parameter == "MissingProjects")
        {
            call.Attribute("Projects")!.Remove();
        }
        else
        {
            call.SetAttributeValue(parameter, value);
        }

        XDocument contents = XDocument.Load(project);
        contents.Root!.Element("Target")!.Add(call);
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(expected);
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void EmptyProjectsDoNotAdmitAnUnauditedMSBuildOverride()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        contents.Root!.AddFirst(new XElement("UsingTask",
            new XAttribute("TaskName", typeof(MSBuild).FullName!),
            new XAttribute("AssemblyFile", typeof(MSBuild).Assembly.Location)));
        contents.Root.Element("Target")!.Add(new XElement("MSBuild",
            new XAttribute("Projects", ""),
            new XAttribute("DeclaredInputs", ""),
            new XAttribute("DeclaredOutputs", ""),
            new XAttribute("File", Path.Combine(directory, "unaudited.txt"))));
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("unaudited tasks");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "unaudited.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("DeclaredInputs")]
    [InlineData("DeclaredOutputs")]
    [InlineData("Importance")]
    public void InvalidMessageDoesNotPublishOrExecuteActions(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        XElement message = new("Message",
            new XAttribute("Text", "construction-message"),
            new XAttribute("Importance", "High"),
            new XAttribute("DeclaredInputs", ""),
            new XAttribute("DeclaredOutputs", ""));
        if (invalid == "Importance")
        {
            message.SetAttributeValue("Importance", "invalid-message-importance");
        }
        else
        {
            message.Attribute(invalid)!.Remove();
        }

        XDocument contents = XDocument.Load(project);
        contents.Root!.Element("Target")!.Add(message);
        File.WriteAllText(project, contents.ToString());
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(invalid == "Importance" ? "invalid-message-importance" : $"missing '{invalid}'");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonCoreBuildUsesFailFastGraphWithoutFailureActions(bool fail)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteCoreBuildProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("Text=\"@(Values)\"", $"Text=\"@(Values)\" Fail=\"{fail}\""));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBe(!fail);
        if (fail)
        {
            File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
        }
        else
        {
            File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
        }
    }

    [UnixOnlyTheory]
    [InlineData("Always", false, true)]
    [InlineData("OnOutputUpdated", false, true)]
    [InlineData("OnBuildSuccess", true, true)]
    [InlineData("Always", true, false)]
    [InlineData("OnOutputUpdated", true, false)]
    public void CommonCoreBuildRejectsFailureDependentPostBuildEvents(string runPostBuildEvent, bool hasEvent, bool accepted)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteCoreBuildProject(directory, runPostBuildEvent, hasEvent ? "echo post-build" : "");
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBe(accepted);

        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        if (!accepted)
        {
            log.ShouldContain("MSB3670");
            File.Exists(graph).ShouldBeFalse();
            Directory.Exists(graph + ".actions").ShouldBeFalse();
        }
    }

    [UnixOnlyTheory]
    [InlineData("", false)]
    [InlineData("Always", true)]
    [InlineData("OnOutputUpdated", true)]
    public void OrdinaryCoreBuildRetainsFailureHandlers(string runPostBuildEvent, bool runsPostBuild)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteCoreBuildProject(directory, runPostBuildEvent, "ordinary-event");
        XDocument contents = XDocument.Load(project);
        contents.Root!.Elements("Target").Single(target => target.Attribute("Name")!.Value == "TestCompile")
            .ReplaceNodes(new XElement("Error", new XAttribute("Text", "expected-core-build-failure")));
        foreach (string targetName in new[] { "_CleanRecordFileWrites", "_TimeStampAfterCompile", "PostBuildEvent" })
        {
            contents.Root.Add(new XElement("Target", new XAttribute("Name", targetName),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", targetName + ".txt"),
                    new XAttribute("Lines", targetName),
                    new XAttribute("Overwrite", "true"))));
        }

        File.WriteAllText(project, contents.ToString());

        string log = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -nr:false -v:minimal", out bool succeeded, outputHelper: _output);

        succeeded.ShouldBeFalse();
        log.ShouldContain("expected-core-build-failure");
        log.ShouldNotContain("MSB3670");
        File.ReadAllText(Path.Combine(directory, "_CleanRecordFileWrites.txt")).ShouldContain("_CleanRecordFileWrites");
        File.Exists(Path.Combine(directory, "_TimeStampAfterCompile.txt")).ShouldBe(runsPostBuild);
        File.Exists(Path.Combine(directory, "PostBuildEvent.txt")).ShouldBe(runsPostBuild);
    }

    [UnixOnlyTheory]
    [InlineData("false")]
    [InlineData("ErrorAndStop")]
    [InlineData("off")]
    public void AllowsExplicitFailFastContinueOnError(string continueOnError)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace(
            "Text=\"@(Values)\"", "Text=\"@(Values)\" ContinueOnError=\"$(ActionContinueOnError)\""));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, $"-p:ActionContinueOnError={continueOnError}").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    [UnixOnlyTheory]
    [InlineData("", "MSB4021")]
    [InlineData("WarnAndContinue", "MSB4286")]
    [InlineData("ErrorAndContinue", "MSB4286")]
    [InlineData("invalid", "MSB4286")]
    public void RejectsNonFailFastOrInvalidContinueOnError(string continueOnError, string diagnosticCode)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace(
            "Text=\"@(Values)\"", $"Text=\"@(Values)\" ContinueOnError=\"{continueOnError}\""));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(diagnosticCode);
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("Unknown")]
    [InlineData("OutputMapping")]
    [InlineData("MissingDeclaration")]
    [InlineData("MissingZeroDeclaration")]
    [InlineData("ConflictingOutputs")]
    [InlineData("Exists")]
    [InlineData("PropertyIO")]
    [InlineData("EvaluationIO")]
    [InlineData("ContinueOnError")]
    [InlineData("OnError")]
    [InlineData("Status")]
    public void RejectsUnsupportedGraphsWithoutPublishingOrExecutingActions(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = invalid switch
        {
            "Unknown" => contents.Replace("</Target>", "<Exec Command=\"touch forbidden.txt\" /></Target>"),
            "OutputMapping" => contents.Replace("DeclaredOutputs=\"first.txt\" />", "DeclaredOutputs=\"first.txt\"><Output TaskParameter=\"Text\" PropertyName=\"Bad\" /></GraphTransformTask>"),
            "MissingDeclaration" => contents.Replace("DeclaredOutputs=\"first.txt\"", ""),
            "MissingZeroDeclaration" => contents.Replace("<GraphValueTask DeclaredInputs=\"\" DeclaredOutputs=\"\">", "<GraphValueTask DeclaredInputs=\"\">"),
            "ConflictingOutputs" => contents.Replace("DeclaredOutputs=\"second.txt\"", "DeclaredOutputs=\"first.txt\""),
            "Exists" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"Exists('first.txt')\""),
            "PropertyIO" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"$([System.IO.File]::Exists('first.txt'))\""),
            "EvaluationIO" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([System.IO.File]::WriteAllText('$(MSBuildProjectDirectory)/forbidden.txt', 'bad'))</Bad></PropertyGroup><Target Name=\"Build\""),
            "ContinueOnError" => contents.Replace("Text=\"@(Values)\"", "Text=\"@(Values)\" ContinueOnError=\"true\""),
            "OnError" => contents.Replace("</Target>", "<OnError ExecuteTargets=\"Failure\" /></Target><Target Name=\"Failure\" />"),
            "Status" => contents.Replace("Source=\"first.txt\"", "Source=\"first.txt\" Condition=\"'$(MSBuildLastTaskResult)' == 'true'\""),
            _ => throw new InvalidOperationException(invalid),
        };
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("--hardened-graph")]
    [InlineData("--hardened-graph -p:MSBuildIsRestoring=true")]
    [InlineData("--hardened-graph -restore")]
    public void HardenedModeRequiresGraphEmissionWithoutExecutingTasks(string arguments)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", """
            <Target Name="Restore">
              <WriteLinesToFile File="forbidden.txt" Lines="restored" Overwrite="true" />
            </Target>
            </Project>
            """));

        string log = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" {arguments} -nr:false -v:minimal",
            out bool success, outputHelper: _output);

        success.ShouldBeFalse();
        log.ShouldContain("MSB4286");
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void RejectsCombinedRestoreAndConstructionWithGraphConfiguration()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", """
            <Target Name="Restore">
              <WriteLinesToFile File="forbidden.txt" Lines="restored" Overwrite="true" />
            </Target>
            </Project>
            """));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-restore").ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void MigratedZeroIOTaskUsesProjectContextAndSingleNode()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("Text=\"@(Values)\"", "Text=\"@(Assigned)\"");
        contents = contents.Replace("<GraphTransformTask File=\"first.txt\"", """
            <ItemGroup>
              <Input Include="folder/file.txt" />
            </ItemGroup>
            <AssignTargetPathWithProjectDirectory
                Files="@(Input)" RootFolder="$(MSBuildProjectDirectory)"
                DeclaredInputs="" DeclaredOutputs="">
              <Output TaskParameter="AssignedFiles" ItemName="Assigned" />
            </AssignTargetPathWithProjectDirectory>
            <ItemGroup>
              <Assigned>
                <Tag>%(Assigned.TargetPath)|$(MSBuildNodeCount)</Tag>
              </Assigned>
            </ItemGroup>
            <GraphTransformTask File="first.txt"
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-m:2").ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();

        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe("folder/file.txt|folder/file.txt|1");
    }

    [UnixOnlyTheory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("ErrorAndStop")]
    public void FailedActionBlocksItsConsumer(string continueOnError)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string control = continueOnError.Length == 0 ? "" : $" ContinueOnError=\"{continueOnError}\"";
        File.WriteAllText(project, File.ReadAllText(project).Replace("Text=\"@(Values)\"", $"Text=\"@(Values)\" Fail=\"true\"{control}"));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool success);

        success.ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void CapturesEachTaskBucketAndInvalidatesOnManifestChanges()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project)
            .Replace("<Target Name=\"Build\"", "<ItemGroup><Batch Include=\"one;two\" /></ItemGroup><Target Name=\"Build\"")
            .Replace("File=\"first.txt\" Text=\"@(Values)\" DeclaredInputs=\"\" DeclaredOutputs=\"first.txt\"",
                "File=\"%(Batch.Identity).txt\" Text=\"@(Values)\" DeclaredInputs=\"\" DeclaredOutputs=\"%(Batch.Identity).txt\"")
            .Replace("<GraphTransformTask File=\"second.txt\" Source=\"first.txt\" DeclaredInputs=\"first.txt\" DeclaredOutputs=\"second.txt\" />", "");
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        Ninja(graph, out bool success);
        success.ShouldBeTrue();
        string first = Path.Combine(directory, "one.txt");
        string second = Path.Combine(directory, "two.txt");
        File.ReadAllText(first).ShouldBe("literal%3B;value|metadata%25");
        File.ReadAllText(second).ShouldBe("literal%3B;value|metadata%25");

        File.WriteAllText(project, contents.Replace("Text=\"@(Values)\"", "Text=\"replacement\""));
        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.ReadAllText(first).ShouldBe("replacement|");
        File.ReadAllText(second).ShouldBe("replacement|");
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructsActionsThroughTargetDependenciesAndCallTarget(bool callTarget)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        string producer = """
            <GraphValueTask DeclaredInputs="" DeclaredOutputs="">
              <Output TaskParameter="Values" ItemName="Values" />
            </GraphValueTask>
            <GraphTransformTask File="first.txt" Text="@(Values)" DeclaredInputs="" DeclaredOutputs="first.txt" />
            """;
        int start = contents.IndexOf("<GraphValueTask ", StringComparison.Ordinal);
        int end = contents.IndexOf("<GraphTransformTask File=\"second.txt\"", StringComparison.Ordinal);
        contents = contents.Remove(start, end - start);
        contents = contents.Replace("</Project>", $"<Target Name=\"Produce\">{producer}</Target></Project>");
        contents = callTarget
            ? contents.Replace("<GraphTransformTask File=\"second.txt\"", "<CallTarget Targets=\"Produce\" /><GraphTransformTask File=\"second.txt\"")
            : contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" DependsOnTargets=\"Produce\"");
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    private static string WritePlatformSDKProject(string directory, string sdkRoot = "")
    {
        string project = Path.Combine(directory, "build.proj");
        File.WriteAllText(project, $$"""
            <Project DefaultTargets="Build">
              <UsingTask TaskName="GraphTransformTask" AssemblyFile="{{SecurityElement.Escape(typeof(GraphTransformTask).Assembly.Location)}}" />
              <PropertyGroup>
                <TargetPlatformIdentifier>GraphMissingSDK</TargetPlatformIdentifier>
                <TargetPlatformVersion>1.0</TargetPlatformVersion>
                <TargetPlatformSdkRootOverride>{{SecurityElement.Escape(sdkRoot)}}</TargetPlatformSdkRootOverride>
                <MSBuildPlatformSDKRestoreFile>$(MSBuildThisFileDirectory)sdk.props</MSBuildPlatformSDKRestoreFile>
              </PropertyGroup>
              <Import Project="$(MSBuildToolsPath)/Microsoft.Common.PlatformSDK.targets" />
              <Target Name="Restore" />
              <Target Name="Build">
                <GraphTransformTask File="result.txt"
                                    Text="$(TargetPlatformSdkMetadataLocation)|$(TargetPlatformWinMDLocation)|$(TargetPlatformMoniker)|$(TargetPlatformDisplayName)"
                                    DeclaredInputs="" DeclaredOutputs="result.txt" />
              </Target>
            </Project>
            """);
        return project;
    }

    private bool RestorePlatformSDK(string project, string restoreProperty = "MSBuildRestoreGraphInputs")
    {
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -t:Restore -p:{restoreProperty}=true -nr:false -v:minimal",
            out bool succeeded, outputHelper: _output);
        return succeeded;
    }

    private static string WriteReferenceSearchProject(string directory, bool sdkProfile, bool configExists, string? searchPaths = null)
    {
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        XElement props = XDocument.Load(Path.Combine(sdk, "Current", "Microsoft.Common.props")).Root!;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        XElement root = contents.Root!;
        XElement build = root.Elements("Target").ShouldHaveSingleItem();
        root.SetAttributeValue("DefaultTargets", "Build");
        build.SetAttributeValue("DependsOnTargets", "$(ResolveAssemblyReferencesDependsOn)");
        build.Element("GraphValueTask")!.AddAfterSelf(new XElement("ItemGroup",
            new XElement("Values", new XElement("Tag", "$(AssemblySearchPaths)|$(AssemblyFoldersConfigFileSearchPath)"))));
        XElement inputs = new("PropertyGroup", new XElement("AssemblySearchPaths", searchPaths ?? ""));
        if (configExists)
        {
            string config = Path.Combine(directory, "AssemblyFolders.config");
            File.WriteAllText(config, "<installation-specific-assembly-search />");
            inputs.Add(new XElement("AssemblyFoldersConfigFile", config));
        }

        root.AddFirst(inputs,
            new XElement(props.Elements("PropertyGroup").Elements("AssemblySearchPath_UseGAC").ShouldHaveSingleItem().Parent!));
        if (sdkProfile)
        {
            XElement sdkProps = XDocument.Load(Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "targets", "Microsoft.NET.Sdk.props")).Root!;
            XNamespace ns = sdkProps.Name.Namespace;
            XElement defaults = new(sdkProps.Elements(ns + "PropertyGroup")
                .Where(group => group.Elements(ns + "AssemblySearchPaths").Any()).ShouldHaveSingleItem());
            foreach (XElement element in defaults.DescendantsAndSelf())
            {
                element.Name = element.Name.LocalName;
            }

            root.Add(defaults);
        }

        root.Add(
            new XElement(common.Elements("PropertyGroup").Elements("AssemblyFoldersConfigFile").ShouldHaveSingleItem().Parent!),
            new XElement(common.Elements("PropertyGroup").Where(group => group.Elements("AssemblySearchPaths").Any()).ShouldHaveSingleItem()),
            new XElement(common.Elements("PropertyGroup")
                .Where(group => group.Element("ResolveAssemblyReferencesDependsOn") is not null && group.Attribute("Condition") is not null)
                .ShouldHaveSingleItem()),
            new XElement(common.Elements("Target")
                .Where(target => target.Attribute("Name")?.Value == "_ValidateHardenedAssemblySearchPaths").ShouldHaveSingleItem()));
        File.WriteAllText(project, contents.ToString());
        return project;
    }

    private static string WriteDirectoryPreparationProject(string directory, bool callTarget = false, bool withDirectories = true)
    {
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Parse(File.ReadAllText(project)
            .Replace("first.txt", "obj/nested/first.txt")
            .Replace("second.txt", "bin/app/second.txt"));
        XElement root = contents.Root!;
        root.AddFirst(new XElement("PropertyGroup",
            new XElement("OutDir", withDirectories ? "bin/" : ""),
            new XElement("IntermediateOutputPath", withDirectories ? "obj/" : ""),
            new XElement("InteropOutputPath", withDirectories ? "obj/" : "")));
        if (withDirectories)
        {
            root.AddFirst(new XElement("ItemGroup",
                new XElement("DocFileItem", new XAttribute("Include", "obj/nested/document.xml")),
                new XElement("EmbeddedResource", new XAttribute("Include", "resource"),
                    new XElement("Culture", "nested")),
                new XElement("CreateDirectory", new XAttribute("Include", "obj/nested/"))));
        }

        XElement prepare = new("Target", new XAttribute("Name", "PrepareDirectories"));
        foreach (XElement makeDir in common.Descendants("MakeDir")
            .Where(element => element.Parent!.Attribute("Condition")?.Value != "'$(MSBuildHardenedGraph)' != 'true'"))
        {
            XElement requirement = makeDir.ElementsAfterSelf().First();
            requirement.Element("Output")!.Attribute("ItemName")!.Value.ShouldBe("_MSBuildRequiredOutputDirectory");
            prepare.Add(new XElement(makeDir), new XElement(requirement));
        }

        XElement build = root.Element("Target")!;
        if (callTarget)
        {
            build.AddFirst(new XElement("CallTarget", new XAttribute("Targets", "PrepareDirectories")));
        }
        else
        {
            build.SetAttributeValue("DependsOnTargets", "PrepareDirectories");
        }

        root.Add(prepare);
        File.WriteAllText(project, contents.ToString());
        return project;
    }

    private static string WriteCoreBuildProject(string directory, string runPostBuildEvent = "", string postBuildEvent = "")
    {
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        XElement root = contents.Root!;
        root.SetAttributeValue("DefaultTargets", "Build");
        root.Element("Target")!.SetAttributeValue("Name", "TestCompile");
        root.AddFirst(new XElement("PropertyGroup",
            new XElement("CoreBuildDependsOn", "TestCompile"),
            new XElement("RunPostBuildEvent", runPostBuildEvent),
            new XElement("PostBuildEvent", postBuildEvent)));
        foreach (string targetName in new[] { "CoreBuild", "_ValidateHardenedBuildFailureHandling" })
        {
            root.Add(new XElement(common.Elements("Target")
                .Where(target => target.Attribute("Name")?.Value == targetName).ShouldHaveSingleItem()));
        }

        root.Add(new XElement("Target", new XAttribute("Name", "Build"), new XAttribute("DependsOnTargets", "CoreBuild")));
        File.WriteAllText(project, contents.ToString());
        return project;
    }

    private static string WriteProject(string directory)
    {
        string assembly = SecurityElement.Escape(typeof(GraphValueTask).Assembly.Location)!;
        string project = Path.Combine(directory, "build.proj");
        File.WriteAllText(project, $$"""
            <Project>
              <UsingTask TaskName="GraphValueTask" AssemblyFile="{{assembly}}" />
              <UsingTask TaskName="GraphTransformTask" AssemblyFile="{{assembly}}" />
              <Target Name="Build" Inputs="first.txt" Outputs="second.txt">
                <GraphValueTask DeclaredInputs="" DeclaredOutputs="">
                  <Output TaskParameter="Values" ItemName="Values" />
                </GraphValueTask>
                <GraphTransformTask File="first.txt" Text="@(Values)" DeclaredInputs="" DeclaredOutputs="first.txt" />
                <GraphTransformTask File="second.txt" Source="first.txt" DeclaredInputs="first.txt" DeclaredOutputs="second.txt" />
              </Target>
            </Project>
            """);
        return project;
    }

    private bool Generate(string project, string graph, string arguments = "")
        => Generate(project, graph, out _, arguments);

    private bool Generate(string project, string graph, out string log, string arguments = "")
    {
        string testDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string framework = Path.GetFileName(testDirectory);
        string configuration = Path.GetFileName(Path.GetDirectoryName(testDirectory))!;
        string host = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..",
            "MSBuild.ActionTaskHost", configuration, framework, "MSBuild.ActionTaskHost.dll"));
        string dotnet = RunnerUtilities.BootstrapDotnetHostPath;
        log = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" --hardened-graph -nr:false -v:minimal {arguments}"
                + $" -p:MSBuildEmitActionGraph=\"{graph}\" -p:MSBuildActionTaskHost=\"{host}\""
                + $" -p:MSBuildActionDotNetHost=\"{dotnet}\"",
            out bool succeeded, outputHelper: _output);
        return succeeded;
    }

    private string Ninja(string graph, out bool success)
        => RunnerUtilities.RunProcessAndGetOutput("ninja", $"-f \"{graph}\" -j1", out success, outputHelper: _output);
}

public sealed class MSBuild : Utilities.Task
{
    public ITaskItem[] Projects { get; set; } = [];
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public string File { get; set; } = "";

    public override bool Execute()
    {
        System.IO.File.WriteAllText(File, "unaudited task executed");
        return true;
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphParameterTask : Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public string? NullText { get; set; }
    public string DefaultText { get; set; } = "constructor-default";

    [Required]
    public string File { get; set; } = "";

    public override bool Execute()
    {
        System.IO.File.WriteAllText(TaskEnvironment.GetAbsolutePath(File).Value, $"{NullText ?? "<null>"}|{DefaultText}");
        return true;
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphCSharpCompileTask : Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    [Required]
    public ITaskItem[] References { get; set; } = [];

    [Required]
    public string CompilerAssembly { get; set; } = "";

    [Required]
    public string DotNetHost { get; set; } = "";

    [Required]
    public string OutputAssembly { get; set; } = "";

    public override bool Execute()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "Microsoft.Build")
            {
                throw new InvalidOperationException("Compilation actions must run without the engine.");
            }
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> inputs = new(comparer);
        foreach (ITaskItem input in DeclaredInputs)
        {
            inputs.Add(Absolute(input.ItemSpec));
        }

        RequireInput(CompilerAssembly);
        RequireInput(DotNetHost);
        foreach (ITaskItem source in Sources)
        {
            RequireInput(source.ItemSpec);
        }

        foreach (ITaskItem reference in References)
        {
            RequireInput(reference.ItemSpec);
        }

        if (Sources.Length == 0 || References.Length == 0 ||
            DeclaredOutputs.Length != 1 || !comparer.Equals(Absolute(DeclaredOutputs[0].ItemSpec), Absolute(OutputAssembly)))
        {
            throw new InvalidOperationException("The compiler fixture requires sources, references, and its exact declared assembly output.");
        }

        ProcessStartInfo start = TaskEnvironment.GetProcessStartInfo();
        start.FileName = Absolute(DotNetHost);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.ArgumentList.Add(Absolute(CompilerAssembly));
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-noconfig");
        start.ArgumentList.Add("-nostdlib+");
        start.ArgumentList.Add("-target:exe");
        start.ArgumentList.Add("-deterministic+");
        start.ArgumentList.Add("-debug-");
        start.ArgumentList.Add("-out:" + Absolute(OutputAssembly));
        foreach (ITaskItem reference in References)
        {
            start.ArgumentList.Add("-reference:" + Absolute(reference.ItemSpec));
        }

        foreach (ITaskItem source in Sources)
        {
            start.ArgumentList.Add(Absolute(source.ItemSpec));
        }

        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The compiler process did not start.");
        }

        System.Threading.Tasks.Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Log.LogMessage(MessageImportance.High, "{0}", stdout.GetAwaiter().GetResult());
        Log.LogMessage(MessageImportance.High, "{0}", stderr.GetAwaiter().GetResult());
        return process.ExitCode == 0;

        string Absolute(string path) => TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm().Value;

        void RequireInput(string path)
        {
            if (!inputs.Contains(Absolute(path)))
            {
                throw new InvalidOperationException($"Compiler input '{path}' was not declared.");
            }
        }
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphValueTask : Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[] Values { get; private set; } = [];

    public override bool Execute()
    {
        Utilities.TaskItem value = new("literal%253B%3Bvalue", treatAsFilePath: false);
        ((ITaskItem2)value).SetMetadataValueLiteral("Tag", "metadata%25");
        ((ITaskItem2)value).SetMetadataValueLiteral("ConstructionDirectory", TaskEnvironment.ProjectDirectory);
        Values = [value];
        return true;
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphTransformTask : Utilities.Task
{
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public ITaskItem[] Text { get; set; } = [];
    public string Source { get; set; } = "";
    public bool Fail { get; set; }

    [Required]
    public string File { get; set; } = "";

    public override bool Execute()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "Microsoft.Build")
            {
                throw new InvalidOperationException("File actions must run without the engine.");
            }
        }

        if (Fail)
        {
            return false;
        }

        string text = Source.Length == 0
            ? Text[0].ItemSpec + "|" + Text[0].GetMetadata("Tag")
            : System.IO.File.ReadAllText(Source) + "|consumed";
        System.IO.File.WriteAllText(File, text);
        return true;
    }
}

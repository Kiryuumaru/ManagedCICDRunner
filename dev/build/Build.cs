using System;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using NukeBuildHelpers;
using NukeBuildHelpers.Common.Enums;
using NukeBuildHelpers.Entry;
using NukeBuildHelpers.Entry.Extensions;
using NukeBuildHelpers.Entry.Models;
using NukeBuildHelpers.RunContext.Interfaces;
using NukeBuildHelpers.Runner.Abstraction;
using NukeBuildHelpers.RunContext.Extensions;
using NukeBuildHelpers.Pipelines.Common.Enums;
using NukeBuildHelpers.Pipelines.Github.Models;

class Build : BaseNukeBuildHelpers
{
    public static int Main() => Execute<Build>(x => x.Version);

    public override string[] EnvironmentBranches { get; } = ["master", "prerelease"];

    public override string MainEnvironmentBranch { get; } = "master";

    private static readonly string[] osMatrix = ["linux", "windows"];
    private static readonly string[] archMatrix = ["x64", "arm64"];

    string GetVersion(IRunContext context)
    {
        string version = "0.0.0";
        if (context.TryGetVersionedContext(out var versionedContext))
        {
            version = versionedContext.AppVersion.Version.ToString();
        }
        return version;
    }

    AbsolutePath GetOutAsset(IRunContext context, string os, string arch)
    {
        string name = $"ManagedCICDRunner_{os.ToLowerInvariant()}_{arch.ToLowerInvariant()}";
        var fileName = name + "-" + GetVersion(context);
        if (os == "linux")
        {
            return OutputDirectory / (fileName + ".tar.gz");
        }
        else if (os == "windows")
        {
            return OutputDirectory / (fileName + ".zip");
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    BuildEntry BuildEdgeGridBinaries => _ => _
        .AppId("managed-cicd-runner")
        .Matrix(osMatrix, (definitionOs, os) =>
        {
            var osPascal = Regex.Replace(os, @"\b\p{Ll}", match => match.Value.ToUpper());
            definitionOs.RunnerOS(os switch
            {
                "linux" => RunnerOS.Ubuntu2204,
                "windows" => RunnerOS.Windows2022,
                _ => throw new NotSupportedException()
            });
            definitionOs.Matrix(archMatrix, (definitionArch, arch) =>
            {
                string runtime = $"{os.ToLowerInvariant()}-{arch.ToLowerInvariant()}";
                definitionArch.WorkflowId($"build_{os}_{arch}");
                definitionArch.DisplayName($"[Build] {osPascal}{arch.ToUpperInvariant()}");
                definitionArch.ReleaseAsset(context => [GetOutAsset(context, os, arch)]);
                definitionArch.Execute(context =>
                {
                    var outAsset = GetOutAsset(context, os, arch);
                    var archivePath = outAsset.Parent / outAsset.NameWithoutExtension;
                    var outPath = archivePath / outAsset.NameWithoutExtension;
                    var proj = RootDirectory / "src" / "Presentation" / "Presentation.csproj";
                    DotNetTasks.DotNetBuild(_ => _
                        .SetProjectFile(proj)
                        .SetConfiguration("Release"));
                    DotNetTasks.DotNetPublish(_ => _
                        .SetProject(proj)
                        .SetConfiguration("Release")
                        .EnableSelfContained()
                        .SetRuntime(runtime switch
                        {
                            "linux-x64" => "linux-x64",
                            "windows-x64" => "win-x64",
                            "linux-arm64" => "linux-arm64",
                            "windows-arm64" => "win-arm64",
                            _ => throw new NotImplementedException()
                        })
                        .EnablePublishSingleFile()
                        .SetOutput(outPath));
                    if (os == "linux")
                    {
                        archivePath.TarGZipTo(outAsset);
                    }
                    else if (os == "windows")
                    {
                        archivePath.ZipTo(outAsset);
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                });
            });
        });
}

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
using System.Collections.Generic;
using NukeBuildHelpers.Common;

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

    AbsolutePath GetOutAsset(string os, string arch)
    {
        string name = $"ManagedCICDRunner_{os.ToLowerInvariant()}_{arch.ToLowerInvariant()}";
        if (os == "linux")
        {
            return OutputDirectory / (name + ".tar.gz");
        }
        else if (os == "windows")
        {
            return OutputDirectory / (name + ".zip");
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    AbsolutePath[] GetAssets(string os, string arch)
    {
        List<AbsolutePath> assets = [];

        assets.Add(GetOutAsset(os, arch));

        if (os == "linux")
        {
        }
        else if (os == "windows")
        {
            assets.Add(OutputDirectory / $"installer_{os}_{arch}.ps1");
            assets.Add(OutputDirectory / $"uninstaller_{os}_{arch}.ps1");
        }
        else
        {
            throw new NotSupportedException();
        }

        return [.. assets];
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
                definitionArch.ReleaseAsset(context => GetAssets(os, arch));
                definitionArch.Execute(context =>
                {
                    var outAsset = GetOutAsset(os, arch);
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
                            "linux-arm64" => "linux-arm64",
                            "windows-x64" => "win-x64",
                            "windows-arm64" => "win-arm64",
                            _ => throw new NotImplementedException()
                        })
                        .EnablePublishSingleFile()
                        .SetOutput(outPath));
                    (RootDirectory / "dockers").CopyRecursively(outPath / "dockers");
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

                    if (context.TryGetVersionedContext(out var versioned))
                    {
                        (OutputDirectory / $"installer_{os}_{arch}.ps1").WriteAllText((RootDirectory / "installerTemplate.ps1").ReadAllText()
                            .Replace("{{$tag}}", $"build.{versioned.AppVersion.BuildId}")
                            .Replace("{{$repo}}", "Kiryuumaru/ManagedCICDRunner")
                            .Replace("{{$appname}}", $"ManagedCICDRunner_{os}_{arch}")
                            .Replace("{{$appexec}}", "Presentation.exe")
                            .Replace("{{$rootextract}}", $"ManagedCICDRunner_{os}_{arch}"));

                        (OutputDirectory / $"uninstaller_{os}_{arch}.ps1").WriteAllText((RootDirectory / "uninstallerTemplate.ps1").ReadAllText()
                            .Replace("{{$tag}}", $"build.{versioned.AppVersion.BuildId}")
                            .Replace("{{$repo}}", "Kiryuumaru/ManagedCICDRunner")
                            .Replace("{{$appname}}", $"ManagedCICDRunner_{os}_{arch}")
                            .Replace("{{$appexec}}", "Presentation.exe")
                            .Replace("{{$rootextract}}", $"ManagedCICDRunner_{os}_{arch}"));
                    }
                });
            });
        });
}

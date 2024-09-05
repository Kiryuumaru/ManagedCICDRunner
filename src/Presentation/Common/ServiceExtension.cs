using AbsolutePathHelpers;
using Application;
using Application.Common;
using CliWrap.EventStream;
using CommandLine;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact.Reader;
using Serilog.Parsing;
using System.Globalization;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Presentation.Common;

internal static class ServiceExtension
{
    public static async Task InstallAsService(CancellationToken cancellationToken)
    {
        Log.Information("Installing service...");

        await PrepareSvc(cancellationToken);

        var winswExecPath = AbsolutePath.Create(Environment.CurrentDirectory) / "winsw.exe";
        var serviceConfig = AbsolutePath.Create(Environment.CurrentDirectory) / "svc.xml";

        try
        {
            await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);
            await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cancellationToken);
        }
        catch { }
        await Cli.RunOnce($"{winswExecPath} install {serviceConfig}", stoppingToken: cancellationToken);
        await Cli.RunOnce($"{winswExecPath} start {serviceConfig}", stoppingToken: cancellationToken);

        Log.Information("Installing service done");
    }

    public static async Task UninstallAsService(CancellationToken cancellationToken)
    {
        Log.Information("Uninstalling service...");

        await PrepareSvc(cancellationToken);

        var winswExecPath = AbsolutePath.Create(Environment.CurrentDirectory) / "winsw.exe";
        var serviceConfig = AbsolutePath.Create(Environment.CurrentDirectory) / "svc.xml";

        try
        {
            await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);
        }
        catch { }
        await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cancellationToken);

        Log.Information("Uninstalling service done");
    }

    private static async Task PrepareSvc(CancellationToken cancellationToken)
    {
        var winswExecPath = AbsolutePath.Create(Environment.CurrentDirectory) / "winsw.exe";
        if (!File.Exists(winswExecPath))
        {
            string folderName;
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                folderName = "winsw_windows_x64";
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                folderName = "winsw_windows_arm64";
            }
            else
            {
                throw new NotSupportedException();
            }
            string dlUrl = $"https://github.com/Kiryuumaru/winsw-modded/releases/download/build.1/{folderName}.zip";
            var downloadsPath = AbsolutePath.Create(Environment.CurrentDirectory) / "downloads";
            var winswZipPath = downloadsPath / "winsw.zip";
            var winswZipExtractPath = downloadsPath / "winsw";
            var winswDownloadedExecPath = winswZipExtractPath / folderName / "winsw.exe";
            try
            {
                await winswZipPath.Delete(cancellationToken);
            }
            catch { }
            try
            {
                await winswZipExtractPath.Delete(cancellationToken);
            }
            catch { }
            downloadsPath.CreateDirectory();
            winswZipExtractPath.CreateDirectory();
            {
                using var client = new HttpClient();
                using var s = await client.GetStreamAsync(dlUrl, cancellationToken: cancellationToken);
                using var fs = new FileStream(winswZipPath, FileMode.OpenOrCreate);
                await s.CopyToAsync(fs, cancellationToken: cancellationToken);
            }
            await winswZipPath.UnZipTo(winswZipExtractPath, cancellationToken);
            await winswDownloadedExecPath.CopyTo(winswExecPath, cancellationToken);
        }

        var config = """
            <service>
              <id>managed-cicd-runner</id>
              <name>Managed CICD runner</name>
              <description>This service is a manager for CICD runner</description>
              <executable>%BASE%\ManagedCICDRunner.exe</executable>
              <arguments>run -s</arguments>
              <log mode="none"></log>
              <startmode>Automatic</startmode>
              <onfailure action="restart" delay="2 sec"/>
              <env name="ASPNETCORE_URLS" value="http://*:23457" />
            </service>
            """;

        var serviceConfig = AbsolutePath.Create(Environment.CurrentDirectory) / "svc.xml";
        await File.WriteAllTextAsync(serviceConfig, config, cancellationToken);
    }
}

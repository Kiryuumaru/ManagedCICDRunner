using ApplicationBuilderHelpers;
using CliWrap.EventStream;
using Infrastructure.SQLite;
using Presentation;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Application.Common;

if (args.Any(i => i.Equals("--install-service", StringComparison.InvariantCultureIgnoreCase)))
{
    await installAsService();
}
else if (args.Any(i => i.Equals("--uninstall-service", StringComparison.InvariantCultureIgnoreCase)))
{
    await uninstallAsService();
}
else
{
    ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args))
        .Add<BasePresentation>()
        .Add<SQLiteApplication>()
        .Run();
}

async Task installAsService()
{
    await prepareSvc();

    var currentDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
    var winswExecPath = currentDir.Trim('\\') + "\\winsw.exe";
    var serviceConfig = currentDir.Trim('\\') + "\\svc.xml";

    await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force");
    await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}");
    await Cli.RunOnce($"{winswExecPath} install {serviceConfig}");
    await Cli.RunOnce($"{winswExecPath} start {serviceConfig}");
}

async Task uninstallAsService()
{
    await prepareSvc();

    var currentDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
    var winswExecPath = currentDir.Trim('\\') + "\\winsw.exe";
    var serviceConfig = currentDir.Trim('\\') + "\\svc.xml";

    await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force");
    await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}");
}

async Task prepareSvc()
{
    await downloadWinsw();
    var config = """
        <service>
          <id>managed-cicd-runner</id>
          <name>Managed CICD runner</name>
          <description>This service is a manager for CICD runner</description>
          <executable>%BASE%\Presentation.exe</executable>
          <log mode="roll"></log>
          <startmode>Automatic</startmode>
          <onfailure action="restart" delay="2 sec"/>
          <outfilepattern>.output.log</outfilepattern>
          <errfilepattern>.error.log</errfilepattern>
          <combinedfilepattern>.combined.log</combinedfilepattern>
        </service>
        """;
    var currentDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
    var serviceConfig = currentDir.Trim('\\') + "\\svc.xml";
    File.WriteAllText(serviceConfig, config);
}

async Task downloadWinsw()
{
    var currentDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
    var winswExecPath = currentDir.Trim('\\') + "\\winsw.exe";
    if (File.Exists(winswExecPath))
    {
        return;
    }
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
    var downloadsPath = currentDir.Trim('\\') + "\\downloads";
    var winswZipPath = downloadsPath + "\\winsw.zip";
    var winswZipExtractPath = downloadsPath + "\\winsw";
    var winswDownloadedExecPath = winswZipExtractPath + $"\\{folderName}\\winsw.exe";
    try
    {
        Directory.Delete(winswZipPath, true);
    }
    catch { }
    try
    {
        Directory.Delete(winswZipExtractPath, true);
    }
    catch { }
    try
    {
        File.Delete(winswZipPath);
    }
    catch { }
    try
    {
        File.Delete(winswZipExtractPath);
    }
    catch { }
    Directory.CreateDirectory(downloadsPath);
    Directory.CreateDirectory(winswZipExtractPath);
    {
        using var client = new HttpClient();
        using var s = await client.GetStreamAsync(dlUrl);
        using var fs = new FileStream(winswZipPath, FileMode.OpenOrCreate);
        await s.CopyToAsync(fs);
    }
    ZipFile.ExtractToDirectory(winswZipPath, winswZipExtractPath);
    File.Copy(winswDownloadedExecPath, winswExecPath);
}
using ApplicationBuilderHelpers;
using CliWrap.EventStream;
using Infrastructure.SQLite;
using Presentation;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Application.Common;

CancellationTokenSource cts = new();
Console.CancelKeyPress += (s, e) =>
{
    cts.Cancel();
};

if (args.Any(i => i.Equals("--install-service", StringComparison.InvariantCultureIgnoreCase)))
{
    await installAsService();
}
else if (args.Any(i => i.Equals("--uninstall-service", StringComparison.InvariantCultureIgnoreCase)))
{
    await uninstallAsService();
}
else if (args.Any(i => i.Equals("--logs-service", StringComparison.InvariantCultureIgnoreCase)))
{
    await foreach (var commandEvent in Cli.RunListen("powershell", ["Get-Content", "-Path", "svc.combined.log", "-Wait"], stoppingToken: cts.Token))
    {
        switch (commandEvent)
        {
            case StandardOutputCommandEvent outEvent:
                Console.WriteLine(outEvent.Text);
                break;
            case StandardErrorCommandEvent errEvent:
                Console.WriteLine(errEvent.Text);
                break;
        }
    }
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

    var winswExecPath = Environment.CurrentDirectory.Trim('\\') + "\\winsw.exe";
    var serviceConfig = Environment.CurrentDirectory.Trim('\\') + "\\svc.xml";

    try
    {
        await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cts.Token);
        await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cts.Token);
    }
    catch { }
    await Cli.RunOnce($"{winswExecPath} install {serviceConfig}", stoppingToken: cts.Token);
    await Cli.RunOnce($"{winswExecPath} start {serviceConfig}", stoppingToken: cts.Token);
}

async Task uninstallAsService()
{
    await prepareSvc();

    var winswExecPath = Environment.CurrentDirectory.Trim('\\') + "\\winsw.exe";
    var serviceConfig = Environment.CurrentDirectory.Trim('\\') + "\\svc.xml";

    try
    {
        await Cli.RunOnce($"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cts.Token);
    }
    catch { }
    await Cli.RunOnce($"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cts.Token);
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
          <env name="ASPNETCORE_URLS" value="http://*:5000" />
        </service>
        """;
    var serviceConfig = Environment.CurrentDirectory.Trim('\\') + "\\svc.xml";
    File.WriteAllText(serviceConfig, config);
}

async Task downloadWinsw()
{
    var winswExecPath = Environment.CurrentDirectory.Trim('\\') + "\\winsw.exe";
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
    var downloadsPath = Environment.CurrentDirectory.Trim('\\') + "\\downloads";
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
        using var s = await client.GetStreamAsync(dlUrl, cancellationToken: cts.Token);
        using var fs = new FileStream(winswZipPath, FileMode.OpenOrCreate);
        await s.CopyToAsync(fs, cancellationToken: cts.Token);
    }
    ZipFile.ExtractToDirectory(winswZipPath, winswZipExtractPath);
    File.Copy(winswDownloadedExecPath, winswExecPath);
}

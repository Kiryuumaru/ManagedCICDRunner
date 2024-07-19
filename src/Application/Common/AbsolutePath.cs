using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common;

public class AbsolutePath
{
    public static AbsolutePath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty", nameof(path));
        }

        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new ArgumentException("Path must be an absolute path", nameof(path));
        }

        return new AbsolutePath() { Path = path };
    }

    public static AbsolutePath operator /(AbsolutePath b, string c)
    {
        return new AbsolutePath() { Path = System.IO.Path.Combine(b.Path, c) };
    }

    public static implicit operator AbsolutePath(string path) => new() { Path = path };

    public static implicit operator string(AbsolutePath path) => path.Path;

    private readonly SHA256 Sha256 = SHA256.Create();

    public required string Path { get; init; }

    [JsonIgnore]
    public AbsolutePath Parent
    {
        get
        {
            return Parse(Directory.GetParent(Path)!.ToString());
        }
    }

    [JsonIgnore]
    public string Stem
    {
        get
        {
            return System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }

    public FileInfo? ToFileInfo()
    {
        return Path is not null ? new FileInfo(Path) : null;
    }

    public DirectoryInfo? ToDirectoryInfo()
    {
        return Path is not null ? new DirectoryInfo(Path) : null;
    }

    public bool IsExists()
    {
        return Directory.Exists(Path) || File.Exists(Path);
    }

    public bool FileExists()
    {
        return File.Exists(Path);
    }

    public bool DirectoryExists()
    {
        return Directory.Exists(Path);
    }

    public bool ContainsFile(string pattern, SearchOption options = SearchOption.TopDirectoryOnly)
    {
        return ToDirectoryInfo()?.GetFiles(pattern, options).Length != 0;
    }

    public bool ContainsDirectory(string pattern, SearchOption options = SearchOption.TopDirectoryOnly)
    {
        return ToDirectoryInfo()?.GetDirectories(pattern, options).Length != 0;
    }

    public string GetHashSha256()
    {
        using FileStream stream = File.OpenRead(Path);
        var bytes = Sha256.ComputeHash(stream);
        string result = "";
        foreach (byte b in bytes) result += b.ToString("x2");
        return result;
    }

    public override string ToString()
    {
        return Path;
    }

    public IEnumerable<AbsolutePath> GetFiles(
        string pattern = "*",
        int depth = 1,
        FileAttributes attributes = 0)
    {
        if (depth == 0)
            return [];

        var files = Directory.EnumerateFiles(Path, pattern, SearchOption.TopDirectoryOnly)
            .Where(x => (File.GetAttributes(x) & attributes) == attributes)
            .OrderBy(x => x)
            .Select(Parse);

        return files.Concat(GetDirectories(depth: depth - 1).SelectMany(x => x.GetFiles(pattern, attributes: attributes)));
    }

    public IEnumerable<AbsolutePath> GetDirectories(
        string pattern = "*",
        int depth = 1,
        FileAttributes attributes = 0)
    {
        var paths = new string[] { Path };
        while (paths.Length != 0 && depth > 0)
        {
            var matchingDirectories = paths
                .SelectMany(x => Directory.EnumerateDirectories(x, pattern, SearchOption.TopDirectoryOnly))
                .Where(x => (File.GetAttributes(x) & attributes) == attributes)
                .OrderBy(x => x)
                .Select(Parse).ToList();

            foreach (var matchingDirectory in matchingDirectories)
                yield return matchingDirectory;

            depth--;
            paths = paths.SelectMany(x => Directory.GetDirectories(x, "*", SearchOption.TopDirectoryOnly)).ToArray();
        }
    }

    public IEnumerable<AbsolutePath> GetPaths()
    {
        var paths = new List<AbsolutePath>();
        paths.AddRange(GetFiles());
        paths.AddRange(GetDirectories());
        return paths;
    }

    public Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(Path, cancellationToken);
    }

    public async Task<T?> ReadObjAsync<T>(JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () => JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(Path, cancellationToken), jsonSerializerOptions));
    }

    public Task WriteAllTextAsync(string content, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(Path, content, cancellationToken);
    }

    public async Task WriteObjAsync<T>(T obj, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => File.WriteAllTextAsync(Path, JsonSerializer.Serialize(obj, jsonSerializerOptions), cancellationToken), cancellationToken);
    }

    public async Task LockFile(CancellationToken unlockToken)
    {
        Directory.CreateDirectory(Parent);
        var fileLock = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await fileLock.WriteAsync(Array.Empty<byte>(), unlockToken);
        await fileLock.FlushAsync(unlockToken);
        async void watchLock()
        {
            await unlockToken.WhenCanceled();
            fileLock.Close();
        }
        watchLock();
    }

    public async Task ClaimFile(CancellationToken unclaimToken)
    {
        Directory.CreateDirectory(Parent);
        Guid guid = Guid.NewGuid();
        string guidStr = guid.ToString();
        bool hasClaimed = false;
        try
        {
            await File.WriteAllTextAsync(Path, guidStr, unclaimToken);
            await Task.Delay(1000, unclaimToken);
            var currentGuidStr = await File.ReadAllTextAsync(Path, unclaimToken);
            if (currentGuidStr == guidStr)
            {
                hasClaimed = true;
            }
        }
        catch { }
        if (hasClaimed)
        {
            async void watchClaim()
            {
                while (!unclaimToken.IsCancellationRequested)
                {
                    try
                    {
                        await File.WriteAllTextAsync(Path, guidStr, unclaimToken);
                    }
                    catch { }
                    finally
                    {
                        try
                        {
                            await Task.Delay(250, unclaimToken);
                        }
                        catch { }
                    }
                }
            }
            watchClaim();
        }
        else
        {
            throw new Exception($"{Path} was already claimed.");
        }
    }

    public void CreateDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void CreateOrCleanDirectory()
    {
        DeleteDirectory();
        CreateDirectory();
    }

    public void TouchFile(DateTime? time = null, bool createDirectories = true)
    {
        if (createDirectories)
            Parent.CreateDirectory();

        if (!File.Exists(Path))
            File.WriteAllBytes(Path, []);

        File.SetLastWriteTime(Path, time ?? DateTime.Now);
    }


    public void DeleteFile()
    {
        if (!FileExists())
            return;

        File.SetAttributes(Path, FileAttributes.Normal);
        File.Delete(Path);
    }

    public void DeleteDirectory()
    {
        if (!DirectoryExists())
            return;

        foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Path, recursive: true);
    }
}

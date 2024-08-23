using System.Text.Json;

namespace Application.Common;

public static partial class AbsolutePathExtensions
{
    /// <summary>
    /// Provides extension methods for the <see cref="AbsolutePath"/> class.
    /// </summary>
    public static FileInfo? ToFileInfo(this AbsolutePath absolutePath)
    {
        return absolutePath.Path is not null ? new FileInfo(absolutePath.Path) : null;
    }

    /// <summary>
    /// Converts the <see cref="AbsolutePath"/> to a <see cref="FileInfo"/> object.
    /// </summary>
    /// <param name="absolutePath">The absolute path to convert.</param>
    /// <returns>A <see cref="FileInfo"/> object representing the file, or null if the path is null.</returns>
    public static DirectoryInfo? ToDirectoryInfo(this AbsolutePath absolutePath)
    {
        return absolutePath.Path is not null ? new DirectoryInfo(absolutePath.Path) : null;
    }

    /// <summary>
    /// Checks if the path specified by the <see cref="AbsolutePath"/> exists as either a file or directory.
    /// </summary>
    /// <param name="absolutePath">The absolute path to check.</param>
    /// <returns>True if the path exists, otherwise false.</returns>
    public static bool IsExists(this AbsolutePath absolutePath)
    {
        return Directory.Exists(absolutePath.Path) || File.Exists(absolutePath.Path);
    }

    /// <summary>
    /// Checks if the file specified by the <see cref="AbsolutePath"/> exists.
    /// </summary>
    /// <param name="absolutePath">The absolute path to check.</param>
    /// <returns>True if the file exists, otherwise false.</returns>
    public static bool FileExists(this AbsolutePath absolutePath)
    {
        return File.Exists(absolutePath.Path);
    }

    /// <summary>
    /// Checks if the directory specified by the <see cref="AbsolutePath"/> exists.
    /// </summary>
    /// <param name="absolutePath">The absolute path to check.</param>
    /// <returns>True if the directory exists, otherwise false.</returns>
    public static bool DirectoryExists(this AbsolutePath absolutePath)
    {
        return Directory.Exists(absolutePath.Path);
    }

    /// <summary>
    /// Checks if the directory specified by the <see cref="AbsolutePath"/> contains any files matching the specified pattern.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the directory.</param>
    /// <param name="pattern">The search string to match against the names of files.</param>
    /// <param name="options">Specifies whether the search operation should include only the current directory or all subdirectories.</param>
    /// <returns>True if the directory contains files matching the pattern, otherwise false.</returns>
    public static bool ContainsFile(this AbsolutePath absolutePath, string pattern, SearchOption options = SearchOption.TopDirectoryOnly)
    {
        return ToDirectoryInfo(absolutePath)?.GetFiles(pattern, options).Length != 0;
    }

    /// <summary>
    /// Checks if the directory specified by the <see cref="AbsolutePath"/> contains any directories matching the specified pattern.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the directory.</param>
    /// <param name="pattern">The search string to match against the names of directories.</param>
    /// <param name="options">Specifies whether the search operation should include only the current directory or all subdirectories.</param>
    /// <returns>True if the directory contains directories matching the pattern, otherwise false.</returns>
    public static bool ContainsDirectory(this AbsolutePath absolutePath, string pattern, SearchOption options = SearchOption.TopDirectoryOnly)
    {
        return ToDirectoryInfo(absolutePath)?.GetDirectories(pattern, options).Length != 0;
    }

    /// <summary>
    /// Retrieves the files within the directory specified by the <see cref="AbsolutePath"/> that match the specified pattern.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the directory.</param>
    /// <param name="pattern">The search string to match against the names of files. Default is "*".</param>
    /// <param name="depth">The depth to search. Default is 1.</param>
    /// <param name="attributes">The file attributes to match against. Default is 0 (no specific attributes).</param>
    /// <returns>An enumerable collection of <see cref="AbsolutePath"/> objects representing the files.</returns>
    public static IEnumerable<AbsolutePath> GetFiles(
        this AbsolutePath absolutePath,
        string pattern = "*",
        int depth = 1,
        FileAttributes attributes = 0)
    {
        if (!DirectoryExists(absolutePath)) return [];

        if (depth == 0)
            return [];

        var files = Directory.EnumerateFiles(absolutePath.Path, pattern, SearchOption.TopDirectoryOnly)
            .Where(x => (File.GetAttributes(x) & attributes) == attributes)
            .OrderBy(x => x)
            .Select(AbsolutePath.Create);

        return files.Concat(GetDirectories(absolutePath, depth: depth - 1).SelectMany(x => x.GetFiles(pattern, attributes: attributes)));
    }

    /// <summary>
    /// Retrieves the directories within the directory specified by the <see cref="AbsolutePath"/> that match the specified pattern.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the directory.</param>
    /// <param name="pattern">The search string to match against the names of directories. Default is "*".</param>
    /// <param name="depth">The depth to search. Default is 1.</param>
    /// <param name="attributes">The directory attributes to match against. Default is 0 (no specific attributes).</param>
    /// <returns>An enumerable collection of <see cref="AbsolutePath"/> objects representing the directories.</returns>
    public static IEnumerable<AbsolutePath> GetDirectories(
        this AbsolutePath absolutePath,
        string pattern = "*",
        int depth = 1,
        FileAttributes attributes = 0)
    {
        if (DirectoryExists(absolutePath))
        {
            var paths = new string[] { absolutePath.Path };
            while (paths.Length != 0 && depth > 0)
            {
                var matchingDirectories = paths
                    .SelectMany(x => Directory.EnumerateDirectories(x, pattern, SearchOption.TopDirectoryOnly))
                    .Where(x => (File.GetAttributes(x) & attributes) == attributes)
                    .OrderBy(x => x)
                    .Select(AbsolutePath.Create).ToList();

                foreach (var matchingDirectory in matchingDirectories)
                    yield return matchingDirectory;

                depth--;
                paths = paths.SelectMany(x => Directory.GetDirectories(x, "*", SearchOption.TopDirectoryOnly)).ToArray();
            }
        }
    }

    /// <summary>
    /// Retrieves all files and directories within the directory specified by the <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the directory.</param>
    /// <returns>An enumerable collection of <see cref="AbsolutePath"/> objects representing the files and directories.</returns>
    public static IEnumerable<AbsolutePath> GetPaths(this AbsolutePath absolutePath)
    {
        var paths = new List<AbsolutePath>();
        paths.AddRange(GetFiles(absolutePath));
        paths.AddRange(GetDirectories(absolutePath));
        return paths;
    }

    /// <summary>
    /// Reads all the text from the file specified by the <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the read operation.</param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains the text from the file.</returns>
    public static Task<string> ReadAllTextAsync(this AbsolutePath absolutePath, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(absolutePath.Path, cancellationToken);
    }

    /// <summary>
    /// Reads and deserializes a JSON file at the specified <see cref="AbsolutePath"/> into an object of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of the object to deserialize to.</typeparam>
    /// <param name="absolutePath">The absolute path of the file.</param>
    /// <param name="jsonSerializerOptions">Options to control the behavior during deserialization.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the read operation.</param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains the deserialized object.</returns>
    public static async Task<T?> ReadObjAsync<T>(this AbsolutePath absolutePath, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () => JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(absolutePath.Path, cancellationToken), jsonSerializerOptions));
    }

    /// <summary>
    /// Determines whether the current path is a parent of the specified child path.
    /// </summary>
    /// <param name="parent">The parent path.</param>
    /// <param name="child">The child path.</param>
    /// <returns>True if the current path is a parent of the specified child path, otherwise false.</returns>
    public static bool IsParentOf(this AbsolutePath parent, AbsolutePath child)
    {
        var pathToCheck = child.Parent;

        while (pathToCheck != null)
        {
            if (pathToCheck == parent)
            {
                return true;
            }

            pathToCheck = pathToCheck.Parent;
        }

        return false;
    }

    /// <summary>
    /// Determines whether the current path is a parent or the same as the specified child path.
    /// </summary>
    /// <param name="parent">The parent path.</param>
    /// <param name="child">The child path.</param>
    /// <returns>True if the current path is a parent or the same as the specified child path, otherwise false.</returns>
    public static bool IsParentOrSelfOf(this AbsolutePath parent, AbsolutePath child)
    {
        if (parent == child)
        {
            return true;
        }

        return IsParentOf(parent, child);
    }

    /// <summary>
    /// Determines whether the current path is a child of the specified parent path.
    /// </summary>
    /// <param name="child">The child path.</param>
    /// <param name="parent">The parent path.</param>
    /// <returns>True if the current path is a child of the specified parent path, otherwise false.</returns>
    public static bool IsChildOf(this AbsolutePath child, AbsolutePath parent)
    {
        return IsParentOf(parent, child);
    }

    /// <summary>
    /// Determines whether the current path is a child or the same as the specified parent path.
    /// </summary>
    /// <param name="child">The child path.</param>
    /// <param name="parent">The parent path.</param>
    /// <returns>True if the current path is a child or the same as the specified parent path, otherwise false.</returns>
    public static bool IsChildOrSelfOf(this AbsolutePath child, AbsolutePath parent)
    {
        return IsParentOrSelfOf(parent, child);
    }

    private static FileMap GetFileMap(AbsolutePath path)
    {
        List<AbsolutePath> files = [];
        List<AbsolutePath> folders = [];
        List<(AbsolutePath Link, AbsolutePath Target)> symbolicLinks = [];

        List<AbsolutePath> next = [path];

        while (next.Count != 0)
        {
            List<AbsolutePath> forNext = [];

            foreach (var item in next)
            {
                bool hasNext = false;

                if (item.FileExists())
                {
                    FileInfo fileInfo = new(item);
                    if (fileInfo.LinkTarget != null)
                    {
                        string linkTarget;
                        if (!Path.IsPathRooted(fileInfo.LinkTarget))
                        {
                            linkTarget = item.Parent / fileInfo.LinkTarget;
                        }
                        else
                        {
                            linkTarget = fileInfo.LinkTarget;
                        }
                        if (linkTarget.StartsWith("\\??\\"))
                        {
                            linkTarget = linkTarget[4..];
                        }
                        symbolicLinks.Add((item, linkTarget));
                    }
                    else
                    {
                        files.Add(item);
                    }
                }
                else if (item.DirectoryExists())
                {
                    DirectoryInfo directoryInfo = new(item);
                    if (directoryInfo.LinkTarget != null)
                    {
                        string linkTarget;
                        if (!Path.IsPathRooted(directoryInfo.LinkTarget))
                        {
                            linkTarget = item.Parent / directoryInfo.LinkTarget;
                        }
                        else
                        {
                            linkTarget = directoryInfo.LinkTarget;
                        }
                        if (linkTarget.StartsWith("\\??\\"))
                        {
                            linkTarget = linkTarget[4..];
                        }
                        symbolicLinks.Add((item, linkTarget));
                    }
                    else
                    {
                        folders.Add(item);

                        hasNext = true;
                    }
                }

                if (hasNext)
                {
                    try
                    {
                        forNext.AddRange(Directory.GetFiles(item, "*", SearchOption.TopDirectoryOnly).Select(AbsolutePath.Create));
                    }
                    catch { }
                    try
                    {
                        forNext.AddRange(Directory.GetDirectories(item, "*", SearchOption.TopDirectoryOnly).Select(AbsolutePath.Create));
                    }
                    catch { }
                }
            }

            next = forNext;
        }

        List<(AbsolutePath Link, AbsolutePath Target)> arangedSymbolicLinks = [];
        foreach (var symbolicLink in symbolicLinks)
        {
            bool add = true;
            foreach (var arangedSymbolicLink in new List<(AbsolutePath Link, AbsolutePath Target)>(arangedSymbolicLinks))
            {
                if (symbolicLink.Target == arangedSymbolicLink.Link)
                {
                    arangedSymbolicLinks.Insert(arangedSymbolicLinks.IndexOf(arangedSymbolicLink) + 1, symbolicLink);
                    add = false;
                    break;
                }
                else if (symbolicLink.Link == arangedSymbolicLink.Target)
                {
                    arangedSymbolicLinks.Insert(arangedSymbolicLinks.IndexOf(arangedSymbolicLink), symbolicLink);
                    add = false;
                    break;
                }
            }
            if (add)
            {
                arangedSymbolicLinks.Add(symbolicLink);
            }
        }

        return new()
        {
            Source = path,
            Files = [.. files],
            Folders = [.. folders],
            SymbolicLinks = [.. arangedSymbolicLinks]
        };
    }
}

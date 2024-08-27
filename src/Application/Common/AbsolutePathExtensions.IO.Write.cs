using System.Text.Json;

namespace Application.Common;

public static partial class AbsolutePathExtensions
{
    /// <summary>
    /// Asynchronously writes the specified text to the file at the given <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path to the file.</param>
    /// <param name="content">The content to write to the file.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the write operation.</param>
    public static async Task WriteAllTextAsync(this AbsolutePath absolutePath, string content, CancellationToken cancellationToken = default)
    {
        if (!absolutePath.Parent.DirectoryExists())
        {
            await absolutePath.Parent.CreateDirectory();
        }
        await File.WriteAllTextAsync(absolutePath.Path, content, cancellationToken);
    }

    /// <summary>
    /// Asynchronously writes the specified object as JSON to the file at the given <see cref="AbsolutePath"/>.
    /// </summary>
    /// <typeparam name="T">The type of the object to write.</typeparam>
    /// <param name="absolutePath">The absolute path to the file.</param>
    /// <param name="obj">The object to write as JSON.</param>
    /// <param name="jsonSerializerOptions">Options to control the behavior during serialization.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the write operation.</param>
    public static async Task WriteObjAsync<T>(this AbsolutePath absolutePath, T obj, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        if (!absolutePath.Parent.DirectoryExists())
        {
            await absolutePath.Parent.CreateDirectory();
        }
        await Task.Run(() => File.WriteAllTextAsync(absolutePath.Path, JsonSerializer.Serialize(obj, jsonSerializerOptions), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Creates a directory at the specified <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path to the directory.</param>
    /// <returns>A task representing the asynchronous directory creation operation.</returns>
    public static Task CreateDirectory(this AbsolutePath absolutePath)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(absolutePath.Path);
        });
    }

    /// <summary>
    /// Creates a directory at the specified <see cref="AbsolutePath"/>, or cleans the existing directory if it already exists.
    /// </summary>
    /// <param name="absolutePath">The absolute path to the directory.</param>
    /// <returns>A task representing the asynchronous directory creation or cleaning operation.</returns>
    public static async Task CreateOrCleanDirectory(this AbsolutePath absolutePath)
    {
        await Delete(absolutePath);
        await CreateDirectory(absolutePath);
    }

    /// <summary>
    /// Creates a file at the specified <see cref="AbsolutePath"/> or updates its last write time.
    /// </summary>
    /// <param name="absolutePath">The absolute path to the file.</param>
    /// <param name="time">The time to set as the last write time. If null, the current time is used.</param>
    /// <param name="createDirectories">Whether to create parent directories if they do not exist.</param>
    public static void TouchFile(this AbsolutePath absolutePath, DateTime? time = null, bool createDirectories = true)
    {
        if (createDirectories)
        {
            absolutePath.Parent.CreateDirectory();
        }

        if (!File.Exists(absolutePath.Path))
        {
            File.WriteAllBytes(absolutePath.Path, []);
        }

        File.SetLastWriteTime(absolutePath.Path, time ?? DateTime.Now);
    }

    /// <summary>
    /// Recursively copies all files and directories from the specified path to the target path.
    /// </summary>
    /// <param name="path">The source path.</param>
    /// <param name="targetPath">The target path.</param>
    /// <returns>A task representing the asynchronous copy operation.</returns>
    public static async Task<bool> Copy(this AbsolutePath path, AbsolutePath targetPath)
    {
        if (path.FileExists())
        {
            Directory.CreateDirectory(targetPath.Parent);
            File.Copy(path.ToString(), targetPath.ToString(), true);

            return true;
        }
        else if (path.DirectoryExists())
        {
            var fileMap = GetFileMap(path);

            Directory.CreateDirectory(targetPath);
            foreach (var folder in fileMap.Folders)
            {
                AbsolutePath target = folder.ToString().Replace(path, targetPath);
                Directory.CreateDirectory(target);
            }
            foreach (var file in fileMap.Files)
            {
                AbsolutePath target = file.ToString().Replace(path, targetPath);
                Directory.CreateDirectory(target.Parent);
                File.Copy(file, target, true);
            }
            foreach (var (Link, Target) in fileMap.SymbolicLinks)
            {
                AbsolutePath newLink = Link.ToString().Replace(path, targetPath);
                string newTarget;
                if (path.IsParentOf(Target))
                {
                    newTarget = Target.ToString().Replace(path, targetPath);
                }
                else
                {
                    newTarget = Target;
                }

                await newLink.Delete();
                Directory.CreateDirectory(newLink.Parent);

                if (Target.DirectoryExists() || Link.DirectoryExists())
                {
                    Directory.CreateSymbolicLink(newLink, newTarget);
                }
                else
                {
                    File.CreateSymbolicLink(newLink, newTarget);
                }
            }

            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Recursively moves all files and directories from the specified path to the target path.
    /// </summary>
    /// <param name="path">The source path.</param>
    /// <param name="targetPath">The target path.</param>
    /// <returns>A task representing the asynchronous move operation.</returns>
    public static async Task<bool> Move(this AbsolutePath path, AbsolutePath targetPath)
    {
        if (path.FileExists())
        {
            Directory.CreateDirectory(targetPath.Parent);
            File.Move(path.ToString(), targetPath.ToString(), true);

            return true;
        }
        else if (path.DirectoryExists())
        {
            var fileMap = GetFileMap(path);

            Directory.CreateDirectory(targetPath);
            foreach (var folder in fileMap.Folders)
            {
                AbsolutePath target = folder.ToString().Replace(path, targetPath);
                Directory.CreateDirectory(target);
            }
            foreach (var file in fileMap.Files)
            {
                AbsolutePath target = file.ToString().Replace(path, targetPath);
                Directory.CreateDirectory(target.Parent);
                File.Move(file, target, true);
            }
            foreach (var (Link, Target) in fileMap.SymbolicLinks)
            {
                AbsolutePath newLink = Link.ToString().Replace(path, targetPath);
                string newTarget;
                if (path.IsParentOf(Target))
                {
                    newTarget = Target.ToString().Replace(path, targetPath);
                }
                else
                {
                    newTarget = Target;
                }

                await newLink.Delete();
                Directory.CreateDirectory(newLink.Parent);

                if (Target.DirectoryExists() || Link.DirectoryExists())
                {
                    Directory.CreateSymbolicLink(newLink, newTarget);
                }
                else
                {
                    File.CreateSymbolicLink(newLink, newTarget);
                }
            }

            await path.Delete();

            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Recursively deletes all files and directories from the specified path.
    /// </summary>
    /// <param name="path">The source path to delete.</param>
    /// <returns>A task representing the asynchronous move operation.</returns>
    public static Task<bool> Delete(this AbsolutePath path)
    {
        if (path.FileExists())
        {
            File.Delete(path);

            return Task.FromResult(true);
        }
        else if (path.DirectoryExists())
        {
            var fileMap = GetFileMap(path);

            foreach (var (Link, Target) in fileMap.SymbolicLinks)
            {
                if (Link.DirectoryExists())
                {
                    Directory.Delete(Link);
                }
                else
                {
                    File.Delete(Link);
                }
            }

            Directory.Delete(path, true);

            return Task.FromResult(true);
        }
        else
        {
            return Task.FromResult(false);
        }
    }
}

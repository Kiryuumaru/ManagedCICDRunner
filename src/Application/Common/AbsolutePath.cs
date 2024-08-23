using System.Text.Json.Serialization;

namespace Application.Common;

/// <summary>
/// Represents an absolute file or directory path.
/// </summary>
public class AbsolutePath
{
    /// <summary>
    /// Creates a new instance of <see cref="AbsolutePath"/> from the specified path string.
    /// </summary>
    /// <param name="path">The path to use for the <see cref="AbsolutePath"/>.</param>
    /// <returns>A new instance of <see cref="AbsolutePath"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is null, empty, or not an absolute path.</exception>
    public static AbsolutePath Create(string path)
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

    /// <summary>
    /// Combines the current <see cref="AbsolutePath"/> with a relative path.
    /// </summary>
    /// <param name="b">The base <see cref="AbsolutePath"/>.</param>
    /// <param name="c">The relative path to combine with.</param>
    /// <returns>A new <see cref="AbsolutePath"/> representing the combined path.</returns>
    public static AbsolutePath operator /(AbsolutePath b, string c)
    {
        return new AbsolutePath() { Path = System.IO.Path.Combine(b.Path, c) };
    }

    /// <summary>
    /// Implicitly converts a string to an <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="path">The string path to convert.</param>
    public static implicit operator AbsolutePath(string path) => new() { Path = path };

    /// <summary>
    /// Implicitly converts an <see cref="AbsolutePath"/> to a string.
    /// </summary>
    /// <param name="path">The <see cref="AbsolutePath"/> to convert.</param>
    public static implicit operator string(AbsolutePath path) => path.Path;

    /// <summary>
    /// Implicitly converts an <see cref="AbsolutePath"/> to a <see cref="FileInfo"/>.
    /// </summary>
    /// <param name="path">The <see cref="AbsolutePath"/> to convert.</param>
    public static implicit operator FileInfo?(AbsolutePath path) => path.ToFileInfo();

    /// <summary>
    /// Implicitly converts an <see cref="AbsolutePath"/> to a <see cref="DirectoryInfo"/>.
    /// </summary>
    /// <param name="path">The <see cref="AbsolutePath"/> to convert.</param>
    public static implicit operator DirectoryInfo?(AbsolutePath path) => path.ToDirectoryInfo();

    /// <summary>
    /// Gets or sets the absolute path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the parent directory of the current path.
    /// </summary>
    [JsonIgnore]
    public AbsolutePath Parent
    {
        get
        {
            return Create(Directory.GetParent(Path)!.ToString());
        }
    }

    /// <summary>
    /// Gets the file name without extension of the current path.
    /// </summary>
    [JsonIgnore]
    public string Stem
    {
        get
        {
            return System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }

    /// <summary>
    /// Gets the file name of the current path.
    /// </summary>
    [JsonIgnore]
    public string Name
    {
        get
        {
            return System.IO.Path.GetFileName(Path);
        }
    }

    /// <summary>
    /// Gets the file extension of the current path.
    /// </summary>
    [JsonIgnore]
    public string Extension
    {
        get
        {
            return System.IO.Path.GetExtension(Path);
        }
    }

    /// <summary>
    /// Returns the string representation of the absolute path.
    /// </summary>
    /// <returns>The absolute path as a string.</returns>
    public override string ToString()
    {
        return Path;
    }
}

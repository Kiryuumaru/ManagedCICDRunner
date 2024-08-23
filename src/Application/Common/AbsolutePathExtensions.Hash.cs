using System.Security.Cryptography;

namespace Application.Common;

public static partial class AbsolutePathExtensions
{
    /// <summary>
    /// Computes the hash of the file at the specified <see cref="AbsolutePath"/> using the provided <see cref="HashAlgorithm"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file to hash.</param>
    /// <param name="hashAlgorithm">The hash algorithm to use.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the computed hash as a hexadecimal string.</returns>
    public static async Task<string> GetHash(this AbsolutePath absolutePath, HashAlgorithm hashAlgorithm)
    {
        using var stream = File.OpenRead(absolutePath.Path);
        byte[] hashBytes = await hashAlgorithm.ComputeHashAsync(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Computes the MD5 hash of the file at the specified <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file to hash.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the MD5 hash as a hexadecimal string.</returns>
    public static Task<string> GetHashMD5(this AbsolutePath absolutePath)
    {
        return GetHash(absolutePath, MD5.Create());
    }

    /// <summary>
    /// Computes the SHA1 hash of the file at the specified <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file to hash.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the SHA1 hash as a hexadecimal string.</returns>
    public static Task<string> GetHashSHA1(this AbsolutePath absolutePath)
    {
        return GetHash(absolutePath, SHA1.Create());
    }

    /// <summary>
    /// Computes the SHA256 hash of the file at the specified <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file to hash.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the SHA256 hash as a hexadecimal string.</returns>
    public static Task<string> GetHashSHA256(this AbsolutePath absolutePath)
    {
        return GetHash(absolutePath, SHA256.Create());
    }

    /// <summary>
    /// Computes the SHA512 hash of the file at the specified <see cref="AbsolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">The absolute path of the file to hash.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the SHA512 hash as a hexadecimal string.</returns>
    public static Task<string> GetHashSHA512(this AbsolutePath absolutePath)
    {
        return GetHash(absolutePath, SHA512.Create());
    }
}

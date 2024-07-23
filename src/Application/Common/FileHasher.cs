using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.IO;

namespace Application.Common;

internal class FileHasher
{
    public static string GetFileHash(AbsolutePath filePath)
    {
        using var sha256 = SHA256.Create();
        using var fileStream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(fileStream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public static bool CompareHashes(AbsolutePath a, AbsolutePath b)
    {
        return string.Equals(GetFileHash(a), GetFileHash(b), StringComparison.OrdinalIgnoreCase);
    }
}

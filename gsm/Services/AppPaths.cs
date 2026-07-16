using System;
using System.IO;

namespace gsm.Services;

public static class AppPaths
{
    public static string RuntimeDirectory { get; } = AppContext.BaseDirectory;

    public static string ForRuntimeFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));

        return Path.Combine(RuntimeDirectory, fileName);
    }

    public static string ResolveRuntimeOrAncestorFile(
        string fileName,
        int maxAncestorDepth = 4,
        string? runtimeDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("A plain file name is required.", nameof(fileName));

        string start = Path.GetFullPath(runtimeDirectory ?? RuntimeDirectory);
        var directory = new DirectoryInfo(start);
        for (int depth = 0; directory != null && depth <= Math.Max(0, maxAncestorDepth); depth++)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(start, fileName);
    }

    public static string ForResolvedFileSibling(
        string primaryFileName,
        string siblingFileName,
        int maxAncestorDepth = 4,
        string? runtimeDirectory = null)
    {
        string primary = ResolveRuntimeOrAncestorFile(
            primaryFileName, maxAncestorDepth, runtimeDirectory);
        return Path.Combine(Path.GetDirectoryName(primary)!, siblingFileName);
    }
}

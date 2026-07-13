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
}

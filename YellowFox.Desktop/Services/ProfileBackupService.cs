using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace YellowFox.Desktop.Services;

public sealed record ProfileBackupResult(string BackupPath, long SourceBytes, int FileCount);

/// <summary>
/// Creates a rollback archive of a stopped profile before its regenerable data
/// is cleaned. The HTTP cache (<c>cache2</c>) is the only thing left out: it is
/// the bulk of a profile and is always safe to drop, while everything else
/// (identity plus the smaller caches) is preserved in the archive.
/// </summary>
public static class ProfileBackupService
{
    private static readonly string[] SkippedDirectories = { "cache2" };
    private static readonly string[] SkippedFiles = { "parent.lock", "lock", ".parentlock" };

    public static ProfileBackupResult CreateBackup(string profileDirectory, string backupZipPath)
    {
        if (!Directory.Exists(profileDirectory))
            return new ProfileBackupResult(backupZipPath, 0, 0);

        var rootFull = Path.GetFullPath(profileDirectory);
        var skippedRoots = SkippedDirectories
            .Select(name => Path.GetFullPath(Path.Combine(profileDirectory, name)))
            .ToArray();

        var backupDirectory = Path.GetDirectoryName(backupZipPath);
        if (!string.IsNullOrWhiteSpace(backupDirectory))
            Directory.CreateDirectory(backupDirectory);
        if (File.Exists(backupZipPath))
            File.Delete(backupZipPath);

        long sourceBytes = 0;
        var fileCount = 0;

        using var archive = ZipFile.Open(backupZipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(profileDirectory, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (IsUnder(full, skippedRoots))
                continue;
            if (SkippedFiles.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                continue;

            var entryName = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
            try
            {
                archive.CreateEntryFromFile(full, entryName, CompressionLevel.Fastest);
                sourceBytes += new FileInfo(full).Length;
                fileCount++;
            }
            catch
            {
                // A file can disappear or stay locked while the profile settles.
            }
        }

        return new ProfileBackupResult(backupZipPath, sourceBytes, fileCount);
    }

    private static bool IsUnder(string filePath, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.Equals(filePath, root, StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

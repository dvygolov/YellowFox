using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace YellowFox.Desktop.Services;

public sealed record ProfileCleanupResult(long BytesFreed, int ItemsRemoved, IReadOnlyList<string> RemovedItems)
{
    public bool FreedAnything => ItemsRemoved > 0;
}

public sealed record ProfileCleanupBatchResult(
    int ProfilesCleaned,
    int ProfilesSkipped,
    long BytesFreed,
    long BackupBytes,
    string? BackupDirectory,
    IReadOnlyList<string> Errors);

/// <summary>
/// Strips regenerable browser data from a stopped profile so only per-profile
/// identity/state stays on disk: cookies, logins, <c>storage</c>, prefs,
/// bookmarks, extensions and the YellowFox passkey store. Everything removed
/// here is recreated by Firefox on the next launch.
/// </summary>
public static class ProfileCleanupService
{
    // Directories that only hold caches, crash reports or on-demand downloads.
    private static readonly string[] DisposableDirectories =
    {
        "cache2",
        "startupCache",
        "shader-cache",
        "thumbnails",
        "crashes",
        "minidumps",
        "safebrowsing",
        "security_state",
        "datareporting",
        "gmp-widevinecdm",
        "OfflineCache",
        "jumpListCache"
    };

    // Individual files that are rebuilt on demand (favicons, Safe Browsing
    // lists, HSTS/Alt-Svc state, stale locks, debug logs).
    private static readonly string[] DisposableFiles =
    {
        "favicons.sqlite",
        "favicons.sqlite-wal",
        "favicons.sqlite-shm",
        "bounce-tracking-protection.sqlite",
        "bounce-tracking-protection.sqlite-journal",
        "domain_to_categories.sqlite",
        "domain_to_categories.sqlite-journal",
        "activity-stream.contile.json",
        "activity-stream.discovery_stream.json",
        ".startup-incomplete",
        "SiteSecurityServiceState.bin",
        "AlternateServices.bin",
        "chrome_debug.log",
        "parent.lock",
        "lock",
        ".parentlock"
    };

    public static ProfileCleanupResult Clean(string profileDirectory)
    {
        var removed = new List<string>();
        long freed = 0;

        if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
            return new ProfileCleanupResult(0, 0, removed);

        foreach (var name in DisposableDirectories)
        {
            var path = Path.Combine(profileDirectory, name);
            if (!Directory.Exists(path))
                continue;

            var size = GetPathSize(path);
            if (TryDeleteDirectory(path))
            {
                freed += size;
                removed.Add(name + "/");
            }
        }

        foreach (var name in DisposableFiles)
        {
            var path = Path.Combine(profileDirectory, name);
            if (!File.Exists(path))
                continue;

            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                // Size is only used for reporting.
            }

            if (TryDeleteFile(path))
            {
                freed += size;
                removed.Add(name);
            }
        }

        return new ProfileCleanupResult(freed, removed.Count, removed);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static long GetPathSize(string path)
    {
        try
        {
            return Directory
                .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception) when (attempt < 2)
            {
                ClearReadOnlyAttributes(path);
                Thread.Sleep(120);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryDeleteFile(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(120);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }
}

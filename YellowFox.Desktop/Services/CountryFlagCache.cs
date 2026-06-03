using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YellowFox.Desktop.Services;

public static class CountryFlagCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CountryLocks = new();
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YellowFox",
        "flags");

    public static async Task<string?> GetFlagPathAsync(string? countryCode)
    {
        var normalized = NormalizeCountryCode(countryCode);
        if (normalized == null)
            return null;

        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, $"{normalized}.png");
        if (IsCachedFlagUsable(path))
            return path;

        var countryLock = CountryLocks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await countryLock.WaitAsync();
        try
        {
            if (IsCachedFlagUsable(path))
                return path;

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var bytes = await HttpClient.GetByteArrayAsync($"https://flagcdn.com/w40/{normalized}.png", cancellation.Token);
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, cancellation.Token);
            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        finally
        {
            countryLock.Release();
        }
    }

    private static bool IsCachedFlagUsable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        var normalized = countryCode?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != 2)
            return null;

        foreach (var ch in normalized)
        {
            if (ch is < 'a' or > 'z')
                return null;
        }

        return normalized;
    }
}

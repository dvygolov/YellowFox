using System.IO;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class ProfileCleanupServiceTests : IDisposable
{
    private readonly string _dir;

    public ProfileCleanupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Clean_RemovesRegenerableData_KeepsIdentity()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cache2"));
        File.WriteAllText(Path.Combine(_dir, "cache2", "entry.bin"), new string('x', 2048));
        Directory.CreateDirectory(Path.Combine(_dir, "startupCache"));
        File.WriteAllText(Path.Combine(_dir, "startupCache", "cache.bin"), new string('y', 1024));
        File.WriteAllText(Path.Combine(_dir, "favicons.sqlite"), "favicons");
        File.WriteAllText(Path.Combine(_dir, "parent.lock"), string.Empty);

        File.WriteAllText(Path.Combine(_dir, "cookies.sqlite"), "cookies");
        File.WriteAllText(Path.Combine(_dir, "prefs.js"), "prefs");
        File.WriteAllText(Path.Combine(_dir, "yellowfox-passkeys.json"), "{}");
        var storageDir = Path.Combine(_dir, "storage", "default", "https+++example.com");
        Directory.CreateDirectory(storageDir);
        File.WriteAllText(Path.Combine(storageDir, "ls"), "identity-data");

        var result = ProfileCleanupService.Clean(_dir);

        Assert.True(result.FreedAnything);
        Assert.True(result.BytesFreed >= 3072);
        Assert.False(Directory.Exists(Path.Combine(_dir, "cache2")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "startupCache")));
        Assert.False(File.Exists(Path.Combine(_dir, "favicons.sqlite")));
        Assert.False(File.Exists(Path.Combine(_dir, "parent.lock")));

        Assert.True(File.Exists(Path.Combine(_dir, "cookies.sqlite")));
        Assert.True(File.Exists(Path.Combine(_dir, "prefs.js")));
        Assert.True(File.Exists(Path.Combine(_dir, "yellowfox-passkeys.json")));
        Assert.True(File.Exists(Path.Combine(storageDir, "ls")));
    }

    [Fact]
    public void Clean_MissingDirectory_IsNoOp()
    {
        var result = ProfileCleanupService.Clean(Path.Combine(_dir, "missing"));

        Assert.Equal(0, result.ItemsRemoved);
        Assert.False(result.FreedAnything);
    }

    [Fact]
    public void FormatBytes_UsesReadableUnits()
    {
        Assert.Equal("512 B", ProfileCleanupService.FormatBytes(512));
        Assert.Equal("1 KB", ProfileCleanupService.FormatBytes(1024));
        Assert.Equal("1.5 MB", ProfileCleanupService.FormatBytes(1024 * 1024 + 512 * 1024));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

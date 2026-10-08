using System.IO;
using System.IO.Compression;
using System.Linq;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class ProfileBackupServiceTests : IDisposable
{
    private readonly string _dir;

    public ProfileBackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void CreateBackup_KeepsIdentity_SkipsHttpCacheOnly()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cache2"));
        File.WriteAllBytes(Path.Combine(_dir, "cache2", "entry.bin"), new byte[64 * 1024]);
        File.WriteAllText(Path.Combine(_dir, "cookies.sqlite"), "cookies");
        File.WriteAllText(Path.Combine(_dir, "prefs.js"), "prefs");
        File.WriteAllText(Path.Combine(_dir, "parent.lock"), string.Empty);
        var storageDir = Path.Combine(_dir, "storage", "default", "https+++example.com");
        Directory.CreateDirectory(storageDir);
        File.WriteAllText(Path.Combine(storageDir, "ls"), "data");

        var zipPath = Path.Combine(_dir, "backup.zip");
        var result = ProfileBackupService.CreateBackup(_dir, zipPath);

        Assert.True(File.Exists(zipPath));
        Assert.True(result.SourceBytes > 0);
        Assert.True(result.FileCount >= 3);

        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();

        Assert.Contains("cookies.sqlite", names);
        Assert.Contains("prefs.js", names);
        Assert.Contains(names, name => name.StartsWith("storage/"));
        Assert.DoesNotContain(names, name => name.StartsWith("cache2/"));
        Assert.DoesNotContain("parent.lock", names);
    }

    [Fact]
    public void CreateBackup_MissingDirectory_ReturnsEmpty()
    {
        var zipPath = Path.Combine(_dir, "backup.zip");
        var result = ProfileBackupService.CreateBackup(Path.Combine(_dir, "missing"), zipPath);

        Assert.Equal(0, result.SourceBytes);
        Assert.Equal(0, result.FileCount);
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

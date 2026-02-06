using System.IO;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class DatabaseServiceTests : IDisposable
{
    private readonly string _testDataDir;

    public DatabaseServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
    }

    [Fact]
    public void CreateAndReadProfile_WithProxyId_ShouldPersistProxyReference()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var proxy = new Proxy
        {
            Name = "Proxy A",
            Type = "http",
            Host = "1.2.3.4",
            Port = 8080
        };
        database.CreateProxy(proxy);

        var profile = new Profile
        {
            Name = "Profile A",
            ProxyId = proxy.Id
        };
        database.CreateProfile(profile);

        var saved = database.GetProfile(profile.Id);

        Assert.NotNull(saved);
        Assert.Equal(proxy.Id, saved!.ProxyId);
    }

    [Fact]
    public void DeleteProxy_ShouldClearProxyReferenceFromProfiles()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var proxy = new Proxy
        {
            Name = "Proxy B",
            Type = "socks5",
            Host = "5.6.7.8",
            Port = 1080
        };
        database.CreateProxy(proxy);

        var profile = new Profile
        {
            Name = "Profile B",
            ProxyId = proxy.Id
        };
        database.CreateProfile(profile);

        database.DeleteProxy(proxy.Id);
        var saved = database.GetProfile(profile.Id);

        Assert.NotNull(saved);
        Assert.Null(saved!.ProxyId);
    }

    [Fact]
    public void CreateAndReadExtension_ShouldPersist()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var extension = new ExtensionItem
        {
            Name = "uBlock",
            Path = @"D:\tmp\ext\uBlock",
            IsEnabled = true
        };

        database.CreateExtension(extension);
        var all = database.GetAllExtensions();

        Assert.Single(all);
        Assert.Equal("uBlock", all[0].Name);
    }

    [Fact]
    public void GetEnabledExtensions_ShouldReturnOnlyEnabled()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        database.CreateExtension(new ExtensionItem
        {
            Name = "Enabled Ext",
            Path = @"D:\tmp\ext\enabled",
            IsEnabled = true
        });
        database.CreateExtension(new ExtensionItem
        {
            Name = "Disabled Ext",
            Path = @"D:\tmp\ext\disabled",
            IsEnabled = false
        });

        var enabled = database.GetEnabledExtensions();

        Assert.Single(enabled);
        Assert.Equal("Enabled Ext", enabled[0].Name);
    }

    [Fact]
    public void CreateAndReadBookmark_ShouldPersist()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var bookmark = new BookmarkItem
        {
            Title = "Example",
            Url = "https://example.com",
            Folder = "Work"
        };

        database.CreateBookmark(bookmark);
        var all = database.GetAllBookmarks();

        Assert.Single(all);
        Assert.Equal("Example", all[0].Title);
        Assert.Equal("Work", all[0].Folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            Directory.Delete(_testDataDir, true);
        }
    }
}

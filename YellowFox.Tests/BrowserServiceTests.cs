using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class BrowserServiceTests : IDisposable
{
    private readonly string _testDataDir;

    public BrowserServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "yellowfox-browser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
    }

    [Fact]
    public void IsExtensionPathUsable_ShouldAcceptFilesAndDirectories()
    {
        var extensionDir = Path.Combine(_testDataDir, "extension");
        Directory.CreateDirectory(extensionDir);
        File.WriteAllText(Path.Combine(extensionDir, "manifest.json"), "{}");
        var invalidExtensionDir = Path.Combine(_testDataDir, "invalid-extension");
        Directory.CreateDirectory(invalidExtensionDir);
        var extensionFile = Path.Combine(_testDataDir, "extension.xpi");
        File.WriteAllText(extensionFile, "fake xpi");

        Assert.True(BrowserService.IsExtensionPathUsable(extensionDir));
        Assert.False(BrowserService.IsExtensionPathUsable(invalidExtensionDir));
        Assert.False(BrowserService.IsExtensionPathUsable(extensionFile));
        Assert.False(BrowserService.IsExtensionPathUsable(Path.Combine(_testDataDir, "missing")));
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldRefreshPlacesWhenBookmarksChange()
    {
        var placesPath = Path.Combine(_testDataDir, "places.sqlite");
        File.WriteAllText(placesPath, "old places db");

        var extensionPath = BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        Assert.False(File.Exists(placesPath));
        var bookmarksHtml = File.ReadAllText(Path.Combine(_testDataDir, "bookmarks.html"));
        Assert.Contains("Bookmarks Toolbar", bookmarksHtml);
        Assert.Contains("PERSONAL_TOOLBAR_FOLDER=\"true\"", bookmarksHtml);
        Assert.Contains("yellowfox shared", bookmarksHtml);
        Assert.Contains("Work", bookmarksHtml);
        Assert.Contains("Example", bookmarksHtml);

        var userJs = File.ReadAllText(Path.Combine(_testDataDir, "user.js"));
        Assert.Contains("browser.places.importBookmarksHTML", userJs);
        Assert.Contains("browser.toolbars.bookmarks.visibility\", \"always\"", userJs);
        Assert.Contains("browser.toolbars.bookmarks.showOtherBookmarks\", false", userJs);
        Assert.Contains("browser.toolbars.bookmarks.showInPrivateBrowsing\", true", userJs);
        Assert.Contains("browser.policies.runOncePerModification.displayBookmarksToolbar\", \"always\"", userJs);
        Assert.Contains("toolkit.legacyUserProfileCustomizations.stylesheets\", true", userJs);
        var xulStore = File.ReadAllText(Path.Combine(_testDataDir, "xulstore.json"));
        Assert.Contains("PersonalToolbar", xulStore);
        Assert.Contains("false", xulStore);
        var userChrome = File.ReadAllText(Path.Combine(_testDataDir, "chrome", "userChrome.css"));
        Assert.Contains("#PersonalToolbar", userChrome);
        Assert.True(BrowserService.IsExtensionPathUsable(extensionPath));
        var backgroundJs = File.ReadAllText(Path.Combine(extensionPath, "background.js"));
        Assert.Contains("yellowfox shared", backgroundJs);
        Assert.Contains("https://example.com", backgroundJs);

        File.WriteAllText(placesPath, "current places db");
        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        Assert.True(File.Exists(placesPath));
    }

    [Fact]
    public void TextSanitizer_ShouldStripHtmlTagsFromNotes()
    {
        var plainText = TextSanitizer.HtmlToPlainText("<p>Line <strong>one</strong></p><p>Second&nbsp;line</p>");

        Assert.Equal($"Line one{Environment.NewLine}Second line", plainText);
    }

    [Fact]
    public void ParseCookiesForImport_ShouldAcceptDolphinChromeCookieShape()
    {
        var json = """
        {
          "export": {
            "cookies": [
              {
                "name": "sid",
                "value": "abc",
                "domain": ".example.com",
                "path": "/",
                "expirationDate": 1772793353.4,
                "httpOnly": true,
                "secure": true,
                "sameSite": "no_restriction"
              }
            ]
          }
        }
        """;

        var cookies = BrowserService.ParseCookiesForImport(json);

        Assert.Single(cookies);
        Assert.Equal("sid", cookies[0].Name);
        Assert.Equal(".example.com", cookies[0].Domain);
        Assert.Equal(Microsoft.Playwright.SameSiteAttribute.None, cookies[0].SameSite);
    }

    [Fact]
    public void ParseLocalStorageForImport_ShouldAcceptDolphinLevelDbShape()
    {
        var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("_https://example.com\0\u0001theme"));
        var partitionedKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("_https://cdn.example.com/^0https://top.example\0\u0001token"));
        var json = $$"""
        {
          "data": {
            "{{key}}": "dark",
            "{{partitionedKey}}": "abc",
            "VkVSU0lPTg==": "1"
          }
        }
        """;

        var storage = BrowserService.ParseLocalStorageForImport(json);

        Assert.Equal("dark", storage["https://example.com"]["theme"]);
        Assert.Equal("abc", storage["https://cdn.example.com"]["token"]);
        Assert.False(storage.ContainsKey("VERSION"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
            Directory.Delete(_testDataDir, true);
    }
}

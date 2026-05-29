using Microsoft.Data.Sqlite;
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
    public void PrepareSharedExtensions_ShouldCopyManagedExtensionsAndPreserveManualOnRemoval()
    {
        var sourceDir = CreateExtensionSource("source-extension", "managed@yellowfox.test", "Managed Extension");
        var profileExtensionsDir = Path.Combine(_testDataDir, "extensions");
        var manualDir = Path.Combine(profileExtensionsDir, "manual@profile.test");
        Directory.CreateDirectory(manualDir);
        File.WriteAllText(Path.Combine(manualDir, "manifest.json"), """
        {"manifest_version":2,"name":"Manual","version":"1.0","browser_specific_settings":{"gecko":{"id":"manual@profile.test"}}}
        """);

        var result = BrowserService.PrepareSharedExtensions(_testDataDir, new[]
        {
            new ExtensionItem { Name = "Managed", Path = sourceDir, IsEnabled = true }
        });

        Assert.Equal(1, result.InstalledCount);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.True(File.Exists(Path.Combine(profileExtensionsDir, "managed@yellowfox.test.xpi")));
        Assert.True(File.Exists(Path.Combine(manualDir, "manifest.json")));
        var stateJson = File.ReadAllText(Path.Combine(_testDataDir, ".yellowfox-managed-extensions.json"));
        Assert.Contains("managed@yellowfox.test", stateJson);
        var userJs = File.ReadAllText(Path.Combine(_testDataDir, "user.js"));
        var prefsJs = File.ReadAllText(Path.Combine(_testDataDir, "prefs.js"));
        Assert.Contains("browser.uiCustomization.state", userJs);
        Assert.Contains("managed_yellowfox_test-browser-action", userJs);
        Assert.Contains("urlbar-container", prefsJs);
        Assert.Contains("unified-extensions-button", prefsJs);

        result = BrowserService.PrepareSharedExtensions(_testDataDir, Array.Empty<ExtensionItem>());

        Assert.Equal(0, result.InstalledCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(File.Exists(Path.Combine(profileExtensionsDir, "managed@yellowfox.test.xpi")));
        Assert.True(File.Exists(Path.Combine(manualDir, "manifest.json")));
    }

    [Fact]
    public void PrepareSharedExtensions_ShouldSkipExtensionsWithoutStableGeckoId()
    {
        var sourceDir = Path.Combine(_testDataDir, "no-id-extension");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "manifest.json"), """
        {"manifest_version":2,"name":"No ID","version":"1.0"}
        """);

        var result = BrowserService.PrepareSharedExtensions(_testDataDir, new[]
        {
            new ExtensionItem { Name = "No ID", Path = sourceDir, IsEnabled = true }
        });

        Assert.Equal(0, result.InstalledCount);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void PrepareSharedExtensions_ShouldResetStartupCacheWhenManagedExtensionIsDisabled()
    {
        var sourceDir = CreateExtensionSource("source-extension", "managed@yellowfox.test", "Managed Extension");
        BrowserService.PrepareSharedExtensions(_testDataDir, new[]
        {
            new ExtensionItem { Name = "Managed", Path = sourceDir, IsEnabled = true }
        });

        var extensionsJsonPath = Path.Combine(_testDataDir, "extensions.json");
        File.WriteAllText(extensionsJsonPath, """
        {"addons":[{"id":"managed@yellowfox.test","active":false,"userDisabled":true,"appDisabled":false}]}
        """);

        var result = BrowserService.PrepareSharedExtensions(_testDataDir, new[]
        {
            new ExtensionItem { Name = "Managed", Path = sourceDir, IsEnabled = true }
        });

        Assert.Equal(0, result.InstalledCount);
        Assert.Equal(0, result.RemovedCount);
        Assert.False(File.Exists(extensionsJsonPath));
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldPreservePlacesAndSyncManagedBookmarks()
    {
        var placesPath = Path.Combine(_testDataDir, "places.sqlite");
        File.WriteAllText(placesPath, "old places db");

        var extensionPath = BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        Assert.True(File.Exists(placesPath));
        var bookmarksHtml = File.ReadAllText(Path.Combine(_testDataDir, "bookmarks.html"));
        Assert.Contains("Bookmarks Toolbar", bookmarksHtml);
        Assert.Contains("PERSONAL_TOOLBAR_FOLDER=\"true\"", bookmarksHtml);
        Assert.DoesNotContain("yellowfox shared", bookmarksHtml);
        Assert.Contains("Work", bookmarksHtml);
        Assert.Contains("Example", bookmarksHtml);

        var userJs = File.ReadAllText(Path.Combine(_testDataDir, "user.js"));
        Assert.Contains("browser.places.importBookmarksHTML", userJs);
        Assert.Contains("browser.places.importBookmarksHTML\", false", userJs);
        Assert.Contains("browser.toolbars.bookmarks.visibility\", \"always\"", userJs);
        Assert.Contains("browser.toolbars.bookmarks.showOtherBookmarks\", false", userJs);
        Assert.Contains("browser.toolbars.bookmarks.showInPrivateBrowsing\", true", userJs);
        Assert.Contains("browser.policies.runOncePerModification.displayBookmarksToolbar\", \"always\"", userJs);
        Assert.DoesNotContain("toolkit.legacyUserProfileCustomizations.stylesheets", userJs);
        Assert.Contains("browser.startup.page\", 0", userJs);
        Assert.Contains("browser.aboutwelcome.enabled\", false", userJs);
        Assert.Contains("browser.preonboarding.enabled\", false", userJs);
        Assert.Contains("taskbar.grouping.useprofile\", true", userJs);
        Assert.Contains("browser.startup.blankWindow\", false", userJs);
        Assert.Contains("keyword.enabled\", true", userJs);
        Assert.Contains("dom.event.contextmenu.enabled\", false", userJs);
        Assert.Contains("browser.fixup.fallback-to-https\", true", userJs);
        Assert.Contains("browser.fixup.upgrade_to_https\", true", userJs);
        Assert.Contains("dom.security.https_first\", true", userJs);
        Assert.Contains("dom.security.https_only_mode\", true", userJs);
        Assert.Contains("browser.search.defaultenginename\", \"Google\"", userJs);
        Assert.Contains("browser.search.selectedEngine\", \"Google\"", userJs);
        Assert.Contains("extensions.autoDisableScopes\", 0", userJs);
        Assert.Contains("extensions.enabledScopes\", 5", userJs);
        Assert.Contains("datareporting.policy.dataSubmissionPolicyAcceptedVersion\", 999", userJs);
        Assert.Contains("datareporting.policy.dataSubmissionPolicyNotifiedTime\", \"0\"", userJs);
        Assert.DoesNotContain("browser.sessionstore.resume_from_crash", userJs);
        Assert.DoesNotContain("browser.link.open_newwindow", userJs);
        Assert.DoesNotContain("toolkit.telemetry.reportingpolicy.firstRun", userJs);
        var prefsJs = File.ReadAllText(Path.Combine(_testDataDir, "prefs.js"));
        Assert.Contains("browser.toolbars.bookmarks.visibility\", \"always\"", prefsJs);
        Assert.Contains("browser.preonboarding.enabled\", false", prefsJs);
        Assert.Contains("taskbar.grouping.useprofile\", true", prefsJs);
        Assert.Contains("browser.startup.blankWindow\", false", prefsJs);
        Assert.Contains("keyword.enabled\", true", prefsJs);
        Assert.Contains("dom.event.contextmenu.enabled\", false", prefsJs);
        Assert.Contains("browser.fixup.fallback-to-https\", true", prefsJs);
        Assert.Contains("dom.security.https_only_mode\", true", prefsJs);
        Assert.Contains("browser.search.defaultenginename\", \"Google\"", prefsJs);
        Assert.Contains("extensions.autoDisableScopes\", 0", prefsJs);
        Assert.Contains("extensions.enabledScopes\", 5", prefsJs);
        Assert.Contains("datareporting.policy.dataSubmissionPolicyAcceptedVersion\", 999", prefsJs);
        Assert.DoesNotContain("browser.uiCustomization.state", prefsJs);
        Assert.False(File.Exists(Path.Combine(_testDataDir, "xulstore.json")));
        Assert.False(File.Exists(Path.Combine(_testDataDir, "chrome", "userChrome.css")));
        Assert.True(BrowserService.IsExtensionPathUsable(extensionPath));
        var backgroundJs = File.ReadAllText(Path.Combine(extensionPath, "background.js"));
        Assert.Contains("legacyRootTitle", backgroundJs);
        Assert.Contains("previousBookmarks", backgroundJs);
        Assert.Contains("removeManagedItem", backgroundJs);
        Assert.Contains("browser.storage.local", backgroundJs);
        Assert.Contains("ensureFolderPath(toolbar, item.folder)", backgroundJs);
        Assert.DoesNotContain("enforceSingleWindow", backgroundJs);
        Assert.Contains("https://example.com", backgroundJs);
        var manifestJson = File.ReadAllText(Path.Combine(extensionPath, "manifest.json"));
        Assert.Contains("\"storage\"", manifestJson);

        File.WriteAllText(placesPath, "current places db");
        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        Assert.True(File.Exists(placesPath));
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldPassPreviousManagedBookmarksToExtension()
    {
        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Id = "old", Title = "Old", Url = "https://old.example", Folder = "Work" }
        });

        var extensionPath = BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Id = "new", Title = "New", Url = "https://new.example", Folder = "Work" }
        });

        var backgroundJs = File.ReadAllText(Path.Combine(extensionPath, "background.js"));
        Assert.Contains("https://old.example", backgroundJs);
        Assert.Contains("https://new.example", backgroundJs);
        var stateJson = File.ReadAllText(Path.Combine(_testDataDir, ".yellowfox-managed-bookmarks.json"));
        Assert.DoesNotContain("https://old.example", stateJson);
        Assert.Contains("https://new.example", stateJson);
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldRemoveDeletedManagedPlacesBookmarkAndKeepManualBookmark()
    {
        var placesPath = CreatePlacesDatabaseWithManualBookmark("Work", "Manual", "https://manual.example/");

        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Id = "managed", Title = "Managed", Url = "https://managed.example/", Folder = "Work" }
        });

        Assert.True(PlacesBookmarkExists(placesPath, "Work", "Managed", "https://managed.example/"));
        Assert.True(PlacesBookmarkExists(placesPath, "Work", "Manual", "https://manual.example/"));

        BrowserService.PrepareSharedBookmarks(_testDataDir, Array.Empty<BookmarkItem>());

        Assert.False(PlacesBookmarkExists(placesPath, "Work", "Managed", "https://managed.example/"));
        Assert.True(PlacesBookmarkExists(placesPath, "Work", "Manual", "https://manual.example/"));
        Assert.True(PlacesFolderExists(placesPath, "Work"));
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldPreserveTabsSnapshotWhileClearingNativeSession()
    {
        var tabsStatePath = Path.Combine(_testDataDir, "tabs-state.json");
        var sessionStorePath = Path.Combine(_testDataDir, "sessionstore.jsonlz4");
        File.WriteAllText(tabsStatePath, """{"urls":["https://example.com"]}""");
        File.WriteAllText(sessionStorePath, "native session");

        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        Assert.True(File.Exists(tabsStatePath));
        Assert.False(File.Exists(sessionStorePath));
    }

    [Fact]
    public void PrepareSharedBookmarks_ShouldForceBookmarksToolbarInExistingPrefs()
    {
        File.WriteAllText(Path.Combine(_testDataDir, "prefs.js"), """
        user_pref("browser.toolbars.bookmarks.visibility", "never");
        user_pref("toolkit.legacyUserProfileCustomizations.stylesheets", false);
        """);

        BrowserService.PrepareSharedBookmarks(_testDataDir, new[]
        {
            new BookmarkItem { Title = "Example", Url = "https://example.com", Folder = "Work" }
        });

        var prefsJs = File.ReadAllText(Path.Combine(_testDataDir, "prefs.js"));
        Assert.Contains("browser.toolbars.bookmarks.visibility\", \"always\"", prefsJs);
        Assert.DoesNotContain("browser.toolbars.bookmarks.visibility\", \"never\"", prefsJs);
        Assert.DoesNotContain("toolkit.legacyUserProfileCustomizations.stylesheets", prefsJs);
        Assert.DoesNotContain("browser.uiCustomization.state", prefsJs);
    }

    [Fact]
    public void IsRestorableUrl_ShouldRejectCamoufoxProxyErrorIp()
    {
        var method = typeof(BrowserService).GetMethod("IsRestorableUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method.Invoke(null, new object?[] { "http://0.0.7.128/" })!);
        Assert.False((bool)method.Invoke(null, new object?[] { "about:blank" })!);
        Assert.False((bool)method.Invoke(null, new object?[] { "javascript:alert(1)" })!);
        Assert.True((bool)method.Invoke(null, new object?[] { "https://example.com/" })!);
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

    private string CreateExtensionSource(string folderName, string id, string name)
    {
        var sourceDir = Path.Combine(_testDataDir, folderName);
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "manifest.json"), $$"""
        {
          "manifest_version": 2,
          "name": "{{name}}",
          "version": "1.0",
          "browser_specific_settings": {
            "gecko": {
              "id": "{{id}}"
            }
          }
        }
        """);
        File.WriteAllText(Path.Combine(sourceDir, "background.js"), "console.log('managed');");
        return sourceDir;
    }

    private string CreatePlacesDatabaseWithManualBookmark(string folderTitle, string bookmarkTitle, string url)
    {
        var placesPath = Path.Combine(_testDataDir, "places.sqlite");
        using var connection = new SqliteConnection($"Data Source={placesPath};Pooling=False");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE moz_places (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT,
                title TEXT,
                rev_host TEXT,
                visit_count INTEGER,
                hidden INTEGER,
                typed INTEGER,
                frecency INTEGER,
                guid TEXT,
                foreign_count INTEGER,
                url_hash INTEGER
            );
            CREATE TABLE moz_bookmarks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type INTEGER,
                fk INTEGER,
                parent INTEGER,
                position INTEGER,
                title TEXT,
                dateAdded INTEGER,
                lastModified INTEGER,
                guid TEXT
            );
            INSERT INTO moz_bookmarks(id, type, fk, parent, position, title, dateAdded, lastModified, guid)
            VALUES(1, 2, NULL, 0, 0, 'Bookmarks Toolbar', 0, 0, 'toolbar_____');
            INSERT INTO moz_bookmarks(id, type, fk, parent, position, title, dateAdded, lastModified, guid)
            VALUES(2, 2, NULL, 1, 0, $folder, 0, 0, 'manualfolder');
            INSERT INTO moz_places(id, url, title, rev_host, visit_count, hidden, typed, frecency, guid, foreign_count, url_hash)
            VALUES(1, $url, $title, 'elpmaxe.launam.', 0, 0, 0, -1, 'manualplace_', 1, 0);
            INSERT INTO moz_bookmarks(id, type, fk, parent, position, title, dateAdded, lastModified, guid)
            VALUES(3, 1, 1, 2, 0, $title, 0, 0, 'manualbmark_');
            """;
            command.Parameters.AddWithValue("$folder", folderTitle);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", bookmarkTitle);
            command.ExecuteNonQuery();
        }

        return placesPath;
    }

    private static bool PlacesBookmarkExists(string placesPath, string folderTitle, string bookmarkTitle, string url)
    {
        using var connection = new SqliteConnection($"Data Source={placesPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT COUNT(*)
        FROM moz_bookmarks b
        JOIN moz_bookmarks f ON f.id = b.parent
        JOIN moz_places p ON p.id = b.fk
        WHERE f.title = $folder AND b.title = $title AND p.url = $url
        """;
        command.Parameters.AddWithValue("$folder", folderTitle);
        command.Parameters.AddWithValue("$title", bookmarkTitle);
        command.Parameters.AddWithValue("$url", url);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool PlacesFolderExists(string placesPath, string folderTitle)
    {
        using var connection = new SqliteConnection($"Data Source={placesPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM moz_bookmarks WHERE type = 2 AND title = $title";
        command.Parameters.AddWithValue("$title", folderTitle);
        return (long)command.ExecuteScalar()! > 0;
    }
}

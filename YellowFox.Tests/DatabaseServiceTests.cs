using System.IO;
using System.IO.Compression;
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
    public void CreateAndReadProxy_ShouldPreserveSocks5Type()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var proxy = new Proxy
        {
            Name = "SOCKS Proxy",
            Type = "socks5",
            Host = "5.6.7.8",
            Port = 1080,
            Username = "user",
            Password = "pass",
            IpChangeUrl = "https://proxy.example/rotate"
        };

        database.CreateProxy(proxy);
        var saved = database.GetProxy(proxy.Id);

        Assert.NotNull(saved);
        Assert.Equal("socks5", saved!.Type);
        Assert.Equal("user", saved.Username);
        Assert.Equal("pass", saved.Password);
        Assert.Equal("https://proxy.example/rotate", saved.IpChangeUrl);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public void CreateAndReadProfile_ShouldStoreNotesAsPlainText()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var profile = new Profile
        {
            Name = "Profile Notes",
            Notes = "<p>First <strong>line</strong></p><p>Second&nbsp;line</p>"
        };

        database.CreateProfile(profile);
        var saved = database.GetProfile(profile.Id);

        Assert.NotNull(saved);
        Assert.Equal($"First line{Environment.NewLine}Second line", saved!.Notes);
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
    public void ImportArchive_ShouldExtractExtensionIntoDataDirectory()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var archivePath = Path.Combine(_testDataDir, "ublock.xpi");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write("{}");
        }

        var storage = new ExtensionStorageService(database);
        var extension = storage.ImportArchive(archivePath, "uBlock");

        Assert.StartsWith(database.GetExtensionsDataDirectory(), extension.Path);
        Assert.True(File.Exists(Path.Combine(extension.Path, "manifest.json")));
        Assert.True(BrowserService.IsExtensionPathUsable(extension.Path));
    }

    [Fact]
    public void TryBuildAmoApiUrl_ShouldExtractAddonSlug()
    {
        var ok = ExtensionStorageService.TryBuildAmoApiUrl(
            "https://addons.mozilla.org/en-US/firefox/addon/darkreader/",
            out var apiUrl);

        Assert.True(ok);
        Assert.Equal("https://addons.mozilla.org/api/v5/addons/addon/darkreader/?app=firefox", apiUrl);
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

    [Fact]
    public void CreateAndDeleteBookmarkFolder_ShouldPersistTree()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var rootFolder = new BookmarkItem
        {
            Title = "Root",
            IsFolder = true
        };
        database.CreateBookmark(rootFolder);

        var childFolder = new BookmarkItem
        {
            Title = "Child",
            ParentId = rootFolder.Id,
            IsFolder = true
        };
        database.CreateBookmark(childFolder);

        var bookmark = new BookmarkItem
        {
            Title = "Example",
            Url = "https://example.com",
            ParentId = childFolder.Id
        };
        database.CreateBookmark(bookmark);

        var all = database.GetAllBookmarks();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, item => item.Id == rootFolder.Id && item.IsFolder && item.ParentId == null);
        Assert.Contains(all, item => item.Id == childFolder.Id && item.IsFolder && item.ParentId == rootFolder.Id);
        Assert.Contains(all, item => item.Id == bookmark.Id && item.ParentId == childFolder.Id && item.Folder == "Root/Child");

        database.DeleteBookmark(rootFolder.Id);

        Assert.Empty(database.GetAllBookmarks());
    }

    [Fact]
    public void UpdateBookmarks_ShouldMoveBookmarkAndPersistSiblingOrder()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var folder = new BookmarkItem
        {
            Title = "Folder",
            IsFolder = true
        };
        var first = new BookmarkItem
        {
            Title = "First",
            Url = "https://first.example"
        };
        var second = new BookmarkItem
        {
            Title = "Second",
            Url = "https://second.example"
        };
        database.CreateBookmark(folder);
        database.CreateBookmark(first);
        database.CreateBookmark(second);

        var all = database.GetAllBookmarks();
        var savedFolder = all.Single(item => item.Id == folder.Id);
        var savedFirst = all.Single(item => item.Id == first.Id);
        var savedSecond = all.Single(item => item.Id == second.Id);
        savedFirst.ParentId = savedFolder.Id;
        savedFirst.SortOrder = 0;
        savedSecond.SortOrder = 0;
        savedFolder.SortOrder = 1;

        database.UpdateBookmarks(all);
        var saved = database.GetAllBookmarks();

        Assert.Contains(saved, item => item.Id == first.Id
                                      && item.ParentId == folder.Id
                                      && item.Folder == "Folder"
                                      && item.SortOrder == 0);
        Assert.Contains(saved, item => item.Id == second.Id
                                      && item.ParentId == null
                                      && item.SortOrder == 0);
        Assert.Contains(saved, item => item.Id == folder.Id
                                      && item.ParentId == null
                                      && item.SortOrder == 1);
    }

    [Fact]
    public void CreateProfileFolder_ShouldPersistHierarchyAndSortOrder()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var parent = new ProfileFolder { Name = "Parent" };
        database.CreateProfileFolder(parent);
        var child = new ProfileFolder { Name = "Child", ParentId = parent.Id };
        database.CreateProfileFolder(child);

        var folders = database.GetAllProfileFolders();

        Assert.Contains(folders, folder => folder.Id == parent.Id && folder.ParentId == null);
        Assert.Contains(folders, folder => folder.Id == child.Id && folder.ParentId == parent.Id);
    }

    [Fact]
    public void ProfilePlacement_ShouldPersistFolderAndOrder()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var folder = new ProfileFolder { Name = "Folder" };
        database.CreateProfileFolder(folder);
        var profile = new Profile { Name = "Placed Profile" };
        database.CreateProfile(profile);

        database.UpdateProfilePlacement(profile.Id, folder.Id, 3);

        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal(folder.Id, saved!.FolderId);
        Assert.Equal(3, saved.SortOrder);
    }

    [Fact]
    public void DeleteProfileFolder_ShouldMoveProfilesToParentFolder()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var parent = new ProfileFolder { Name = "Parent" };
        database.CreateProfileFolder(parent);
        var child = new ProfileFolder { Name = "Child", ParentId = parent.Id };
        database.CreateProfileFolder(child);
        var profile = new Profile { Name = "Nested Profile", FolderId = child.Id };
        database.CreateProfile(profile);

        database.DeleteProfileFolder(child.Id);

        Assert.DoesNotContain(database.GetAllProfileFolders(), folder => folder.Id == child.Id);
        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal(parent.Id, saved!.FolderId);
    }

    [Fact]
    public void DeleteProfileFolder_WithNestedFolders_ShouldDeleteWholeSubtreeAndKeepProfiles()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var parent = new ProfileFolder { Name = "Parent" };
        database.CreateProfileFolder(parent);
        var child = new ProfileFolder { Name = "Child", ParentId = parent.Id };
        database.CreateProfileFolder(child);
        var grandChild = new ProfileFolder { Name = "GrandChild", ParentId = child.Id };
        database.CreateProfileFolder(grandChild);
        var profile = new Profile { Name = "Deep Profile", FolderId = grandChild.Id };
        database.CreateProfile(profile);

        database.DeleteProfileFolder(parent.Id);

        Assert.Empty(database.GetAllProfileFolders());
        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Null(saved!.FolderId);
    }

    [Fact]
    public void CreateAndReadTag_ShouldPersistNameIconAndColor()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var tag = new Tag { Name = "Facebook", Icon = "globe", Color = "#6EDB76" };
        database.CreateTag(tag);

        var tags = database.GetAllTags();
        var saved = Assert.Single(tags);
        Assert.Equal("Facebook", saved.Name);
        Assert.Equal("globe", saved.Icon);
        Assert.Equal("#6EDB76", saved.Color);
    }

    [Fact]
    public void Profile_WithTags_ShouldRoundTripTagIds()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var first = new Tag { Name = "First" };
        var second = new Tag { Name = "Second" };
        database.CreateTag(first);
        database.CreateTag(second);

        var profile = new Profile { Name = "Tagged", TagIds = new List<string> { first.Id, second.Id } };
        database.CreateProfile(profile);

        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal(new[] { first.Id, second.Id }, saved!.TagIds);

        saved.TagIds.Remove(second.Id);
        database.UpdateProfile(saved);
        var updated = database.GetProfile(profile.Id);
        Assert.Equal(new[] { first.Id }, updated!.TagIds);
    }

    [Fact]
    public void DeleteTag_ShouldClearReferencesFromProfilesExtensionsAndBookmarks()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var tag = new Tag { Name = "ToDelete" };
        database.CreateTag(tag);

        var profile = new Profile { Name = "P", TagIds = new List<string> { tag.Id } };
        database.CreateProfile(profile);
        var extension = new ExtensionItem { Name = "E", Path = "x", TagId = tag.Id };
        database.CreateExtension(extension);
        var bookmark = new BookmarkItem { Title = "B", Url = "https://b", TagId = tag.Id };
        database.CreateBookmark(bookmark);

        database.DeleteTag(tag.Id);

        Assert.Empty(database.GetAllTags());
        Assert.Empty(database.GetProfile(profile.Id)!.TagIds);
        Assert.Null(database.GetAllExtensions().Single().TagId);
        Assert.Null(database.GetAllBookmarks().Single(b => !b.IsFolder).TagId);
    }

    [Fact]
    public void ExtensionAndBookmark_ShouldPersistSingleTag()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var tag = new Tag { Name = "Work" };
        database.CreateTag(tag);

        database.CreateExtension(new ExtensionItem { Name = "E", Path = "x", TagId = tag.Id });
        database.CreateBookmark(new BookmarkItem { Title = "B", Url = "https://b", TagId = tag.Id });

        Assert.Equal(tag.Id, database.GetAllExtensions().Single().TagId);
        Assert.Equal(tag.Id, database.GetAllBookmarks().Single(b => !b.IsFolder).TagId);
    }

    [Fact]
    public void GetExtensionsForProfile_ShouldReturnUntaggedAndMatchingOnly()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var work = new Tag { Name = "Work" };
        var fun = new Tag { Name = "Fun" };
        database.CreateTag(work);
        database.CreateTag(fun);

        database.CreateExtension(new ExtensionItem { Name = "Universal", Path = "u" });
        database.CreateExtension(new ExtensionItem { Name = "WorkExt", Path = "w", TagId = work.Id });
        database.CreateExtension(new ExtensionItem { Name = "FunExt", Path = "f", TagId = fun.Id });

        var profile = new Profile { Name = "P", TagIds = new List<string> { work.Id } };
        var names = database.GetExtensionsForProfile(profile).Select(e => e.Name).ToList();

        Assert.Contains("Universal", names);
        Assert.Contains("WorkExt", names);
        Assert.DoesNotContain("FunExt", names);
    }

    [Fact]
    public void GetBookmarksForProfile_ShouldFilterByTagAndExcludeTaggedFolderSubtree()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var work = new Tag { Name = "Work" };
        var fun = new Tag { Name = "Fun" };
        database.CreateTag(work);
        database.CreateTag(fun);

        var workFolder = new BookmarkItem { Title = "WorkFolder", IsFolder = true, TagId = work.Id };
        var funFolder = new BookmarkItem { Title = "FunFolder", IsFolder = true, TagId = fun.Id };
        database.CreateBookmark(workFolder);
        database.CreateBookmark(funFolder);

        database.CreateBookmark(new BookmarkItem { Title = "InWork", Url = "https://w", ParentId = workFolder.Id });
        database.CreateBookmark(new BookmarkItem { Title = "InFun", Url = "https://f", ParentId = funFolder.Id });
        database.CreateBookmark(new BookmarkItem { Title = "Root", Url = "https://r" });

        var profile = new Profile { Name = "P", TagIds = new List<string> { work.Id } };
        var titles = database.GetBookmarksForProfile(profile).Select(b => b.Title).ToList();

        Assert.Contains("WorkFolder", titles);
        Assert.Contains("InWork", titles);
        Assert.Contains("Root", titles);
        Assert.DoesNotContain("FunFolder", titles);
        Assert.DoesNotContain("InFun", titles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            Directory.Delete(_testDataDir, true);
        }
    }
}

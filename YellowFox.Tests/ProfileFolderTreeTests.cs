using System.IO;
using System.Linq;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Tests;

public class ProfileFolderTreeTests : IDisposable
{
    private readonly string _testDataDir;

    public ProfileFolderTreeTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
    }

    private (DatabaseService Database, ProfilesViewModel ViewModel) CreateViewModel()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var settings = new SettingsService(Path.Combine(_testDataDir, "settings.json"));
        var browser = new BrowserService(database, settings, new ProxyValidatorService());
        return (database, new ProfilesViewModel(database, browser, settings));
    }

    [Fact]
    public void BuildsFolderTree_WithNestedFoldersAndProfiles()
    {
        var (database, viewModel) = CreateViewModel();

        var work = new ProfileFolder { Name = "Work" };
        database.CreateProfileFolder(work);
        var farm = new ProfileFolder { Name = "Farm", ParentId = work.Id };
        database.CreateProfileFolder(farm);

        database.CreateProfile(new Profile { Name = "Nested", FolderId = farm.Id });
        database.CreateProfile(new Profile { Name = "Root" });
        viewModel.RefreshCommand.Execute(null);

        var workNode = Assert.IsType<ProfileFolderNodeViewModel>(viewModel.RootNodes.Single(n => n.Name == "Work"));
        var farmNode = Assert.IsType<ProfileFolderNodeViewModel>(workNode.Children.Single());
        Assert.Equal("Farm", farmNode.Name);
        Assert.Equal("Nested", farmNode.Children.Single().Name);
        Assert.Equal(1, workNode.TotalProfileCount);
        Assert.Equal(1, farmNode.TotalProfileCount);

        Assert.Contains(viewModel.RootNodes, n => n is ProfileItemViewModel profile && profile.Name == "Root");
    }

    [Fact]
    public void MoveProfileIntoFolder_ShouldPersistFolderAssignment()
    {
        var (database, viewModel) = CreateViewModel();

        var folder = new ProfileFolder { Name = "Work" };
        database.CreateProfileFolder(folder);
        var profile = new Profile { Name = "Move Me" };
        database.CreateProfile(profile);
        viewModel.RefreshCommand.Execute(null);

        var profileNode = Assert.IsType<ProfileItemViewModel>(viewModel.RootNodes.Single(n => n is ProfileItemViewModel));
        var folderNode = viewModel.RootNodes.OfType<ProfileFolderNodeViewModel>().Single();

        viewModel.MoveNode(profileNode, folderNode, ProfileDropPosition.Inside);

        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal(folder.Id, saved!.FolderId);

        var folderAfter = viewModel.RootNodes.OfType<ProfileFolderNodeViewModel>().Single();
        Assert.Single(folderAfter.Children);
    }

    [Fact]
    public void MoveProfileToRootArea_ShouldClearFolderAssignment()
    {
        var (database, viewModel) = CreateViewModel();

        var folder = new ProfileFolder { Name = "Work" };
        database.CreateProfileFolder(folder);
        var profile = new Profile { Name = "Nested", FolderId = folder.Id };
        database.CreateProfile(profile);
        viewModel.RefreshCommand.Execute(null);

        var profileNode = viewModel.RootNodes
            .OfType<ProfileFolderNodeViewModel>()
            .Single()
            .Children
            .OfType<ProfileItemViewModel>()
            .Single();

        viewModel.MoveNode(profileNode, null, ProfileDropPosition.RootEnd);

        var saved = database.GetProfile(profile.Id);
        Assert.NotNull(saved);
        Assert.Null(saved!.FolderId);
    }

    [Fact]
    public void CannotMoveFolderIntoItsOwnDescendant()
    {
        var (database, viewModel) = CreateViewModel();

        var parent = new ProfileFolder { Name = "Parent" };
        database.CreateProfileFolder(parent);
        var child = new ProfileFolder { Name = "Child", ParentId = parent.Id };
        database.CreateProfileFolder(child);
        viewModel.RefreshCommand.Execute(null);

        var parentNode = viewModel.RootNodes.OfType<ProfileFolderNodeViewModel>().Single();
        var childNode = parentNode.Children.OfType<ProfileFolderNodeViewModel>().Single();

        Assert.False(viewModel.CanMoveNode(parentNode, childNode, ProfileDropPosition.Inside));
        Assert.False(viewModel.CanMoveNode(parentNode, childNode, ProfileDropPosition.After));

        viewModel.MoveNode(parentNode, childNode, ProfileDropPosition.Inside);
        Assert.Null(database.GetAllProfileFolders().Single(f => f.Id == parent.Id).ParentId);
    }

    [Fact]
    public void SearchFiltersToMatchingProfilesAndTheirAncestorFolders()
    {
        var (database, viewModel) = CreateViewModel();

        var folder = new ProfileFolder { Name = "Work" };
        database.CreateProfileFolder(folder);
        database.CreateProfile(new Profile { Name = "Alpha", FolderId = folder.Id });
        database.CreateProfile(new Profile { Name = "Beta" });

        viewModel.SearchText = "Alpha";

        var visible = viewModel.EnumerateProfileNodes().ToList();
        Assert.Single(visible);
        Assert.Equal("Alpha", visible[0].Name);

        var folderNode = Assert.IsType<ProfileFolderNodeViewModel>(viewModel.RootNodes.Single());
        Assert.Equal("Work", folderNode.Name);
    }

    [Fact]
    public void TagFilterShowsProfilesWithAnySelectedTag()
    {
        var (database, viewModel) = CreateViewModel();

        var work = new Tag { Name = "Work" };
        var farm = new Tag { Name = "Farm" };
        database.CreateTag(work);
        database.CreateTag(farm);

        database.CreateProfile(new Profile { Name = "A", TagIds = new List<string> { work.Id } });
        database.CreateProfile(new Profile { Name = "B", TagIds = new List<string> { farm.Id } });
        database.CreateProfile(new Profile { Name = "C" });
        viewModel.RefreshCommand.Execute(null);

        Assert.False(viewModel.IsTagFiltering);
        Assert.Equal(3, viewModel.EnumerateProfileNodes().Count());

        viewModel.TagFilterOptions.Single(o => o.TagId == work.Id).IsSelected = true;

        Assert.True(viewModel.IsTagFiltering);
        var visible = viewModel.EnumerateProfileNodes().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "A" }, visible);

        viewModel.TagFilterOptions.Single(o => o.TagId == farm.Id).IsSelected = true;

        visible = viewModel.EnumerateProfileNodes().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "A", "B" }, visible);

        viewModel.ClearTagFilterCommand.Execute(null);

        Assert.False(viewModel.IsTagFiltering);
        Assert.Equal(3, viewModel.EnumerateProfileNodes().Count());
    }

    [Fact]
    public void TagFilterKeepsAncestorFoldersOfMatchingProfiles()
    {
        var (database, viewModel) = CreateViewModel();

        var tag = new Tag { Name = "Work" };
        database.CreateTag(tag);

        var folder = new ProfileFolder { Name = "WorkFolder" };
        database.CreateProfileFolder(folder);
        var emptyFolder = new ProfileFolder { Name = "EmptyFolder" };
        database.CreateProfileFolder(emptyFolder);

        database.CreateProfile(new Profile { Name = "Tagged", FolderId = folder.Id, TagIds = new List<string> { tag.Id } });
        database.CreateProfile(new Profile { Name = "Untagged", FolderId = emptyFolder.Id });
        viewModel.RefreshCommand.Execute(null);

        viewModel.TagFilterOptions.Single(o => o.TagId == tag.Id).IsSelected = true;

        var folderNode = Assert.IsType<ProfileFolderNodeViewModel>(viewModel.RootNodes.Single());
        Assert.Equal("WorkFolder", folderNode.Name);
        Assert.Equal("Tagged", folderNode.Children.Single().Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            Directory.Delete(_testDataDir, true);
        }
    }
}

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

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            Directory.Delete(_testDataDir, true);
        }
    }
}

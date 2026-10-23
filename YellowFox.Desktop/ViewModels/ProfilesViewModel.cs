using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;
using YellowFox.Desktop.Views;

namespace YellowFox.Desktop.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private readonly BrowserService _browserService;
    private readonly SettingsService _settingsService;
    private readonly ProxyStatusService? _proxyStatusService;
    private readonly PasskeyStoreService _passkeyStore = new();
    private readonly Dictionary<string, bool> _folderExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProfileFolder> _foldersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Profile> _profilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tag> _tagsById = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ProfileNodeViewModel? _selectedNode;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _showProxy = true;

    [ObservableProperty]
    private bool _showNotes = true;

    [ObservableProperty]
    private bool _showTags = true;

    public ObservableCollection<ProfileNodeViewModel> RootNodes { get; } = new();

    public ObservableCollection<TagFilterItemViewModel> TagFilterOptions { get; } = new();

    public bool HasSelection => SelectedCount > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool IsTagFiltering => TagFilterOptions.Any(option => option.IsSelected);
    public bool HasActiveFilter => IsSearching || IsTagFiltering;
    public int SelectedTagFilterCount => TagFilterOptions.Count(option => option.IsSelected);

    public ProfilesViewModel(DatabaseService databaseService, BrowserService browserService, SettingsService settingsService, ProxyStatusService? proxyStatusService = null)
    {
        _databaseService = databaseService;
        _browserService = browserService;
        _settingsService = settingsService;
        _proxyStatusService = proxyStatusService;

        var settings = _settingsService.GetSettings();
        _showProxy = settings.ProfileTreeShowProxy;
        _showNotes = settings.ProfileTreeShowNotes;
        _showTags = settings.ProfileTreeShowTags;

        _browserService.ProfileRunningStateChanged += OnProfileRunningStateChanged;
        LoadProfiles();
    }

    partial void OnShowProxyChanged(bool value)
    {
        SaveViewOptions();
        UpdateItemViewOptions();
    }

    partial void OnShowNotesChanged(bool value)
    {
        SaveViewOptions();
        UpdateItemViewOptions();
    }

    partial void OnShowTagsChanged(bool value)
    {
        SaveViewOptions();
        UpdateItemViewOptions();
    }

    private void SaveViewOptions()
    {
        var settings = _settingsService.GetSettings();
        settings.ProfileTreeShowProxy = ShowProxy;
        settings.ProfileTreeShowNotes = ShowNotes;
        settings.ProfileTreeShowTags = ShowTags;
        _settingsService.SaveSettings(settings);
    }

    private void UpdateItemViewOptions()
    {
        foreach (var item in EnumerateProfileNodes())
            item.SetViewOptions(ShowProxy, ShowNotes, ShowTags);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        RootNodes.Clear();
        _foldersById.Clear();
        _profilesById.Clear();

        var folders = _databaseService.GetAllProfileFolders();
        var profiles = _databaseService.GetAllProfiles();

        _tagsById.Clear();
        var allTags = _databaseService.GetAllTags();
        foreach (var tag in allTags)
            _tagsById[tag.Id] = tag;

        SyncTagFilterOptions(allTags);

        foreach (var folder in folders)
            _foldersById[folder.Id] = folder;

        foreach (var profile in profiles)
            _profilesById[profile.Id] = profile;

        var filter = IsSearching ? SearchText.Trim() : null;
        var tagFilter = IsTagFiltering ? SelectedTagFilterIds() : null;
        var hasFilter = filter != null || tagFilter != null;

        foreach (var folder in OrderedFolders(null))
        {
            var node = BuildFolderNode(folder, filter, tagFilter);
            if (hasFilter && node.Children.Count == 0)
                continue;

            RootNodes.Add(node);
        }

        foreach (var profile in OrderedProfiles(null))
        {
            if (MatchesFilters(profile, filter, tagFilter))
                RootNodes.Add(BuildProfileNode(profile));
        }

        UpdateSelectedCount();
    }

    private HashSet<string> SelectedTagFilterIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in TagFilterOptions)
        {
            if (option.IsSelected)
                ids.Add(option.TagId);
        }

        return ids;
    }

    private void SyncTagFilterOptions(List<Tag> tags, bool force = false)
    {
        if (!force
            && TagFilterOptions.Count == tags.Count
            && TagFilterOptions.Select(option => option.TagId).SequenceEqual(tags.Select(tag => tag.Id), StringComparer.Ordinal))
        {
            return;
        }

        var selectedIds = SelectedTagFilterIds();
        TagFilterOptions.Clear();
        foreach (var tag in tags)
            TagFilterOptions.Add(new TagFilterItemViewModel(tag, selectedIds.Contains(tag.Id), OnTagFilterOptionChanged));
    }

    private void OnTagFilterOptionChanged()
    {
        OnPropertyChanged(nameof(IsTagFiltering));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(SelectedTagFilterCount));
        LoadProfiles();
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        if (!IsTagFiltering)
            return;

        foreach (var option in TagFilterOptions)
            option.IsSelected = false;
    }

    private ProfileFolderNodeViewModel BuildFolderNode(ProfileFolder folder, string? filter, HashSet<string>? tagFilter)
    {
        var node = CreateFolderNode(folder);
        var hasFilter = filter != null || tagFilter != null;

        foreach (var childFolder in OrderedFolders(folder.Id))
        {
            var childNode = BuildFolderNode(childFolder, filter, tagFilter);
            if (hasFilter && childNode.Children.Count == 0)
                continue;

            node.Children.Add(childNode);
        }

        foreach (var childProfile in OrderedProfiles(folder.Id))
        {
            if (!MatchesFilters(childProfile, filter, tagFilter))
                continue;

            node.Children.Add(BuildProfileNode(childProfile));
        }

        if (hasFilter)
            node.IsExpanded = true;

        node.TotalProfileCount = CountProfiles(folder.Id);
        return node;
    }

    private ProfileFolderNodeViewModel CreateFolderNode(ProfileFolder folder)
    {
        var node = new ProfileFolderNodeViewModel(folder, this);
        if (_folderExpansion.TryGetValue(folder.Id, out var expanded))
            node.IsExpanded = expanded;

        node.PropertyChanged += OnFolderNodePropertyChanged;
        return node;
    }

    private ProfileItemViewModel BuildProfileNode(Profile profile)
    {
        var vm = new ProfileItemViewModel(profile, this, _databaseService, ResolveTags(profile.TagIds), ShowProxy, ShowNotes, ShowTags);
        vm.UpdateRunningStatus(_browserService.IsRunning(profile.Id));
        vm.PropertyChanged += OnProfileItemPropertyChanged;
        return vm;
    }

    private List<Tag> ResolveTags(IEnumerable<string> tagIds)
    {
        var tags = new List<Tag>();
        foreach (var tagId in tagIds)
        {
            if (_tagsById.TryGetValue(tagId, out var tag))
                tags.Add(tag);
        }

        return tags;
    }

    public void ReloadTagChips()
    {
        _tagsById.Clear();
        var allTags = _databaseService.GetAllTags();
        foreach (var tag in allTags)
            _tagsById[tag.Id] = tag;

        SyncTagFilterOptions(allTags, force: true);

        foreach (var item in EnumerateProfileNodes())
            item.SetTags(ResolveTags(item.Profile.TagIds));
    }

    private static bool MatchesFilters(Profile profile, string? filter, HashSet<string>? tagFilter)
    {
        if (filter != null
            && !profile.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            && !(profile.Notes?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        if (tagFilter != null && !profile.TagIds.Any(tagFilter.Contains))
            return false;

        return true;
    }

    private int CountProfiles(string folderId)
    {
        var count = OrderedProfiles(folderId).Count;
        foreach (var child in OrderedFolders(folderId))
            count += CountProfiles(child.Id);

        return count;
    }

    private List<ProfileFolder> OrderedFolders(string? parentId, string? excludeId = null)
    {
        return _foldersById.Values
            .Where(folder => SameParent(folder.ParentId, parentId)
                             && !string.Equals(folder.Id, excludeId, StringComparison.Ordinal))
            .OrderBy(folder => folder.SortOrder)
            .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<Profile> OrderedProfiles(string? parentId, string? excludeId = null)
    {
        return _profilesById.Values
            .Where(profile => SameParent(profile.FolderId, parentId)
                              && !string.Equals(profile.Id, excludeId, StringComparison.Ordinal))
            .OrderBy(profile => profile.SortOrder)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SameParent(string? left, string? right)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.Ordinal);
    }

    private void OnFolderNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ProfileFolderNodeViewModel node && e.PropertyName == nameof(ProfileNodeViewModel.IsExpanded))
        {
            if (!HasActiveFilter)
                _folderExpansion[node.Folder.Id] = node.IsExpanded;
            return;
        }

        if (e.PropertyName == nameof(ProfileNodeViewModel.IsSelected))
            OnPropertyChanged(nameof(HasSelection));
    }

    private void OnProfileItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemViewModel.IsSelected))
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = EnumerateProfileNodes().Count(p => p.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
    }

    public IEnumerable<ProfileItemViewModel> EnumerateProfileNodes()
    {
        foreach (var node in EnumerateNodes(RootNodes))
        {
            if (node is ProfileItemViewModel profile)
                yield return profile;
        }
    }

    public ProfileNodeViewModel? FindNode(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 3)
            return null;

        var kind = token[0];
        var id = token.Substring(2);

        foreach (var node in EnumerateNodes(RootNodes))
        {
            if (kind == 'F' && node is ProfileFolderNodeViewModel folder
                && string.Equals(folder.Folder.Id, id, StringComparison.Ordinal))
            {
                return folder;
            }

            if (kind == 'P' && node is ProfileItemViewModel profile
                && string.Equals(profile.Profile.Id, id, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    private static IEnumerable<ProfileNodeViewModel> EnumerateNodes(IEnumerable<ProfileNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
                yield return child;
        }
    }

    private string? SelectedFolderId()
    {
        return SelectedNode switch
        {
            ProfileFolderNodeViewModel folder => folder.Folder.Id,
            ProfileItemViewModel profile => profile.Profile.FolderId,
            _ => null
        };
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        var folderId = SelectedFolderId();
        var editorVm = new ProfileEditorViewModel(_databaseService, null, initialFolderId: folderId, proxyStatusService: _proxyStatusService);
        var dialog = new ProfileEditorWindow
        {
            DataContext = editorVm
        };

        var result = await dialog.ShowDialog<bool>(GetMainWindow());

        if (result)
        {
            if (folderId != null)
                _folderExpansion[folderId] = true;
            LoadProfiles();
        }
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        var parentId = SelectedFolderId();
        var name = await PromptForText("New Folder", "Folder name", string.Empty, "Enter folder name...");
        if (string.IsNullOrWhiteSpace(name))
            return;

        _databaseService.CreateProfileFolder(new ProfileFolder
        {
            Name = name.Trim(),
            ParentId = parentId
        });

        if (parentId != null)
            _folderExpansion[parentId] = true;

        LoadProfiles();
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadProfiles();
    }

    public async Task RenameFolder(ProfileFolderNodeViewModel folderNode)
    {
        var name = await PromptForText("Rename Folder", "Folder name", folderNode.Folder.Name, "Enter folder name...");
        if (string.IsNullOrWhiteSpace(name))
            return;

        folderNode.Folder.Name = name.Trim();
        _databaseService.UpdateProfileFolder(folderNode.Folder);
        LoadProfiles();
    }

    public async Task RenameProfile(ProfileItemViewModel profileVm)
    {
        var name = await PromptForText("Rename Profile", "Profile name", profileVm.Profile.Name, "Enter profile name...");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var trimmed = name.Trim();
        if (string.Equals(trimmed, profileVm.Profile.Name, StringComparison.Ordinal))
            return;

        var previousName = profileVm.Profile.Name;
        profileVm.Profile.Name = trimmed;
        try
        {
            _databaseService.UpdateProfile(profileVm.Profile);
        }
        catch (Exception ex)
        {
            profileVm.Profile.Name = previousName;
            await ShowInfo("Rename Failed", ex.Message);
            return;
        }

        LoadProfiles();
    }

    public async Task NewSubfolder(ProfileFolderNodeViewModel folderNode)
    {
        var name = await PromptForText("New Subfolder", "Folder name", string.Empty, "Enter folder name...");
        if (string.IsNullOrWhiteSpace(name))
            return;

        _databaseService.CreateProfileFolder(new ProfileFolder
        {
            Name = name.Trim(),
            ParentId = folderNode.Folder.Id
        });

        _folderExpansion[folderNode.Folder.Id] = true;
        LoadProfiles();
    }

    public async Task DeleteFolder(ProfileFolderNodeViewModel folderNode)
    {
        var message = folderNode.TotalProfileCount > 0
            ? $"Delete folder '{folderNode.Folder.Name}'? Profiles inside it will be moved to the parent folder."
            : $"Delete folder '{folderNode.Folder.Name}'?";
        if (!await ShowConfirmation("Delete Folder", message))
            return;

        _databaseService.DeleteProfileFolder(folderNode.Folder.Id);
        _folderExpansion.Remove(folderNode.Folder.Id);
        LoadProfiles();
    }

    public async Task StartProfileAsync(ProfileItemViewModel profileVm)
    {
        var success = await _browserService.StartProfileAsync(profileVm.Profile.Id);
        if (success)
        {
            profileVm.UpdateRunningStatus(true);
        }
    }

    public async Task StopProfileAsync(ProfileItemViewModel profileVm)
    {
        var success = await _browserService.StopProfileAsync(profileVm.Profile.Id);
        if (success)
        {
            profileVm.UpdateRunningStatus(false);
        }
    }

    public async Task EditProfile(ProfileItemViewModel profileVm)
    {
        var editorVm = new ProfileEditorViewModel(_databaseService, profileVm.Profile, proxyStatusService: _proxyStatusService);
        var dialog = new ProfileEditorWindow
        {
            DataContext = editorVm
        };

        var result = await dialog.ShowDialog<bool>(GetMainWindow());

        if (result)
        {
            LoadProfiles();
        }
    }

    private Window GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow!
            : throw new InvalidOperationException("Main window not found");
    }

    public async Task DeleteProfile(ProfileItemViewModel profileVm)
    {
        var result = await ShowConfirmation(
            "Delete Profile",
            $"Are you sure you want to delete profile '{profileVm.Profile.Name}'?");

        if (result)
        {
            if (profileVm.IsRunning)
                await _browserService.StopProfileAsync(profileVm.Profile.Id);

            _databaseService.DeleteProfile(profileVm.Profile.Id);
            LoadProfiles();
        }
    }

    public async Task CloneProfile(ProfileItemViewModel profileVm)
    {
        var editorVm = new ProfileEditorViewModel(_databaseService, profileVm.Profile, isCloneMode: true, proxyStatusService: _proxyStatusService);
        var dialog = new ProfileEditorWindow
        {
            DataContext = editorVm
        };

        var result = await dialog.ShowDialog<bool>(GetMainWindow());

        if (result)
        {
            LoadProfiles();
        }
    }

    public async Task ExportCookies(ProfileItemViewModel profileVm)
    {
        var mainWindow = GetMainWindow();
        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export cookies: {profileVm.Profile.Name}",
            SuggestedFileName = $"{profileVm.Profile.Name}-cookies.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });

        if (file == null)
            return;

        var result = await _browserService.ExportCookiesAsync(profileVm.Profile.Id, file.Path.LocalPath);
        await ShowInfo(result.Success ? "Cookies Export" : "Export Error", result.Message);
    }

    public async Task ImportCookies(ProfileItemViewModel profileVm)
    {
        if (profileVm.IsImportingCookies)
            return;

        var importVm = new CookieImportViewModel(profileVm.Profile.Name);
        var window = new CookieImportWindow
        {
            DataContext = importVm
        };

        var confirmed = await window.ShowDialog<bool>(GetMainWindow());
        if (!confirmed)
            return;

        profileVm.IsImportingCookies = true;
        try
        {
            var result = await _browserService.ImportCookiesFromTextAsync(
                profileVm.Profile.Id,
                importVm.CookieText,
                importVm.Domain,
                "manual input");
            await ShowInfo(result.Success ? "Cookies Import" : "Import Error", result.Message);
        }
        finally
        {
            profileVm.IsImportingCookies = false;
        }
    }

    public Task OpenPasskeys(ProfileItemViewModel profileVm)
    {
        var profileDirectory = _databaseService.GetProfileDataDirectory(profileVm.Profile.Id);
        var window = new PasskeysWindow();
        var passkeysVm = new PasskeysViewModel(
            _passkeyStore,
            profileVm.Profile.Id,
            profileVm.Profile.Name,
            profileDirectory,
            profileVm.IsRunning,
            () => window);
        passkeysVm.CloseRequested += window.Close;
        window.DataContext = passkeysVm;
        window.Show(GetMainWindow());
        return Task.CompletedTask;
    }

    public async Task OpenLog(ProfileItemViewModel profileVm)
    {
        try
        {
            var logPath = _browserService.GetOrCreateProfileLogPath(profileVm.Profile.Id);
            var logViewer = new LogViewerWindow
            {
                DataContext = new LogViewerViewModel(logPath, profileVm.Profile.Name)
            };
            logViewer.Show(GetMainWindow());
        }
        catch (Exception ex)
        {
            await ShowInfo("Log Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task StartAllSelected()
    {
        var selected = EnumerateProfileNodes().Where(p => p.IsSelected && !p.IsRunning).ToList();
        foreach (var profile in selected)
        {
            await StartProfileAsync(profile);
        }
    }

    [RelayCommand]
    private async Task DeleteAllSelected()
    {
        await DeleteSelectedNodes();
    }

    public async Task DeleteSelectedNodes()
    {
        var selectedProfiles = EnumerateProfileNodes().Where(p => p.IsSelected).ToList();
        var selectedFolders = EnumerateNodes(RootNodes).OfType<ProfileFolderNodeViewModel>().Where(f => f.IsSelected).ToList();

        // No checkboxes checked — fall back to the highlighted tree row.
        if (selectedProfiles.Count == 0 && selectedFolders.Count == 0 && SelectedNode != null)
        {
            switch (SelectedNode)
            {
                case ProfileItemViewModel profile:
                    selectedProfiles.Add(profile);
                    break;
                case ProfileFolderNodeViewModel folder:
                    selectedFolders.Add(folder);
                    break;
            }
        }

        if (selectedProfiles.Count == 0 && selectedFolders.Count == 0)
            return;

        var parts = new List<string>();
        if (selectedProfiles.Count > 0)
            parts.Add($"{selectedProfiles.Count} profile(s)");
        if (selectedFolders.Count > 0)
            parts.Add($"{selectedFolders.Count} folder(s)");

        var result = await ShowConfirmation(
            "Delete Selected",
            $"Are you sure you want to delete {string.Join(" and ", parts)}?");
        if (!result)
            return;

        foreach (var profile in selectedProfiles)
            _databaseService.DeleteProfile(profile.Profile.Id);
        foreach (var folder in selectedFolders)
            _databaseService.DeleteProfileFolder(folder.Folder.Id);

        LoadProfiles();
    }

    public bool CanMoveNode(ProfileNodeViewModel dragged, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        if (target == null)
            return dragged is not null;

        if (ReferenceEquals(dragged, target))
            return false;

        if (position == ProfileDropPosition.Inside && target is not ProfileFolderNodeViewModel)
            return false;

        if (dragged is ProfileFolderNodeViewModel draggedFolder)
        {
            if (target is not (ProfileFolderNodeViewModel or ProfileItemViewModel))
                return false;

            var newParentId = ResolveFolderParentId(draggedFolder.Folder, target, position);
            if (newParentId != null && IsFolderOrDescendant(newParentId, draggedFolder.Folder.Id))
                return false;
        }
        else if (dragged is not ProfileItemViewModel)
        {
            return false;
        }

        return true;
    }

    public void MoveNode(ProfileNodeViewModel dragged, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        if (!CanMoveNode(dragged, target, position))
            return;

        switch (dragged)
        {
            case ProfileItemViewModel profileNode:
                MoveProfile(profileNode.Profile, target, position);
                break;
            case ProfileFolderNodeViewModel folderNode:
                MoveFolder(folderNode.Folder, target, position);
                break;
            default:
                return;
        }

        LoadProfiles();
    }

    public void MoveNodes(IReadOnlyList<ProfileNodeViewModel> nodes, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        if (nodes.Count == 0)
            return;

        if (nodes.Count == 1)
        {
            MoveNode(nodes[0], target, position);
            return;
        }

        MoveNodesToFolder(nodes, ResolveDropTargetFolderId(target, position));
    }

    public bool CanMoveNodes(IReadOnlyList<ProfileNodeViewModel> nodes, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        if (nodes.Count == 0)
            return false;

        if (nodes.Count == 1)
            return CanMoveNode(nodes[0], target, position);

        var targetFolderId = ResolveDropTargetFolderId(target, position);
        if (targetFolderId != null
            && nodes.OfType<ProfileFolderNodeViewModel>().Any(folder => IsFolderOrDescendant(targetFolderId, folder.Folder.Id)))
        {
            return false;
        }

        return nodes.Any(node => CanMoveNodeToFolder(node, targetFolderId));
    }

    public bool CanMoveNodeToFolder(ProfileNodeViewModel node, string? targetFolderId)
    {
        if (node is not (ProfileItemViewModel or ProfileFolderNodeViewModel))
            return false;

        return targetFolderId == null
               || node is not ProfileFolderNodeViewModel folder
               || !IsFolderOrDescendant(targetFolderId, folder.Folder.Id);
    }

    public static string? ResolveDropTargetFolderId(ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        if (target == null)
            return null;

        if (position == ProfileDropPosition.Inside && target is ProfileFolderNodeViewModel insideFolder)
            return insideFolder.Folder.Id;

        return target switch
        {
            ProfileItemViewModel profile => profile.Profile.FolderId,
            ProfileFolderNodeViewModel folder => folder.Folder.ParentId,
            _ => null
        };
    }

    /// <summary>
    /// Moves every given node into the target folder (null means the tree root),
    /// keeping the current order of the nodes.
    /// </summary>
    public void MoveNodesToFolder(IEnumerable<ProfileNodeViewModel> nodeSource, string? targetFolderId)
    {
        var nodes = nodeSource.Distinct().ToList();
        var targetFolder = string.IsNullOrWhiteSpace(targetFolderId) ? null : targetFolderId;

        var folderNodes = nodes.OfType<ProfileFolderNodeViewModel>().ToList();
        var profileNodes = nodes.OfType<ProfileItemViewModel>().ToList();
        if (folderNodes.Count == 0 && profileNodes.Count == 0)
            return;

        var moved = false;

        if (targetFolder != null
            && folderNodes.Any(folderNode => IsFolderOrDescendant(targetFolder, folderNode.Folder.Id)))
        {
            return;
        }

        foreach (var folderNode in folderNodes)
        {
            var folder = folderNode.Folder;
            if (targetFolder != null && IsFolderOrDescendant(targetFolder, folder.Id))
                continue;

            MoveFolderCore(folder, targetFolder, OrderedFolders(targetFolder, folder.Id).Count);
            moved = true;
        }

        var movedFolderIds = folderNodes.Select(node => node.Folder.Id).ToList();

        bool TravelsWithMovedFolder(string? folderId)
        {
            if (string.IsNullOrWhiteSpace(folderId))
                return false;

            return movedFolderIds.Any(movedId => IsFolderOrDescendant(folderId, movedId));
        }

        foreach (var profileNode in profileNodes)
        {
            var profile = profileNode.Profile;

            // Profiles that sit inside a folder moving along keep their place in that folder.
            if (TravelsWithMovedFolder(profile.FolderId))
                continue;

            MoveProfileCore(profile, targetFolder, OrderedProfiles(targetFolder, profile.Id).Count);
            moved = true;
        }

        if (!moved)
            return;

        LoadProfiles();
    }

    public IReadOnlyList<ProfileNodeViewModel> GetSelection()
    {
        var selected = EnumerateNodes(RootNodes).Where(node => node.IsSelected).ToList();
        if (selected.Count == 0 && SelectedNode != null)
            selected.Add(SelectedNode);

        return selected;
    }

    public IReadOnlyList<ProfileNodeViewModel> GetMoveSelection(ProfileNodeViewModel node)
    {
        var selected = GetSelection();
        return node.IsSelected && selected.Count > 1
            ? selected
            : new List<ProfileNodeViewModel> { node };
    }

    public void SelectAll()
    {
        foreach (var node in EnumerateNodes(RootNodes))
            node.IsSelected = true;
    }

    /// <summary>
    /// Builds "move to folder" entries for the given nodes; folders that belong to the
    /// dragged set (or live inside them) are skipped, because they cannot be their own target.
    /// </summary>
    public List<ProfileMoveTarget> BuildMoveTargets(IEnumerable<ProfileNodeViewModel> nodeSource)
    {
        var nodes = nodeSource.Distinct().ToList();
        var blockedFolderIds = nodes.OfType<ProfileFolderNodeViewModel>()
            .Select(node => node.Folder.Id)
            .ToList();

        var targets = new List<ProfileMoveTarget>
        {
            new(null, "Root", "— Root —", new RelayCommand(() => MoveNodesToFolder(nodes, null)))
        };

        void AddTargets(string? parentId, string prefix, int depth)
        {
            foreach (var folder in OrderedFolders(parentId))
            {
                if (blockedFolderIds.Any(blockedId => IsFolderOrDescendant(folder.Id, blockedId)))
                    continue;

                var path = string.IsNullOrEmpty(prefix) ? folder.Name : $"{prefix} / {folder.Name}";
                var folderId = folder.Id;
                targets.Add(new ProfileMoveTarget(
                    folderId,
                    folder.Name,
                    new string('\u2003', depth) + path,
                    new RelayCommand(() => MoveNodesToFolder(nodes, folderId))));

                AddTargets(folderId, path, depth + 1);
            }
        }

        AddTargets(null, string.Empty, 0);
        return targets;
    }

    private void MoveProfile(Profile profile, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        string? newFolderId;
        int insertIndex;

        if (position == ProfileDropPosition.RootEnd || target == null)
        {
            newFolderId = null;
            insertIndex = OrderedProfiles(null, profile.Id).Count;
        }
        else
        {
            newFolderId = ResolveProfileParentId(profile, target, position);
            var siblings = OrderedProfiles(newFolderId, profile.Id);
            if (position is ProfileDropPosition.Before or ProfileDropPosition.After
                && target is ProfileItemViewModel profileTarget)
            {
                var targetIndex = siblings.FindIndex(p => string.Equals(p.Id, profileTarget.Profile.Id, StringComparison.Ordinal));
                insertIndex = targetIndex < 0
                    ? siblings.Count
                    : position == ProfileDropPosition.Before ? targetIndex : targetIndex + 1;
            }
            else
            {
                insertIndex = siblings.Count;
            }
        }

        MoveProfileCore(profile, newFolderId, insertIndex);
    }

    private void MoveProfileCore(Profile profile, string? newFolderId, int insertIndex)
    {
        var oldFolderId = profile.FolderId;
        profile.FolderId = newFolderId;
        var list = OrderedProfiles(newFolderId, profile.Id);
        list.Insert(Math.Clamp(insertIndex, 0, list.Count), profile);
        for (var index = 0; index < list.Count; index++)
            list[index].SortOrder = index;

        var changed = new List<Profile>(list);
        if (!SameParent(oldFolderId, newFolderId))
        {
            var oldList = OrderedProfiles(oldFolderId, profile.Id);
            for (var index = 0; index < oldList.Count; index++)
                oldList[index].SortOrder = index;
            changed.AddRange(oldList);
        }

        _databaseService.UpdateProfilePlacements(changed);

        if (newFolderId != null)
            _folderExpansion[newFolderId] = true;
    }

    private void MoveFolder(ProfileFolder folder, ProfileNodeViewModel? target, ProfileDropPosition position)
    {
        string? newParentId;
        int insertIndex;

        if (position == ProfileDropPosition.RootEnd || target == null)
        {
            newParentId = null;
            insertIndex = OrderedFolders(null, folder.Id).Count;
        }
        else
        {
            newParentId = ResolveFolderParentId(folder, target, position);
            if (newParentId != null && IsFolderOrDescendant(newParentId, folder.Id))
                return;

            var siblings = OrderedFolders(newParentId, folder.Id);
            if (position is ProfileDropPosition.Before or ProfileDropPosition.After
                && target is ProfileFolderNodeViewModel folderTarget)
            {
                var targetIndex = siblings.FindIndex(f => string.Equals(f.Id, folderTarget.Folder.Id, StringComparison.Ordinal));
                insertIndex = targetIndex < 0
                    ? siblings.Count
                    : position == ProfileDropPosition.Before ? targetIndex : targetIndex + 1;
            }
            else
            {
                insertIndex = siblings.Count;
            }
        }

        MoveFolderCore(folder, newParentId, insertIndex);
    }

    private void MoveFolderCore(ProfileFolder folder, string? newParentId, int insertIndex)
    {
        var oldParentId = folder.ParentId;
        folder.ParentId = newParentId;
        var list = OrderedFolders(newParentId, folder.Id);
        list.Insert(Math.Clamp(insertIndex, 0, list.Count), folder);
        for (var index = 0; index < list.Count; index++)
            list[index].SortOrder = index;

        var changed = new List<ProfileFolder>(list);
        if (!SameParent(oldParentId, newParentId))
        {
            var oldList = OrderedFolders(oldParentId, folder.Id);
            for (var index = 0; index < oldList.Count; index++)
                oldList[index].SortOrder = index;
            changed.AddRange(oldList);
        }

        _databaseService.UpdateProfileFolderPlacements(changed);

        if (newParentId != null)
            _folderExpansion[newParentId] = true;
        _folderExpansion[folder.Id] = true;
    }

    private static string? ResolveFolderParentId(ProfileFolder dragged, ProfileNodeViewModel target, ProfileDropPosition position)
    {
        if (position == ProfileDropPosition.Inside)
            return ((ProfileFolderNodeViewModel)target).Folder.Id;

        return target switch
        {
            ProfileFolderNodeViewModel folderTarget => folderTarget.Folder.ParentId,
            ProfileItemViewModel profileTarget => profileTarget.Profile.FolderId,
            _ => null
        };
    }

    private static string? ResolveProfileParentId(Profile dragged, ProfileNodeViewModel target, ProfileDropPosition position)
    {
        if (position == ProfileDropPosition.Inside)
            return ((ProfileFolderNodeViewModel)target).Folder.Id;

        return target switch
        {
            ProfileItemViewModel profileTarget => profileTarget.Profile.FolderId,
            ProfileFolderNodeViewModel folderTarget => folderTarget.Folder.Id,
            _ => null
        };
    }

    private bool IsFolderOrDescendant(string candidateId, string ancestorId)
    {
        var currentId = candidateId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(currentId) && guard++ < 1000)
        {
            if (string.Equals(currentId, ancestorId, StringComparison.Ordinal))
                return true;

            if (!_foldersById.TryGetValue(currentId, out var current))
                return false;

            currentId = current.ParentId;
        }

        return false;
    }

    private async Task<string?> PromptForText(string title, string prompt, string initialValue, string watermark = "")
    {
        var editorVm = new TextInputViewModel(title, prompt, initialValue, watermark);
        var window = new TextInputWindow
        {
            DataContext = editorVm
        };

        var result = await window.ShowDialog<bool>(GetMainWindow());
        return result ? editorVm.Value.Trim() : null;
    }

    private async Task<bool> ShowConfirmation(string title, string message)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Yes", IsDefault = true },
                    new ButtonDefinition { Name = "No", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 400,
                MaxWidth = 600,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        var result = await box.ShowWindowDialogAsync(mainWindow!);
        return result == "Yes";
    }

    private async Task ShowInfo(string title, string message)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK", IsDefault = true } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 600,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(mainWindow!);
    }

    private void OnProfileRunningStateChanged(object? sender, ProfileRunningStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var profileVm = EnumerateProfileNodes().FirstOrDefault(p => p.Profile.Id == e.ProfileId);
            profileVm?.UpdateRunningStatus(e.IsRunning);
        });
    }
}

public abstract partial class ProfileNodeViewModel : ViewModelBase
{
    public ObservableCollection<ProfileNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public abstract string Name { get; }
    public int TotalProfileCount { get; internal set; }
    public bool IsFolder => this is ProfileFolderNodeViewModel;

    public void SetSelectedRecursive(bool value)
    {
        IsSelected = value;
        foreach (var child in Children)
            child.SetSelectedRecursive(value);
    }
}

public partial class ProfileFolderNodeViewModel : ProfileNodeViewModel
{
    private readonly ProfilesViewModel _parent;

    public ProfileFolder Folder { get; }

    public override string Name => Folder.Name;
    public bool HasProfiles => TotalProfileCount > 0;
    public string ProfileCountDisplay => TotalProfileCount.ToString();

    public ProfileFolderNodeViewModel(ProfileFolder folder, ProfilesViewModel parent)
    {
        Folder = folder;
        _parent = parent;
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        await _parent.RenameFolder(this);
    }

    [RelayCommand]
    private async Task NewSubfolderAsync()
    {
        await _parent.NewSubfolder(this);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await _parent.DeleteFolder(this);
    }
}

public partial class ProfileItemViewModel : ProfileNodeViewModel
{
    private readonly ProfilesViewModel _parent;
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private bool _isRunning;
    [ObservableProperty]
    private bool _isNotesExpanded;

    [ObservableProperty]
    private string _notesEdit = string.Empty;

    [ObservableProperty]
    private bool _isImportingCookies;

    [ObservableProperty]
    private bool _showProxy = true;

    [ObservableProperty]
    private bool _showNotes = true;

    [ObservableProperty]
    private bool _showTags = true;

    public ObservableCollection<TagChipViewModel> TagChips { get; } = new();
    public bool HasTags => TagChips.Count > 0;

    public Profile Profile { get; }

    public override string Name => Profile.Name;

    public string ProxyDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Profile.ProxyId))
                return "No proxy";

            var proxy = _databaseService.GetProxy(Profile.ProxyId);
            return proxy?.Name ?? "Unknown proxy";
        }
    }

    public string ProxyToolTip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Profile.ProxyId))
                return "No proxy";

            var proxy = _databaseService.GetProxy(Profile.ProxyId);
            if (proxy == null)
                return "Unknown proxy";

            return $"{proxy.Name}\n{proxy.Type.ToUpperInvariant()} {proxy.Host}:{proxy.Port}";
        }
    }

    public string NotesDisplay => TextSanitizer.HtmlToPlainText(Profile.Notes);
    public bool HasNotes => !string.IsNullOrWhiteSpace(NotesDisplay);
    public bool IsNotesCollapsed => !IsNotesExpanded;
    public string NotesToggleIcon => IsNotesExpanded ? "\uE70E" : "\uE70D";
    public string NotesToggleTip => IsNotesExpanded ? "Collapse notes" : "Expand notes";
    private OsOption OsOption => OsOption.FromId(Profile.FingerprintConfig.Os);
    public string OsIconData => OsOption.IconData;
    public string OsIconFill => OsOption.IconFill;
    public double OsIconBoxSize => OsOption.Id == "linux" ? 16 : 17;
    public string OsIconTip => OsOption.DisplayName;

    public string StatusIcon => IsRunning ? "🟢" : "⚫";
    public bool IsNotRunning => !IsRunning;
    public bool IsRunningActionVisible => IsRunning && !IsImportingCookies;
    public bool IsStartActionVisible => !IsRunning && !IsImportingCookies;

    public ProfileItemViewModel(Profile profile, ProfilesViewModel parent, DatabaseService databaseService, IEnumerable<Tag>? tags = null, bool showProxy = true, bool showNotes = true, bool showTags = true)
    {
        Profile = profile;
        _parent = parent;
        _databaseService = databaseService;
        _showProxy = showProxy;
        _showNotes = showNotes;
        _showTags = showTags;

        _notesEdit = TextSanitizer.HtmlToPlainText(profile.Notes);

        if (tags != null)
        {
            foreach (var tag in tags)
                TagChips.Add(new TagChipViewModel(tag));
        }
    }

    public void SetViewOptions(bool showProxy, bool showNotes, bool showTags)
    {
        ShowProxy = showProxy;
        ShowNotes = showNotes;
        ShowTags = showTags;
    }

    public void SetTags(IEnumerable<Tag> tags)
    {
        TagChips.Clear();
        foreach (var tag in tags)
            TagChips.Add(new TagChipViewModel(tag));

        OnPropertyChanged(nameof(HasTags));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await _parent.StartProfileAsync(this);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _parent.StopProfileAsync(this);
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        await _parent.EditProfile(this);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        await _parent.RenameProfile(this);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await _parent.DeleteProfile(this);
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        await _parent.CloneProfile(this);
    }

    [RelayCommand]
    private async Task ExportCookies()
    {
        await _parent.ExportCookies(this);
    }

    [RelayCommand]
    private async Task ImportCookies()
    {
        await _parent.ImportCookies(this);
    }

    [RelayCommand]
    private async Task ViewLog()
    {
        await _parent.OpenLog(this);
    }

    [RelayCommand]
    private Task Passkeys()
    {
        return _parent.OpenPasskeys(this);
    }

    [RelayCommand]
    private void ToggleNotes()
    {
        IsNotesExpanded = !IsNotesExpanded;
    }

    partial void OnNotesEditChanged(string value)
    {
        var normalized = value ?? string.Empty;
        if (string.Equals(Profile.Notes ?? string.Empty, normalized, StringComparison.Ordinal))
            return;

        Profile.Notes = string.IsNullOrWhiteSpace(normalized) ? null : normalized;

        try
        {
            _databaseService.UpdateProfileNotes(Profile.Id, Profile.Notes);
        }
        catch
        {
            // Keep the in-memory note even if persisting it fails; the next edit retries.
        }

        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(HasNotes));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(IsRunningActionVisible));
        OnPropertyChanged(nameof(IsStartActionVisible));
    }

    partial void OnIsNotesExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotesCollapsed));
        OnPropertyChanged(nameof(NotesToggleIcon));
        OnPropertyChanged(nameof(NotesToggleTip));
    }

    partial void OnIsImportingCookiesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRunningActionVisible));
        OnPropertyChanged(nameof(IsStartActionVisible));
    }

    public void UpdateRunningStatus(bool isRunning)
    {
        IsRunning = isRunning;
    }
}

public enum ProfileDropPosition
{
    Before,
    After,
    Inside,
    RootEnd
}

public sealed class ProfileMoveTarget
{
    public ProfileMoveTarget(string? folderId, string name, string display, IRelayCommand command)
    {
        FolderId = folderId;
        Name = name;
        Display = display;
        Command = command;
    }

    public string? FolderId { get; }
    public string Name { get; }
    public string Display { get; }
    public IRelayCommand Command { get; }
}

public sealed class TagChipViewModel
{
    public TagChipViewModel(Tag tag)
    {
        Name = tag.Name;
        Glyph = TagIconCatalog.GlyphFor(tag.Icon);
        Color = tag.Color;
        Brush = new SolidColorBrush(Avalonia.Media.Color.Parse(tag.Color));
    }

    public string Name { get; }
    public string Glyph { get; }
    public string Color { get; }
    public IBrush Brush { get; }
}

public partial class TagFilterItemViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    public TagFilterItemViewModel(Tag tag, bool isSelected, Action selectionChanged)
    {
        TagId = tag.Id;
        Name = tag.Name;
        Glyph = TagIconCatalog.GlyphFor(tag.Icon);
        Brush = new SolidColorBrush(Avalonia.Media.Color.Parse(tag.Color));
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public string TagId { get; }
    public string Name { get; }
    public string Glyph { get; }
    public IBrush Brush { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        _selectionChanged();
    }
}

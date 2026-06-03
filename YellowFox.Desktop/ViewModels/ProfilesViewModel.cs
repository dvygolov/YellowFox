using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
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
    
    [ObservableProperty]
    private string _searchText = string.Empty;
    
    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;
    
    [ObservableProperty]
    private int _selectedCount;
    
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();
    
    public bool HasSelection => SelectedCount > 0;
    
    public ProfilesViewModel(DatabaseService databaseService, BrowserService browserService)
    {
        _databaseService = databaseService;
        _browserService = browserService;
        _browserService.ProfileRunningStateChanged += OnProfileRunningStateChanged;
        LoadProfiles();
    }
    
    partial void OnSearchTextChanged(string value)
    {
        FilterProfiles();
    }
    
    private void LoadProfiles()
    {
        Profiles.Clear();
        var profiles = _databaseService.GetAllProfiles();
        
        foreach (var profile in profiles)
        {
            AddProfileItem(profile);
        }
        
        UpdateSelectedCount();
    }
    
    private void OnProfileItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemViewModel.IsSelected))
        {
            UpdateSelectedCount();
        }
    }
    
    private void UpdateSelectedCount()
    {
        SelectedCount = Profiles.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
    }
    
    private void FilterProfiles()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            LoadProfiles();
            return;
        }
        
        var filtered = _databaseService.GetAllProfiles()
            .Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        Profiles.Clear();
        foreach (var profile in filtered)
        {
            AddProfileItem(profile);
        }

        UpdateSelectedCount();
    }

    private void AddProfileItem(Profile profile)
    {
        var vm = new ProfileItemViewModel(profile, this, _databaseService);
        vm.UpdateRunningStatus(_browserService.IsRunning(profile.Id));
        vm.PropertyChanged += OnProfileItemPropertyChanged;
        Profiles.Add(vm);
    }
    
    [RelayCommand]
    private async Task NewProfile()
    {
        var editorVm = new ProfileEditorViewModel(_databaseService, null);
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
    
    [RelayCommand]
    private void Refresh()
    {
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
        var editorVm = new ProfileEditorViewModel(_databaseService, profileVm.Profile);
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
            _databaseService.DeleteProfile(profileVm.Profile.Id);
            profileVm.PropertyChanged -= OnProfileItemPropertyChanged;
            Profiles.Remove(profileVm);
            UpdateSelectedCount();
        }
    }
    
    public async Task CloneProfile(ProfileItemViewModel profileVm)
    {
        var editorVm = new ProfileEditorViewModel(_databaseService, profileVm.Profile, isCloneMode: true);
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
        var selected = Profiles.Where(p => p.IsSelected && !p.IsRunning).ToList();
        foreach (var profile in selected)
        {
            await StartProfileAsync(profile);
        }
    }
    
    [RelayCommand]
    private async Task DeleteAllSelected()
    {
        var selected = Profiles.Where(p => p.IsSelected).ToList();
        
        var result = await ShowConfirmation(
            "Delete Profiles",
            $"Are you sure you want to delete {selected.Count} profile(s)?");
        
        if (result)
        {
            foreach (var profile in selected)
            {
                _databaseService.DeleteProfile(profile.Profile.Id);
                profile.PropertyChanged -= OnProfileItemPropertyChanged;
                Profiles.Remove(profile);
            }
            UpdateSelectedCount();
        }
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
            var profileVm = Profiles.FirstOrDefault(p => p.Profile.Id == e.ProfileId);
            profileVm?.UpdateRunningStatus(e.IsRunning);
        });
    }
}

public partial class ProfileItemViewModel : ViewModelBase
{
    private readonly ProfilesViewModel _parent;
    private readonly DatabaseService _databaseService;
    
    [ObservableProperty]
    private bool _isRunning;
    
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isNotesExpanded;

    [ObservableProperty]
    private bool _isImportingCookies;
    
    public Profile Profile { get; }
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
    
    public ProfileItemViewModel(Profile profile, ProfilesViewModel parent, DatabaseService databaseService)
    {
        Profile = profile;
        _parent = parent;
        _databaseService = databaseService;
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
    private void ToggleNotes()
    {
        if (HasNotes)
            IsNotesExpanded = !IsNotesExpanded;
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

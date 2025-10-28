using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
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
            var vm = new ProfileItemViewModel(profile, this);
            vm.PropertyChanged += OnProfileItemPropertyChanged;
            Profiles.Add(vm);
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
            var vm = new ProfileItemViewModel(profile, this);
            Profiles.Add(vm);
        }
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
                WindowIcon = mainWindow?.Icon,
                MinWidth = 400,
                MaxWidth = 600,
                SizeToContent = SizeToContent.WidthAndHeight
            });
        
        var result = await box.ShowWindowDialogAsync(mainWindow);
        return result == "Yes";
    }
}

public partial class ProfileItemViewModel : ViewModelBase
{
    private readonly ProfilesViewModel _parent;
    
    [ObservableProperty]
    private bool _isRunning;
    
    [ObservableProperty]
    private bool _isSelected;
    
    public Profile Profile { get; }
    
    public string StatusIcon => IsRunning ? "🟢" : "⚫";
    
    public ProfileItemViewModel(Profile profile, ProfilesViewModel parent)
    {
        Profile = profile;
        _parent = parent;
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
    
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusIcon));
    }
    
    public void UpdateRunningStatus(bool isRunning)
    {
        IsRunning = isRunning;
    }
}

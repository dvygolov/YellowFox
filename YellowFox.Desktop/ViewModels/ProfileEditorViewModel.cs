using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private readonly Profile? _existingProfile;
    private readonly bool _isCloneMode;
    
    [ObservableProperty]
    private string _name = string.Empty;
    
    [ObservableProperty]
    private string _notes = string.Empty;
    
    [ObservableProperty]
    private string _selectedOs = string.Empty;
    
    [ObservableProperty]
    private ScreenPreset? _selectedScreenPreset;
    
    public ObservableCollection<string> OsOptions { get; } = new()
    {
        "windows",
        "macos",
        "linux"
    };
    
    public ObservableCollection<ScreenPreset> ScreenPresets { get; } = new(ScreenPreset.Presets);
    
    public bool IsEditMode => _existingProfile != null && !_isCloneMode;
    public string Title => _isCloneMode ? "Clone Profile" : (IsEditMode ? "Edit Profile" : "New Profile");
    
    public ProfileEditorViewModel(DatabaseService databaseService, Profile? existingProfile, bool isCloneMode = false)
    {
        _databaseService = databaseService;
        _existingProfile = existingProfile;
        _isCloneMode = isCloneMode;
        
        if (existingProfile != null)
        {
            // For clone mode, append " (Copy)" to the name
            Name = _isCloneMode ? $"{existingProfile.Name} (Copy)" : existingProfile.Name;
            Notes = existingProfile.Notes ?? string.Empty;
            SelectedOs = existingProfile.FingerprintConfig.Os;
            
            var preset = ScreenPreset.Presets.FirstOrDefault(p =>
                p.Width == existingProfile.FingerprintConfig.Screen.MaxWidth &&
                p.Height == existingProfile.FingerprintConfig.Screen.MaxHeight);
            SelectedScreenPreset = preset ?? ScreenPreset.Presets[0];
        }
        else
        {
            // Default values
            SelectedOs = GetDefaultOs();
            SelectedScreenPreset = ScreenPreset.Presets[0];
        }
        
    }
    
    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return;
        
        if (SelectedScreenPreset == null)
            return;
        
        // For clone mode, always create a new profile
        var profile = (_existingProfile != null && !_isCloneMode) ? _existingProfile : new Profile();
        profile.Name = Name.Trim();
        profile.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        profile.FingerprintConfig = new FingerprintConfig
        {
            Os = SelectedOs,
            Screen = new ScreenConfig
            {
                MaxWidth = SelectedScreenPreset.Width,
                MaxHeight = SelectedScreenPreset.Height
            }
        };
        
        if (_existingProfile != null && !_isCloneMode)
        {
            _databaseService.UpdateProfile(profile);
        }
        else
        {
            _databaseService.CreateProfile(profile);
        }
    }
    
    [RelayCommand]
    private void Cancel()
    {
        // Dialog will handle closing
    }
    
    private static string GetDefaultOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "windows";
    }
}

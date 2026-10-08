using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class PasskeysViewModel : ViewModelBase
{
    private readonly PasskeyStoreService _passkeyStore;
    private readonly string _profileDirectory;
    private readonly Func<Window?> _ownerProvider;

    public string ProfileId { get; }
    public string ProfileName { get; }
    public string StorePath { get; }

    public ObservableCollection<ProfilePasskey> Passkeys { get; } = new();

    [ObservableProperty]
    private ProfilePasskey? _selectedPasskey;

    [ObservableProperty]
    private bool _profileRunning;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string Title => $"Passkeys — {ProfileName}";
    public bool HasPasskeys => Passkeys.Count > 0;
    public bool IsEmpty => Passkeys.Count == 0;
    public bool CanDelete => SelectedPasskey != null && !ProfileRunning;
    public bool IsRunning => ProfileRunning;
    public string CountDisplay => Passkeys.Count == 1 ? "1 passkey" : $"{Passkeys.Count} passkeys";
    public string RunningHint =>
        "The profile is running. Stop it before deleting passkeys — the browser keeps its own copy until then.";

    public event Action? CloseRequested;

    public PasskeysViewModel(
        PasskeyStoreService passkeyStore,
        string profileId,
        string profileName,
        string profileDirectory,
        bool profileRunning,
        Func<Window?> ownerProvider)
    {
        _passkeyStore = passkeyStore;
        _profileDirectory = profileDirectory;
        _ownerProvider = ownerProvider;
        ProfileId = profileId;
        ProfileName = profileName;
        StorePath = passkeyStore.GetStorePath(profileDirectory);
        _profileRunning = profileRunning;
        LoadPasskeys();
    }

    partial void OnSelectedPasskeyChanged(ProfilePasskey? value)
    {
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnProfileRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsRunning));
    }

    [RelayCommand]
    private void Refresh() => LoadPasskeys();

    private void LoadPasskeys()
    {
        SelectedPasskey = null;
        Passkeys.Clear();

        foreach (var passkey in _passkeyStore.GetPasskeys(_profileDirectory))
            Passkeys.Add(passkey);

        OnPropertyChanged(nameof(HasPasskeys));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountDisplay));
        StatusMessage = Passkeys.Count == 0
            ? "No passkeys stored for this profile yet."
            : $"Store: {StorePath}";
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var target = SelectedPasskey;
        if (target == null)
            return;

        if (ProfileRunning)
        {
            await ShowInfo("Delete Passkey", "Stop the profile before deleting passkeys.");
            return;
        }

        var confirmed = await ShowConfirmation(
            "Delete Passkey",
            $"Delete the passkey for “{target.RpIdDisplay}”? This cannot be undone.");
        if (!confirmed)
            return;

        if (_passkeyStore.DeletePasskey(_profileDirectory, target.CredentialId))
        {
            LoadPasskeys();
            StatusMessage = $"Deleted passkey for {target.RpIdDisplay}.";
        }
        else
        {
            StatusMessage = "Passkey was not found in the store.";
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var owner = _ownerProvider();
        if (owner == null)
            return;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export passkeys: {ProfileName}",
            SuggestedFileName = $"{ProfileName}-passkeys.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });

        if (file == null)
            return;

        try
        {
            var store = _passkeyStore.LoadStore(_profileDirectory);
            await File.WriteAllTextAsync(file.Path.LocalPath, _passkeyStore.SerializeForExport(store));
            await ShowInfo("Passkeys Export", $"Exported {store.Credentials.Count} passkey(s).");
        }
        catch (Exception ex)
        {
            await ShowInfo("Export Error", ex.Message);
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private async Task<bool> ShowConfirmation(string title, string message)
    {
        var owner = _ownerProvider();
        var box = MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
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

        var result = await box.ShowWindowDialogAsync(owner!);
        return result == "Yes";
    }

    private async Task ShowInfo(string title, string message)
    {
        var owner = _ownerProvider();
        var box = MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
        {
            ContentTitle = title,
            ContentMessage = message,
            ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK", IsDefault = true } },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 360,
            MaxWidth = 600,
            SizeToContent = SizeToContent.WidthAndHeight
        });

        await box.ShowWindowDialogAsync(owner!);
    }
}

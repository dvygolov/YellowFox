using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

public partial class ExtensionsViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private readonly ExtensionStorageService _extensionStorageService;

    [ObservableProperty]
    private ExtensionItemViewModel? _selectedExtension;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _extensionPath = string.Empty;

    [ObservableProperty]
    private bool _isExtensionEnabled = true;

    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = new();
    public bool HasSelection => SelectedExtension != null;
    public bool IsEditMode => SelectedExtension != null;
    public string FormTitle => IsEditMode ? "Edit Extension" : "New Extension";

    public ExtensionsViewModel(DatabaseService databaseService, ExtensionStorageService extensionStorageService)
    {
        _databaseService = databaseService;
        _extensionStorageService = extensionStorageService;
        Load();
    }

    partial void OnSelectedExtensionChanged(ExtensionItemViewModel? value)
    {
        if (value == null)
        {
            ResetForm();
        }
        else
        {
            Name = value.Extension.Name;
            ExtensionPath = value.Extension.Path;
            IsExtensionEnabled = value.Extension.IsEnabled;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(FormTitle));
    }

    [RelayCommand]
    private void Refresh()
    {
        Load();
        StatusMessage = "Refreshed";
    }

    [RelayCommand]
    private void NewExtension()
    {
        SelectedExtension = null;
        ResetForm();
    }

    [RelayCommand]
    private async Task ImportArchive()
    {
        var mainWindow = GetMainWindow();
        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Extension Archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Extension Archive") { Patterns = new[] { "*.zip", "*.xpi" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            var extension = _extensionStorageService.ImportArchive(file.Path.LocalPath, fileName);
            StatusMessage = $"Imported: {extension.Name}";
            Load();
            SelectedExtension = Extensions.FirstOrDefault(e => e.Extension.Id == extension.Id);
        }
        catch (Exception ex)
        {
            await ShowMessage("Import Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task BrowseExtensionFile()
    {
        var mainWindow = GetMainWindow();
        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Extension Archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Extension Archive") { Patterns = new[] { "*.zip", "*.xpi" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
            return;

        ExtensionPath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(Name))
            Name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!ValidateForm(out var validationError))
        {
            await ShowMessage("Validation", validationError);
            return;
        }

        try
        {
            if (SelectedExtension == null)
            {
                var extension = new ExtensionItem
                {
                    Name = Name.Trim(),
                    IsEnabled = IsExtensionEnabled
                };

                extension.Path = _extensionStorageService.IsArchivePath(ExtensionPath.Trim())
                    ? _extensionStorageService.StoreArchive(ExtensionPath.Trim(), extension.Id)
                    : ExtensionPath.Trim();

                _databaseService.CreateExtension(extension);
                StatusMessage = $"Created: {extension.Name}";
            }
            else
            {
                var extension = SelectedExtension.Extension;
                extension.Name = Name.Trim();
                extension.Path = _extensionStorageService.IsArchivePath(ExtensionPath.Trim())
                    ? _extensionStorageService.StoreArchive(ExtensionPath.Trim(), extension.Id)
                    : ExtensionPath.Trim();
                extension.IsEnabled = IsExtensionEnabled;
                _databaseService.UpdateExtension(extension);
                StatusMessage = $"Updated: {extension.Name}";
            }

            Load();
            SelectedExtension = null;
            ResetForm();
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if (SelectedExtension == null)
            return;

        var confirmed = await ConfirmDelete(SelectedExtension.Extension.Name);
        if (!confirmed)
            return;

        _extensionStorageService.DeleteExtensionWithFiles(SelectedExtension.Extension);
        StatusMessage = $"Deleted: {SelectedExtension.Extension.Name}";
        Load();
        SelectedExtension = null;
    }

    [RelayCommand]
    private void ToggleEnabled()
    {
        if (SelectedExtension == null)
            return;

        var ext = SelectedExtension.Extension;
        ext.IsEnabled = !ext.IsEnabled;
        _databaseService.UpdateExtension(ext);
        StatusMessage = $"{ext.Name}: {(ext.IsEnabled ? "enabled" : "disabled")}";
        Load();
    }

    private bool ValidateForm(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            validationError = "Name is required.";
            return false;
        }

        var path = ExtensionPath.Trim();
        if (!BrowserService.IsExtensionPathUsable(path) && !_extensionStorageService.IsArchivePath(path))
        {
            validationError = "Path must point to an unpacked extension folder with manifest.json, or to a .zip/.xpi archive.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    private void ResetForm()
    {
        Name = string.Empty;
        ExtensionPath = string.Empty;
        IsExtensionEnabled = true;
    }

    private void Load()
    {
        Extensions.Clear();
        foreach (var extension in _databaseService.GetAllExtensions())
        {
            Extensions.Add(new ExtensionItemViewModel(extension));
        }
    }

    private async Task<bool> ConfirmDelete(string name)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Delete Extension",
                ContentMessage = $"Delete extension '{name}'?",
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Yes", IsDefault = true },
                    new ButtonDefinition { Name = "No", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        var result = await box.ShowWindowDialogAsync(mainWindow!);
        return result == "Yes";
    }

    private async Task ShowMessage(string title, string message)
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
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(mainWindow!);
    }

    private Window GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow!
            : throw new InvalidOperationException("Main window not found");
    }
}

public class ExtensionItemViewModel : ViewModelBase
{
    public ExtensionItem Extension { get; }
    public string Name => Extension.Name;
    public string Path => Extension.Path;
    public string Status => Extension.IsEnabled ? "Enabled" : "Disabled";

    public ExtensionItemViewModel(ExtensionItem extension)
    {
        Extension = extension;
    }
}

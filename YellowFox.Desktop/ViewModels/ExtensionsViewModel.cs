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

    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = new();
    public bool HasSelection => SelectedExtension != null;

    public ExtensionsViewModel(DatabaseService databaseService, ExtensionStorageService extensionStorageService)
    {
        _databaseService = databaseService;
        _extensionStorageService = extensionStorageService;
        Load();
    }

    partial void OnSelectedExtensionChanged(ExtensionItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void Refresh()
    {
        Load();
        StatusMessage = "Refreshed";
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
            var fileName = Path.GetFileNameWithoutExtension(file.Name);
            var extension = _extensionStorageService.ImportArchive(file.Path.LocalPath, fileName);
            StatusMessage = $"Imported: {extension.Name}";
            Load();
        }
        catch (Exception ex)
        {
            await ShowMessage("Import Error", ex.Message);
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

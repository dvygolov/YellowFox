using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class ExtensionEditorWindow : Window
{
    public ExtensionEditorWindow()
    {
        InitializeComponent();
    }

    private async void BrowseFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionEditorViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

        vm.ExtensionPath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(vm.Name))
            vm.Name = Path.GetFileNameWithoutExtension(file.Name);
    }

    private async void BrowseFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionEditorViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Extension Folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null)
            return;

        vm.ExtensionPath = folder.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(vm.Name))
            vm.Name = Path.GetFileName(folder.Path.LocalPath);
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionEditorViewModel vm)
            return;

        if (!vm.TryValidate(out var error))
        {
            await ShowError(error);
            return;
        }

        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async Task ShowError(string message)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Error",
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK" } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(this);
    }
}

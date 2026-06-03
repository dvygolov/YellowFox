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

public partial class CookieImportWindow : Window
{
    public CookieImportWindow()
    {
        InitializeComponent();
    }

    private async void LoadFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CookieImportViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load cookies",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cookie files") { Patterns = new[] { "*.json", "*.txt", "*.cookie", "*.cookies", "*" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            vm.CookieText = await File.ReadAllTextAsync(file.Path.LocalPath);
        }
        catch (IOException ex)
        {
            await ShowError($"Failed to read file: {ex.Message}");
        }
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CookieImportViewModel vm)
            return;

        if (string.IsNullOrWhiteSpace(vm.CookieText))
        {
            await ShowError("Paste cookies or load them from a file.");
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
                ContentTitle = "Import Cookies",
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

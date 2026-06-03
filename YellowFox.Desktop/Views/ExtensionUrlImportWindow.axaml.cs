using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;

namespace YellowFox.Desktop.Views;

public partial class ExtensionUrlImportWindow : Window
{
    public string ExtensionUrl { get; private set; } = string.Empty;

    public ExtensionUrlImportWindow()
    {
        InitializeComponent();
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = UrlTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            await ShowError("URL is required.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            await ShowError("Enter a valid URL.");
            return;
        }

        ExtensionUrl = url;
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

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class ProxyEditorWindow : Window
{
    public ProxyEditorWindow()
    {
        InitializeComponent();
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProxyEditorViewModel vm)
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

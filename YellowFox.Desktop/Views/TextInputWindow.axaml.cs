using Avalonia.Controls;
using Avalonia.Interactivity;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class TextInputWindow : Window
{
    public TextInputWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextInputViewModel { HasValue: false })
            return;

        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}

using Avalonia.Controls;
using Avalonia.Input;

namespace YellowFox.Desktop.Views;

public partial class ExtensionsView : UserControl
{
    public ExtensionsView()
    {
        InitializeComponent();
        ExtensionsList.AddHandler(InputElement.KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not ViewModels.ExtensionsViewModel vm)
            return;

        vm.DeleteSelectedCommand.Execute(null);
        e.Handled = true;
    }
}

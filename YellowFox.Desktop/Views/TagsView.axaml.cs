using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace YellowFox.Desktop.Views;

public partial class TagsView : UserControl
{
    public TagsView()
    {
        InitializeComponent();
        TagsList.AddHandler(InputElement.KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not ViewModels.TagsViewModel vm)
            return;

        vm.DeleteTagCommand.Execute(null);
        e.Handled = true;
    }
}

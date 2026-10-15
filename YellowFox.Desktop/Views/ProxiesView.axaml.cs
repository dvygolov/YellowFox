using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class ProxiesView : UserControl
{
    public ProxiesView()
    {
        InitializeComponent();
        ProxiesList.SelectionChanged += OnSelectionChanged;
        ProxiesList.AddHandler(InputElement.PointerPressedEvent, OnProxiesListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ProxiesList.ContextRequested += OnProxiesListContextRequested;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ProxiesViewModel vm)
            vm.SyncSelection(ProxiesList.SelectedItems.Cast<ProxyItemViewModel>());
    }

    // Right-click selects the row under the cursor before the context menu opens.
    // If the row is already part of a multi-selection, the selection is kept.
    private void OnProxiesListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(ProxiesList);
        if (!point.Properties.IsRightButtonPressed)
            return;

        var row = RowUnder(e.Source);
        if (row?.DataContext is ProxyItemViewModel item && !ProxiesList.SelectedItems.Contains(item))
            ProxiesList.SelectedItem = item;
    }

    // Suppress the context menu when the right-click lands outside of any row.
    private void OnProxiesListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (RowUnder(e.Source) == null)
            e.Handled = true;
    }

    private static DataGridRow? RowUnder(object? source)
    {
        return (source as Visual)?.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
    }

    // The status-filter header uses a TextBlock (a Button inside a DataGrid column
    // header collapses to zero width in Avalonia), so the menu is opened here.
    private void OnFilterHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control target || DataContext is not ProxiesViewModel vm)
            return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Items.Add(CreateFilterItem("All proxies", "All", "ProxyFilterAllItem", vm));
        flyout.Items.Add(CreateFilterItem("✓ Checked only", "Valid", "ProxyFilterValidItem", vm));
        flyout.Items.Add(CreateFilterItem("✗ Failed only", "Invalid", "ProxyFilterInvalidItem", vm));
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private static MenuItem CreateFilterItem(string header, string parameter, string automationId, ProxiesViewModel vm)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = vm.SetStatusFilterCommand,
            CommandParameter = parameter
        };
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }
}
